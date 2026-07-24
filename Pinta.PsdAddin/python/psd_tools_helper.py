from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from PIL import Image
from psd_tools import PSDImage


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Render PSD layers for Pinta import.")
    parser.add_argument("--input", required=True, help="Path to the PSD or PSB file.")
    parser.add_argument("--output-dir", required=True, help="Directory for manifest and rendered PNG files.")
    return parser.parse_args()


def blank_image(size: tuple[int, int]) -> Image.Image:
    return Image.new("RGBA", size, (0, 0, 0, 0))


def is_container_layer(layer) -> bool:
    return getattr(layer, "kind", "") in {"group", "artboard", "psdimage"}


def normalize_blend_mode(layer) -> str:
    value = getattr(layer, "blend_mode", None)
    if value is None:
        return "normal"

    name = getattr(value, "name", None)
    if isinstance(name, str) and name:
        return name.lower()

    text = str(value)
    if "." in text:
        text = text.rsplit(".", 1)[-1]
    return text.lower()


def render_full_canvas(layer, canvas_size: tuple[int, int], viewport: tuple[int, int, int, int]) -> Image.Image:
    if is_container_layer(layer):
        return blank_image(canvas_size)

    image = None

    try:
        image = layer.composite(viewport=viewport, force=True)
    except Exception:
        image = None

    if image is None:
        try:
            image = layer.topil()
        except Exception:
            image = None

    if image is None:
        return blank_image(canvas_size)

    image = image.convert("RGBA")
    if image.size == canvas_size:
        return image

    canvas = blank_image(canvas_size)
    bbox = getattr(layer, "bbox", None)
    if bbox and len(bbox) == 4:
        left, top, _, _ = bbox
        canvas.alpha_composite(image, dest=(left, top))
    else:
        canvas.alpha_composite(image)
    return canvas


def export_layer(layer, psd, output_dir: Path, next_id: list[int]) -> dict[str, object]:
    layer_id = f"layer-{next_id[0]:04d}"
    next_id[0] += 1

    surface_name = f"{layer_id}.png"
    surface_path = output_dir / surface_name

    image = render_full_canvas(layer, (psd.width, psd.height), psd.viewbox)
    image.save(surface_path, format="PNG")

    children = [export_layer(child, psd, output_dir, next_id) for child in layer] if is_container_layer(layer) else []

    opacity = getattr(layer, "opacity", 255)
    try:
        opacity_value = max(0.0, min(float(opacity) / 255.0, 1.0))
    except Exception:
        opacity_value = 1.0

    visible = getattr(layer, "visible", True)
    return {
        "id": layer_id,
        "name": getattr(layer, "name", "") or "Layer",
        "hidden": not bool(visible),
        "opacity": opacity_value,
        "blendMode": normalize_blend_mode(layer),
        "kind": getattr(layer, "kind", "") or "",
        "surface": surface_name,
        "children": children,
    }


def main() -> int:
    args = parse_args()

    input_path = Path(args.input)
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    psd = PSDImage.open(input_path)
    next_id = [1]
    layers = [export_layer(layer, psd, output_dir, next_id) for layer in psd]

    manifest = {
        "width": psd.width,
        "height": psd.height,
        "selectedLayerId": layers[-1]["id"] if layers else None,
        "layers": layers,
    }

    (output_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=True),
        encoding="utf-8",
    )

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise
