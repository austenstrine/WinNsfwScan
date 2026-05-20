from fastapi import FastAPI, UploadFile, File, HTTPException
from nudenet import NudeDetector
import os
import sys
import argparse
import traceback
import socket
import uvicorn

app = FastAPI()

def get_base_path():
    if getattr(sys, 'frozen', False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))

def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument('--model', type=str, default='640m.onnx', help='Model filename (relative to exe dir)')
    parser.add_argument('--resolution', type=int, default=640, help='Inference resolution')
    # parse_known_args avoids errors from frozen exe launchers passing extra args
    args, _ = parser.parse_known_args()
    return args

args = parse_args()
model_path = os.path.join(get_base_path(), args.model)
detector = NudeDetector(model_path=model_path, inference_resolution=args.resolution)

@app.post("/detect")
async def detect(file: UploadFile = File(...)):
    try:
        contents = await file.read()
        detections = detector.detect(contents)
        return {"detections": detections}
    except Exception as e:
        print(f"ERROR in /detect: {str(e)}")
        traceback.print_exc()
        raise HTTPException(status_code=500, detail=str(e))

if __name__ == "__main__":
    sock = socket.socket()
    sock.bind(('', 0))
    port = sock.getsockname()[1]
    sock.close()

    print(f"PORT:{port}", flush=True)
    uvicorn.run(app, host="127.0.0.1", port=port, log_level="warning")