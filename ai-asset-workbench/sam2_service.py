"""Small local SAM 2 image-prompt service for the browser workbench."""

from __future__ import annotations

import base64
import io
import os
from pathlib import Path
from threading import Lock

import numpy as np
import torch
from fastapi import FastAPI, File, Form, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from PIL import Image

ROOT = Path(__file__).resolve().parent
SAM2_REPO = Path(os.getenv("SAM2_REPO", r"H:\AISpine\AIModel\sam2"))
CHECKPOINT = Path(os.getenv("SAM2_CHECKPOINT", r"H:\AISpine\AIModel\models\sam2\sam2_hiera_base_plus.pt"))
CONFIG = os.getenv("SAM2_CONFIG", "configs/sam2/sam2_hiera_b+.yaml")

app = FastAPI(title="Pinta SAM 2 service")
app.add_middleware(CORSMiddleware, allow_origins=["http://localhost:4173", "http://127.0.0.1:4173"], allow_methods=["POST", "GET"], allow_headers=["*"])
model_lock = Lock()
predictor = None


def get_predictor():
    global predictor
    if predictor is None:
        from sam2.build_sam import build_sam2
        from sam2.sam2_image_predictor import SAM2ImagePredictor

        if not CHECKPOINT.exists():
            raise FileNotFoundError(f"SAM 2 checkpoint not found: {CHECKPOINT}")
        previous_directory = Path.cwd()
        os.chdir(SAM2_REPO)
        try:
            model = build_sam2(CONFIG, str(CHECKPOINT), device="cuda" if torch.cuda.is_available() else "cpu")
        finally:
            os.chdir(previous_directory)
        predictor = SAM2ImagePredictor(model)
    return predictor


def encode_mask(mask: np.ndarray) -> str:
    output = Image.fromarray((mask.astype(np.uint8) * 255), mode="L")
    stream = io.BytesIO()
    output.save(stream, format="PNG", optimize=True)
    return base64.b64encode(stream.getvalue()).decode("ascii")


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok", "checkpoint": str(CHECKPOINT), "device": "cuda" if torch.cuda.is_available() else "cpu"}


@app.post("/segment")
async def segment(
    file: UploadFile = File(...),
    box: str | None = Form(default=None),
    point_x: float | None = Form(default=None),
    point_y: float | None = Form(default=None),
) -> dict[str, object]:
    image = Image.open(io.BytesIO(await file.read())).convert("RGB")
    image_array = np.asarray(image)
    with model_lock:
        model = get_predictor()
        model.set_image(image_array)
        kwargs: dict[str, object] = {"multimask_output": True}
        if box:
            x1, y1, x2, y2 = (float(value) for value in box.split(","))
            kwargs["box"] = np.array([x1, y1, x2, y2], dtype=np.float32)
        elif point_x is not None and point_y is not None:
            kwargs["point_coords"] = np.array([[point_x, point_y]], dtype=np.float32)
            kwargs["point_labels"] = np.array([1], dtype=np.int32)
        else:
            return {"error": "box or point prompt is required"}
        masks, scores, _ = model.predict(**kwargs)
    index = int(np.argmax(scores))
    selected = np.asarray(masks[index]).squeeze().astype(bool)
    return {"mask_base64": encode_mask(selected), "score": float(scores[index]), "width": image.width, "height": image.height}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("sam2_service:app", host="127.0.0.1", port=8765, reload=False)
