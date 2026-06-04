import asyncio
import argparse
import datetime
import os
import socket
import sys
import traceback

from fastapi import FastAPI, File, HTTPException, Request, UploadFile
from nudenet.nudenet import _postprocess, _read_image
import uvicorn
import onnxruntime as ort

app = FastAPI()

def get_base_path():
    if getattr(sys, 'frozen', False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


class ServerRuntimeLogger:
    def __init__(self, base_path):
        self._log_path = self._build_log_path(base_path)

    def _build_log_path(self, base_path):
        local_app_data = os.getenv('LOCALAPPDATA')
        log_dir = (
            os.path.join(local_app_data, 'WinNsfwScan', 'logs')
            if local_app_data
            else os.path.join(base_path, 'logs')
        )
        os.makedirs(log_dir, exist_ok=True)
        stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
        return os.path.join(log_dir, f'server-runtime-{stamp}.log')

    def write(self, message):
        timestamp = datetime.datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]
        line = f'{timestamp} {message}'
        with open(self._log_path, 'a', encoding='utf-8') as f:
            f.write(line + '\n')

def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument('--model', type=str, default='320n.onnx', help='Model filename (relative to exe dir)')
    parser.add_argument('--resolution', type=int, default=320, help='Inference resolution')
    parser.add_argument('--port', type=int, default=0, help='Server port (0 = auto-select)')
    parser.add_argument(
        '--execution-provider',
        type=str,
        default='auto',
        choices=['auto', 'directml', 'cuda', 'cpu'],
        help='Inference provider preference (default: auto)'
    )
    # parse_known_args avoids errors from frozen exe launchers passing extra args
    args, _ = parser.parse_known_args()
    return args


class RuntimeNudeDetector:
    def __init__(self, model_path, inference_resolution, execution_provider):
        self.input_width = inference_resolution
        self.input_height = inference_resolution
        self.providers = self._resolve_providers(execution_provider)
        self.onnx_session = ort.InferenceSession(model_path, providers=self.providers)
        self.active_providers = self.onnx_session.get_providers()
        self.input_name = self.onnx_session.get_inputs()[0].name

    def _resolve_providers(self, execution_provider):
        available = ort.get_available_providers()

        if execution_provider == 'cpu':
            preferred = ['CPUExecutionProvider']
        elif execution_provider == 'cuda':
            preferred = ['CUDAExecutionProvider', 'CPUExecutionProvider']
        elif execution_provider == 'directml':
            preferred = ['DmlExecutionProvider', 'CPUExecutionProvider']
        else:
            preferred = ['DmlExecutionProvider', 'CUDAExecutionProvider', 'CPUExecutionProvider']

        resolved = [p for p in preferred if p in available]
        if 'CPUExecutionProvider' not in resolved and 'CPUExecutionProvider' in available:
            resolved.append('CPUExecutionProvider')

        if not resolved:
            # Final safety fallback for unusual runtime environments.
            return ['CPUExecutionProvider']

        return resolved

    def detect(self, image_bytes):
        (
            preprocessed_image,
            x_ratio,
            y_ratio,
            x_pad,
            y_pad,
            image_original_width,
            image_original_height,
        ) = _read_image(image_bytes, self.input_width)

        outputs = self.onnx_session.run(None, {self.input_name: preprocessed_image})

        return _postprocess(
            outputs,
            x_pad,
            y_pad,
            x_ratio,
            y_ratio,
            image_original_width,
            image_original_height,
            self.input_width,
            self.input_height,
        )


args = parse_args()
base_path = get_base_path()
logger = ServerRuntimeLogger(base_path)
model_path = os.path.join(base_path, args.model)
available_providers = ort.get_available_providers()
detector = RuntimeNudeDetector(
    model_path=model_path,
    inference_resolution=args.resolution,
    execution_provider=args.execution_provider,
)
logger.write(
    f"startup model={args.model} resolution={args.resolution} providerPreference={args.execution_provider} "
    f"availableProviders={available_providers} requestedProviders={detector.providers} activeProviders={detector.active_providers} "
    f"gpuInUse={any(p in detector.active_providers for p in ['DmlExecutionProvider', 'CUDAExecutionProvider'])}"
)

@app.post("/shutdown")
async def shutdown():
    os._exit(0)

@app.post("/detect")
async def detect(file: UploadFile = File(...)):
    try:
        contents = await file.read()
        detections = await asyncio.to_thread(detector.detect, contents)
        return {"detections": detections}
    except Exception as e:
        logger.write(f"error /detect: {str(e)}")
        logger.write(traceback.format_exc())
        raise HTTPException(status_code=500, detail=str(e))

@app.post("/detect_raw")
async def detect_raw(request: Request):
    try:
        contents = await request.body()
        if not contents:
            raise HTTPException(status_code=400, detail="Request body is empty")
        detections = await asyncio.to_thread(detector.detect, contents)
        return {"detections": detections}
    except HTTPException:
        raise
    except Exception as e:
        logger.write(f"error /detect_raw: {str(e)}")
        logger.write(traceback.format_exc())
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    if args.port > 0:
        port = args.port
    else:
        sock = socket.socket()
        sock.bind(('', 0))
        port = sock.getsockname()[1]
        sock.close()

    logger.write(f"listening host=127.0.0.1 port={port}")
    print(f"PORT:{port}", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="warning")
