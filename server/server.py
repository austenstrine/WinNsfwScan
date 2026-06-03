"""EraX NSFW Detection Server
FastAPI + ONNX Runtime inference server for the EraX YOLO11 NSFW model.

Accepts a pre-letterboxed 640×640 BGRA frame via POST /detect_raw.
All image preprocessing (resize + pad) is performed in C# using SkiaSharp
before this server is called, so Python only handles float normalisation,
ONNX inference, and NMS.
"""

import argparse
import asyncio
from contextlib import asynccontextmanager
import datetime
import os
import socket
import sys
import traceback

import numpy as np
from fastapi import FastAPI, HTTPException, Request
import uvicorn
import onnxruntime as ort

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

CLASS_NAMES = ['anus', 'make_love', 'nipple', 'penis', 'vagina']
_MODEL_SIZE = 640   # YOLO11 native input resolution


# ---------------------------------------------------------------------------
# Utilities
# ---------------------------------------------------------------------------

def _base_path() -> str:
    if getattr(sys, 'frozen', False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


class Logger:
    def __init__(self, base: str):
        local = os.getenv('LOCALAPPDATA')
        log_dir = os.path.join(local, 'WinNsfwScan', 'logs') if local else os.path.join(base, 'logs')
        os.makedirs(log_dir, exist_ok=True)
        stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
        self._path = os.path.join(log_dir, f'server-{stamp}.log')

    def write(self, msg: str) -> None:
        ts = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]
        with open(self._path, 'a', encoding='utf-8') as f:
            f.write(f'{ts} {msg}\n')


# ---------------------------------------------------------------------------
# Image post-processing (no pre-processing — C# handles letterbox)
# ---------------------------------------------------------------------------

def _nms(boxes_xyxy: np.ndarray, scores: np.ndarray, iou_thresh: float) -> list:
    x1, y1, x2, y2 = boxes_xyxy[:, 0], boxes_xyxy[:, 1], boxes_xyxy[:, 2], boxes_xyxy[:, 3]
    areas = (x2 - x1) * (y2 - y1)
    order = scores.argsort()[::-1]
    keep = []
    while len(order):
        i = order[0]
        keep.append(int(i))
        ix1 = np.maximum(x1[i], x1[order[1:]])
        iy1 = np.maximum(y1[i], y1[order[1:]])
        ix2 = np.minimum(x2[i], x2[order[1:]])
        iy2 = np.minimum(y2[i], y2[order[1:]])
        inter = np.maximum(0.0, ix2 - ix1) * np.maximum(0.0, iy2 - iy1)
        iou = inter / (areas[i] + areas[order[1:]] - inter + 1e-7)
        order = order[np.where(iou <= iou_thresh)[0] + 1]
    return keep


def _decode(outputs, conf_thresh: float, iou_thresh: float) -> list:
    """Decode YOLO11 output tensor [1, 4+C, 8400] to detection dicts.

    Boxes are already in 640×640 letterbox space (C# pre-processed).
    Box format in result: [x, y, w, h] in 640px pixels (top-left origin).
    """
    preds = outputs[0][0].T          # [8400, 4+C]
    cls_scores = preds[:, 4:]
    max_scores = cls_scores.max(axis=1)
    class_ids  = cls_scores.argmax(axis=1)

    mask = max_scores >= conf_thresh
    if not mask.any():
        return []

    cx = preds[mask, 0]
    cy = preds[mask, 1]
    bw = preds[mask, 2]
    bh = preds[mask, 3]
    scores = max_scores[mask]
    ids    = class_ids[mask]

    x1 = np.clip(cx - bw / 2, 0, _MODEL_SIZE)
    y1 = np.clip(cy - bh / 2, 0, _MODEL_SIZE)
    x2 = np.clip(cx + bw / 2, 0, _MODEL_SIZE)
    y2 = np.clip(cy + bh / 2, 0, _MODEL_SIZE)

    keep = _nms(np.stack([x1, y1, x2, y2], axis=1), scores, iou_thresh)

    return [
        {
            'class': CLASS_NAMES[int(ids[i])] if int(ids[i]) < len(CLASS_NAMES) else str(int(ids[i])),
            'score': float(scores[i]),
            'box':   [int(x1[i]), int(y1[i]), int(x2[i] - x1[i]), int(y2[i] - y1[i])],
        }
        for i in keep
    ]


# ---------------------------------------------------------------------------
# Detector
# ---------------------------------------------------------------------------

class Detector:
    def __init__(self, model_path: str, resolution: int, provider_pref: str,
                 conf_thresh: float, iou_thresh: float):
        self.resolution  = resolution
        self.conf_thresh = conf_thresh
        self.iou_thresh  = iou_thresh

        opts = ort.SessionOptions()
        opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        # Limit CPU threads per process — we run several processes in parallel.
        opts.intra_op_num_threads = 2
        opts.inter_op_num_threads = 1

        self.providers = self._resolve_providers(provider_pref)
        self.session   = ort.InferenceSession(model_path, sess_options=opts,
                                               providers=self.providers)
        self.active_providers = self.session.get_providers()
        self.input_name = self.session.get_inputs()[0].name

        # Warm-up: one dummy inference so the first real request is not penalised.
        dummy = np.zeros((1, 3, resolution, resolution), dtype=np.float32)
        self.session.run(None, {self.input_name: dummy})

    @staticmethod
    def _resolve_providers(pref: str) -> list:
        available = ort.get_available_providers()
        order = {
            'cpu':       ['CPUExecutionProvider'],
            'cuda':      ['CUDAExecutionProvider', 'CPUExecutionProvider'],
            'directml':  ['DmlExecutionProvider',  'CPUExecutionProvider'],
        }.get(pref, ['DmlExecutionProvider', 'CUDAExecutionProvider', 'CPUExecutionProvider'])
        resolved = [p for p in order if p in available]
        if 'CPUExecutionProvider' not in resolved:
            resolved.append('CPUExecutionProvider')
        return resolved

    def run(self, img_bgr: np.ndarray) -> list:
        # img_bgr is already 640×640 (letterboxed by C#).
        # BGR → RGB, HWC → NCHW, normalise to [0, 1].
        blob = (img_bgr[:, :, ::-1].astype(np.float32) / 255.0).transpose(2, 0, 1)[np.newaxis]
        outputs = self.session.run(None, {self.input_name: blob})
        return _decode(outputs, self.conf_thresh, self.iou_thresh)


# ---------------------------------------------------------------------------
# Startup
# ---------------------------------------------------------------------------

def _parse_args():
    p = argparse.ArgumentParser()
    p.add_argument('--model',              default='erax_nsfw_yolo11m.onnx')
    p.add_argument('--resolution',  type=int,   default=640)
    p.add_argument('--port',        type=int,   default=0)
    p.add_argument('--conf',        type=float, default=0.25)
    p.add_argument('--iou',         type=float, default=0.45)
    p.add_argument('--execution-provider', default='auto',
                   choices=['auto', 'directml', 'cuda', 'cpu'])
    args, _ = p.parse_known_args()
    return args


args     = _parse_args()
base     = _base_path()
log      = Logger(base)
detector = Detector(
    model_path    = os.path.join(base, args.model),
    resolution    = args.resolution,
    provider_pref = args.execution_provider,
    conf_thresh   = args.conf,
    iou_thresh    = args.iou,
)
log.write(
    f'startup model={args.model} resolution={args.resolution} '
    f'conf={args.conf} iou={args.iou} '
    f'providers={detector.active_providers}'
)

# Port is resolved in __main__ and stored here so the lifespan handler can
# announce it only after uvicorn is actually listening.
_announced_port = 0


@asynccontextmanager
async def _lifespan(app: FastAPI):
    """Announce the server port on stdout only after uvicorn has bound its socket."""
    print(f'PORT:{_announced_port}', flush=True)
    yield


app = FastAPI(lifespan=_lifespan)


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------

@app.post('/shutdown')
async def shutdown():
    os._exit(0)


@app.post('/detect_raw')
async def detect_raw(request: Request):
    """Detect NSFW content in a pre-letterboxed 640×640 BGRA frame.

    Body: exactly 640×640×4 raw BGRA bytes (1,638,400 bytes), no headers needed.
    Boxes in the response are in 640×640 pixel space; C# un-projects them to
    tile-local and then screen coordinates.
    """
    try:
        body = await request.body()
        # Drop alpha — SkiaSharp sends BGRA, model expects BGR.
        img_bgra = np.frombuffer(body, dtype=np.uint8).reshape(_MODEL_SIZE, _MODEL_SIZE, 4)
        img_bgr  = np.ascontiguousarray(img_bgra[:, :, :3])
        detections = await asyncio.to_thread(detector.run, img_bgr)
        return {'detections': detections}
    except Exception as e:
        log.write(f'error /detect_raw: {e}\n{traceback.format_exc()}')
        raise HTTPException(status_code=500, detail=str(e))


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == '__main__':
    if args.port > 0:
        port = args.port
    else:
        sock = socket.socket()
        sock.bind(('', 0))
        port = sock.getsockname()[1]
        sock.close()

    log.write(f'listening host=127.0.0.1 port={port}')
    # PORT is announced from the lifespan handler _after_ uvicorn binds its socket.
    _announced_port = port
    uvicorn.run(app, host='127.0.0.1', port=port, log_level='warning')
