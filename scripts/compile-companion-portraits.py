"""Compile dedicated R3 portraits; never writes the R2 body assets or compiler.

Pillow is the only third-party dependency. Source PNGs remain unchanged. Equal
grid cells locate characters, but 8-connected alpha silhouettes define actual
crops so ears and helmets crossing a nominal cell boundary are not cut off.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import struct

import PIL
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
WIDTH, HEIGHT, PALETTE_SIZE = 48, 30, 32
FRAMES = ("idle", "blink", "happy")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def rgb565(rgb):
    r, g, b = rgb
    return (((r * 31 + 127) // 255) << 11) | (((g * 63 + 127) // 255) << 5) | ((b * 31 + 127) // 255)


def expand565(value):
    r, g, b = (value >> 11) & 31, (value >> 5) & 63, value & 31
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))


def pixels(im):
    return im.get_flattened_data() if hasattr(im, "get_flattened_data") else im.getdata()


def alpha_stats(im):
    h = im.getchannel("A").histogram()
    return {"zero": h[0], "opaque_255": h[255], "alpha_1_63": sum(h[1:64]),
            "alpha_64_127": sum(h[64:128]), "alpha_128_191": sum(h[128:192]), "alpha_192_254": sum(h[192:255])}


def component_boxes(im, threshold, expected):
    """Union opaque horizontal spans using 8-connectivity, without image edits."""
    raw = im.getchannel("A").point(lambda a: 255 if a >= threshold else 0).tobytes()
    parents, runs, previous = [], [], []
    def find(i):
        while parents[i] != i:
            parents[i] = parents[parents[i]]
            i = parents[i]
        return i
    for y in range(im.height):
        current = []
        for match in re.finditer(b"\xff+", raw[y * im.width:(y + 1) * im.width]):
            x0, x1 = match.span()
            index = len(parents)
            parents.append(index)
            runs.append((x0, y, x1, index))
            current.append((x0, x1, index))
            for p0, p1, pi in previous:
                if p0 > x1:
                    break
                if p1 >= x0:
                    parents[find(index)] = find(pi)
        previous = current
    boxes = {}
    for x0, y, x1, index in runs:
        root = find(index)
        if root not in boxes:
            boxes[root] = [x0, y, x1, y + 1, 0]
        box = boxes[root]
        box[0] = min(box[0], x0)
        box[1] = min(box[1], y)
        box[2] = max(box[2], x1)
        box[3] = max(box[3], y + 1)
        box[4] += x1 - x0
    ranked = sorted(boxes.items(), key=lambda item: item[1][4], reverse=True)
    if len(ranked) < expected or ranked[expected - 1][1][4] < im.width * im.height / expected * .04:
        raise ValueError(f"Atlas lacks {expected} separate portrait silhouettes; largest component areas: {[x[1][4] for x in ranked[:expected]]}")
    if len(ranked) > expected and ranked[expected][1][4] > ranked[expected - 1][1][4] * .08:
        raise ValueError("Ambiguous detached component; inspect the original before compiling")
    retained = dict(ranked[:expected])
    masks = {root: Image.new("L", im.size) for root in retained}
    draws = {root: ImageDraw.Draw(mask) for root, mask in masks.items()}
    for x0, y, x1, index in runs:
        root = find(index)
        if root in draws:
            draws[root].line((x0, y, x1 - 1, y), fill=255)
    return [(box[:4], masks[root], box[4]) for root, box in retained.items()]


def collect(mapping, folder):
    threshold = mapping["alpha_threshold"]
    if not isinstance(threshold, int) or not 1 <= threshold <= 255:
        raise ValueError("Alpha threshold must be 1..255")
    frames, sources = {}, []
    for atlas in mapping["atlases"]:
        if atlas.get("enabled", True) is False:
            continue
        path = (folder / atlas["file"]).resolve()
        if not path.is_relative_to(folder.resolve()):
            raise ValueError("Source path escapes the asset folder")
        if not path.is_file():
            if atlas.get("required", False):
                raise FileNotFoundError(path)
            continue
        rows, columns = atlas["characters"], atlas["frames"]
        if not rows or tuple(columns) != FRAMES or len(set(rows)) != len(rows):
            raise ValueError("Expected unique portrait rows and idle/blink/happy columns")
        original = path.read_bytes()
        with Image.open(path) as opened:
            im = opened.convert("RGBA")
        sources.append({"file": atlas["file"], "sha256": sha(original), "size": list(im.size),
                        "alpha": alpha_stats(im), "grid_rows": len(rows), "grid_columns": 3})
        try:
            components = sorted(component_boxes(im, threshold, len(rows) * 3), key=lambda c: c[0][1] + c[0][3])
        except ValueError as error:
            raise ValueError(atlas["file"] + ": " + str(error)) from error
        cell_height = im.height / len(rows)
        for row, character in enumerate(rows):
            if not isinstance(character, int) or not 0 <= character < 12:
                raise ValueError("Portrait character is outside 0..11")
            row_components = sorted(components[row * 3:row * 3 + 3], key=lambda c: c[0][0] + c[0][2])
            for col, frame in enumerate(columns):
                key = character, frame
                if key in frames:
                    raise ValueError("Duplicate character/frame")
                box, mask, count = row_components[col]
                crop = im.crop(box)
                crop.putalpha(mask.crop(box))
                nominal = [round(col * im.width / 3), round(row * cell_height),
                           round((col + 1) * im.width / 3), round((row + 1) * cell_height)]
                frames[key] = {"image": crop, "cell_height": cell_height, "source": atlas["file"],
                    "source_sha256": sha(original), "tile_box": box, "nominal_tile_box": nominal,
                    "source_component_pixels": count, "row": row, "column": col,
                    "normalized_bbox": [-crop.width / (2 * cell_height), -crop.height / cell_height,
                                        crop.width / (2 * cell_height), 0.0]}
    return frames, sources


def decode(indices, palette):
    if len(indices) != WIDTH * HEIGHT or len(palette) != PALETTE_SIZE or any(i >= PALETTE_SIZE for i in indices):
        raise ValueError("Invalid 48x30 indexed portrait")
    im = Image.new("RGBA", (WIDTH, HEIGHT))
    im.putdata([(*expand565(palette[i]), 255) if i else (0, 0, 0, 0) for i in indices])
    return im


def prepare(character, frames, padding):
    available = [(frame, frames[character, frame]) for frame in FRAMES if (character, frame) in frames]
    widest = max(d["image"].width / d["cell_height"] for _, d in available)
    tallest = max(d["image"].height / d["cell_height"] for _, d in available)
    scale = min((WIDTH - padding * 2) / widest, (HEIGHT - padding * 2) / tallest)
    baseline = HEIGHT - padding
    scaled = []
    for frame, data in available:
        crop = data["image"]
        ratio = data["cell_height"] / scale
        affine = (ratio, 0, crop.width / 2 - WIDTH / 2 * ratio, 0, ratio, crop.height - baseline * ratio)
        im = crop.transform((WIDTH, HEIGHT), Image.Transform.AFFINE, affine,
                            resample=Image.Resampling.NEAREST, fillcolor=(0, 0, 0, 0))
        bbox = im.getchannel("A").getbbox()
        if not bbox or bbox[0] < padding or bbox[1] < padding or bbox[2] > WIDTH - padding or bbox[3] > HEIGHT - padding:
            raise ValueError("Portrait is empty or clipped at the shared canvas border")
        scaled.append((frame, im, data))
    counts = Counter(p[:3] for _, im, _ in scaled for p in pixels(im) if p[3])
    samples = [rgb for rgb, count in counts.items() for _ in range(count)]
    strip = Image.new("RGB", (len(samples), 1))
    strip.putdata(samples)
    quantized = strip.quantize(colors=31, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    colors = quantized.getpalette()
    used = sorted(index for _, index in quantized.getcolors())
    palette = [0] + list(dict.fromkeys(rgb565(colors[i * 3:i * 3 + 3]) for i in used))
    actual_count = len(palette)
    palette += [0] * (PALETTE_SIZE - actual_count)
    expanded = [expand565(value) for value in palette]
    lookup = {rgb: min(range(1, actual_count), key=lambda i: sum((rgb[j] - expanded[i][j]) ** 2 for j in range(3))) for rgb in counts}
    output = []
    for frame, im, data in scaled:
        indices = bytes(lookup[p[:3]] if p[3] else 0 for p in pixels(im))
        output.append((frame, decode(indices, palette), indices, data))
    return output, palette, {"scale_pixels_per_cell_height": scale, "anchor_x": WIDTH / 2,
        "baseline_exclusive_y": baseline, "shared_normalized_union_bbox": [-widest / 2, -tallest, widest / 2, 0],
        "palette_count": PALETTE_SIZE, "used_palette_count": actual_count, "palette_rgb565": palette}


def contact(manifest, folder, ids, name):
    records = {(r["character"], r["frame"]): r for r in manifest["frames"]}
    ids = [i for i in ids if (i, "idle") in records]
    zoom, gap, cw, ch = 6, 12, 300, 214
    sheet = Image.new("RGB", (cw * 6 + gap, ch * len(ids) + gap), "#edf1f3")
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 17)
    except OSError:
        font = ImageFont.load_default()
    for row, character in enumerate(ids):
        for col, frame in enumerate(FRAMES):
            record = records[character, frame]
            with Image.open(folder / record["png"]) as opened:
                im = opened.convert("RGBA").resize((WIDTH * zoom, HEIGHT * zoom), Image.Resampling.NEAREST)
            for side, color in enumerate(("#10242e", "#f8f2e6")):
                x, y = gap + (side * 3 + col) * cw, gap + row * ch
                panel = Image.new("RGBA", im.size, color)
                panel.alpha_composite(im)
                sheet.paste(panel.convert("RGB"), (x, y))
                draw.text((x, y + im.height + 4), f"{record['slug']} / {frame}", fill="#24323d", font=font)
    sheet.save(folder / name)


def emit_cpp(manifest, indices, palettes, folder):
    records = {(r["character"], r["frame"]): r for r in manifest["frames"]}
    if len(records) != 36 or any((i, f) not in records for i in range(12) for f in FRAMES):
        raise ValueError("Firmware requires all 36 real portrait frames; no placeholders")
    header = """#pragma once
#include <stdint.h>
#include "SpriteArt.h"
namespace deskportrait {
struct Sprite { const uint8_t* indices; const uint16_t* palette;
  uint8_t width,height,paletteCount,transparentIndex; };
// WalkA/WalkB and invalid frames return Idle; invalid characters return character 0.
const Sprite& get(uint8_t character,deskart::Frame frame);
}
"""
    lines = ['// Generated by scripts/compile-companion-portraits.py; see the R3 compiled manifest.',
             '#include "PortraitArt.h"', 'namespace deskportrait { namespace {']
    for c in manifest["characters"]:
        lines.append(f"const uint16_t palette_{c['id']}[32]={{" + ",".join(f"0x{v:04X}" for v in c["palette_rgb565"]) + "};")
    for i in range(12):
        for frame in FRAMES:
            start = records[i, frame]["indices_offset"]
            data = indices[start:start + WIDTH * HEIGHT]
            lines.append(f"const uint8_t pixels_{i}_{frame}[1440]={{")
            lines.extend(",".join(str(v) for v in data[j:j + WIDTH]) + "," for j in range(0, len(data), WIDTH))
            lines.append("};")
    lines.append("const Sprite portraits[12][3]={")
    for i in range(12):
        lines.append("{" + ",".join(f"{{pixels_{i}_{f},palette_{i},48,30,32,0}}" for f in FRAMES) + "},")
    lines.extend(["};", "}", "const Sprite& get(uint8_t character,deskart::Frame frame){",
        "  const uint8_t f=static_cast<uint8_t>(frame);", "  return portraits[character<12?character:0][f<3?f:0];", "}", "}", ""])
    source = "\n".join(lines)
    for c in manifest["characters"]:
        match = re.search(rf"palette_{c['id']}\[32\]=\{{(.*?)\}};", source)
        raw = struct.pack("<32H", *[int(v, 16) for v in re.findall(r"0x[0-9A-F]+", match.group(1))])
        if raw != palettes[c["palette_offset"]:c["palette_offset"] + 64]:
            raise AssertionError("C++ palette readback differs")
    for (i, frame), record in records.items():
        match = re.search(rf"pixels_{i}_{frame}\[1440\]=\{{(.*?)\}};", source, re.S)
        raw = bytes(int(v) for v in re.findall(r"\d+", match.group(1)))
        if raw != indices[record["indices_offset"]:record["indices_offset"] + WIDTH * HEIGHT]:
            raise AssertionError("C++ index readback differs")
    folder.mkdir(parents=True, exist_ok=True)
    (folder / "PortraitArt.h").write_text(header, "utf-8")
    (folder / "PortraitArt.cpp").write_text(source, "utf-8")
    return {name: sha((folder / name).read_bytes()) for name in ("PortraitArt.h", "PortraitArt.cpp")}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mapping", type=Path, default=ROOT / "assets/companions/r3/atlas-map.json")
    parser.add_argument("--output", type=Path, default=ROOT / "assets/companions/r3/compiled")
    parser.add_argument("--emit-firmware", action="store_true")
    parser.add_argument("--firmware-dir", type=Path, default=ROOT / "firmware/DeskController")
    args = parser.parse_args()
    mapping = json.loads(args.mapping.read_text("utf-8"))
    if mapping.get("schema_version") != 1 or mapping.get("asset_kind") != "dedicated_portrait":
        raise ValueError("Unsupported portrait mapping")
    padding = mapping["padding"]
    if not isinstance(padding, int) or not 1 <= padding < HEIGHT / 2:
        raise ValueError("Invalid portrait padding")
    folder = args.mapping.parent.resolve()
    frames, sources = collect(mapping, folder)
    if not frames:
        raise ValueError("No portrait sources available; no output or firmware generated")
    specs = {c["id"]: c for c in mapping["characters"]}
    if set(specs) != set(range(12)):
        raise ValueError("Character metadata must cover IDs 0..11")
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = {"schema_version": 1, "asset_kind": "dedicated_portrait", "dimensions": [WIDTH, HEIGHT],
        "compiler_sha256": sha(Path(__file__).read_bytes()), "mapping_sha256": sha(args.mapping.read_bytes()),
        "pillow_version": PIL.__version__, "alpha_threshold": mapping["alpha_threshold"], "transparent_index": 0,
        "palette_size": PALETTE_SIZE, "sampling": "nearest-neighbour", "source_pngs_modified": False,
        "extraction": "8-connected whole alpha silhouettes; nominal grid only assigns row/column",
        "alignment": "one scale per character in source-cell-height units; shared center X=24 and bottom Y=29",
        "palette_method": "one per-character palette shared across idle/blink/happy; median-cut31 then nearest RGB565; no dithering",
        "rgb565_decode": "bit replication r5<<3|r5>>2, g6<<2|g6>>4, b5<<3|b5>>2",
        "sources": sources, "characters": [], "frames": []}
    indices, palettes = bytearray(), bytearray()
    checks = 0
    for character in sorted({i for i, _ in frames}):
        output, palette, info = prepare(character, frames, padding)
        info.update({"id": character, "slug": specs[character]["slug"], "name": specs[character]["name"], "palette_offset": len(palettes)})
        palettes.extend(struct.pack("<32H", *palette))
        manifest["characters"].append(info)
        for frame, decoded, raw, data in output:
            name = f"{character:02d}-{info['slug']}-{frame}.png"
            decoded.save(args.output / name)
            with Image.open(args.output / name) as reopened:
                if reopened.convert("RGBA").tobytes() != decode(raw, palette).tobytes():
                    raise AssertionError("Saved PNG differs from device RGB565/index values")
            for index, pixel in zip(raw, pixels(decoded)):
                if index and (pixel[3] != 255 or rgb565(pixel[:3]) != palette[index]):
                    raise AssertionError("PNG cannot recover exact device RGB565 value")
                if not index and pixel != (0, 0, 0, 0):
                    raise AssertionError("Invalid transparent pixel")
                checks += 1
            record = {k: data[k] for k in ("source", "source_sha256", "tile_box", "nominal_tile_box", "source_component_pixels", "row", "column")}
            record.update({"character": character, "slug": info["slug"], "frame": frame, "indices_offset": len(indices),
                "indices_bytes": len(raw), "indices_sha256": sha(raw), "png": name,
                "png_sha256": sha((args.output / name).read_bytes()), "visible_pixels": sum(i != 0 for i in raw),
                "compiled_alpha_bbox": list(decoded.getchannel("A").getbbox())})
            manifest["frames"].append(record)
            indices.extend(raw)
    (args.output / "portraits.indices.bin").write_bytes(indices)
    (args.output / "portraits.palettes.rgb565.bin").write_bytes(palettes)
    indices = (args.output / "portraits.indices.bin").read_bytes()
    palettes = (args.output / "portraits.palettes.rgb565.bin").read_bytes()
    by_id = {c["id"]: c for c in manifest["characters"]}
    for record in manifest["frames"]:
        palette = struct.unpack_from("<32H", palettes, by_id[record["character"]]["palette_offset"])
        raw = indices[record["indices_offset"]:record["indices_offset"] + WIDTH * HEIGHT]
        with Image.open(args.output / record["png"]) as saved:
            if decode(raw, palette).tobytes() != saved.convert("RGBA").tobytes():
                raise AssertionError("Combined binary readback differs from PNG")
    manifest["missing_frames"] = [{"character": i, "frame": f} for i in range(12) for f in FRAMES if (i, f) not in frames]
    manifest["complete"] = not manifest["missing_frames"]
    manifest["budget"] = {"indices_bytes": len(indices), "palette_bytes": len(palettes),
        "arm_sprite_table_bytes": len(manifest["frames"]) * 12, "complete_set_data_estimate_bytes": 53040}
    manifest["verification"] = {"png_pixel_checks": checks, "binary_readback": "PASS", "rgb565_roundtrip": "PASS"}
    if args.emit_firmware:
        manifest["firmware_sha256"] = emit_cpp(manifest, indices, palettes, args.firmware_dir)
        manifest["verification"]["cpp_initializer_readback"] = "PASS"
    for source in sources:
        if sha((folder / source["file"]).read_bytes()) != source["sha256"]:
            raise AssertionError("Original portrait source changed")
    contact(manifest, args.output, range(12), "contact-sheet.png")
    for group, ids in (("woodland", (2, 8, 4, 9)), ("fantasy", (3, 5, 6, 7)), ("classic", (0, 1, 10, 11))):
        if any(i in by_id for i in ids):
            contact(manifest, args.output, ids, "contact-" + group + ".png")
    manifest["contact_sheet_sha256"] = sha((args.output / "contact-sheet.png").read_bytes())
    (args.output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", "utf-8")
    print(json.dumps({"frames": len(manifest["frames"]), "complete": manifest["complete"],
                      "verification": manifest["verification"], "budget": manifest["budget"]}, indent=2))


if __name__ == "__main__":
    main()
