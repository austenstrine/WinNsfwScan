from fastapi import FastAPI, UploadFile, File, HTTPException
from nudenet import NudeDetector
import uvicorn
import io
import socket
import traceback

app = FastAPI()
def get_model_path():
    if getattr(sys, 'frozen', False):
        base_path = os.path.dirname(sys.executable)
    else:
        base_path = os.path.dirname(os.path.abspath(__file__))

    return os.path.join(base_path, "320n.onnx")

detector = NudeDetector(model_path=get_model_path())

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