"""Compile source atlases into indexed RGB565 sprites without altering source PNGs.

Only geometric cropping/nearest-neighbour sampling, alpha thresholding and palette
quantization occur. All frames of one character share normalized bounds, scale,
ground line, RGB565 palette and portrait crop. Missing frames are reported, never
invented. --emit-firmware requires the complete twelve-character/five-frame set.
"""
from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import re
import struct

from PIL import Image, ImageDraw, ImageFont
import PIL

ROOT = Path(__file__).resolve().parents[1]
FRAMES = ("idle", "blink", "happy", "walk_a", "walk_b")
WIDTH, HEIGHT, PALETTE_SIZE = 64, 60, 32


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rgb565(rgb):
    r, g, b = rgb
    return (((r * 31 + 127) // 255) << 11) | (((g * 63 + 127) // 255) << 5) | ((b * 31 + 127) // 255)


def expand565(value):
    r, g, b = (value >> 11) & 31, (value >> 5) & 63, value & 31
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))


def alpha_stats(im):
    histogram = im.getchannel("A").histogram()
    return {"zero": histogram[0], "opaque_255": histogram[255],
            "alpha_1_63": sum(histogram[1:64]), "alpha_64_127": sum(histogram[64:128]),
            "alpha_128_191": sum(histogram[128:192]), "alpha_192_254": sum(histogram[192:255])}


def pixels(im):
    return im.get_flattened_data() if hasattr(im, "get_flattened_data") else im.getdata()


def component_boxes(im, threshold, expected):
    """Find whole silhouettes before grid assignment; generation may cross nominal cells.

    Union adjacent opaque horizontal runs (8-connected). Tiny alpha specks are not
    a character. Retain exactly the expected large silhouettes, and reject an
    ambiguous size gap instead of silently cutting feet or including a neighbour.
    """
    mask = im.getchannel("A").point(lambda a: 255 if a >= threshold else 0)
    raw, width = mask.tobytes(), im.width
    parents, runs, previous = [], [], []
    def find(i):
        while parents[i] != i:
            parents[i] = parents[parents[i]]; i = parents[i]
        return i
    for y in range(im.height):
        current = []
        for match in re.finditer(b"\xff+", raw[y * width:(y + 1) * width]):
            x0, x1 = match.span(); index = len(parents)
            parents.append(index); runs.append((x0, y, x1, index)); current.append((x0, x1, index))
            for p0, p1, pi in previous:
                if p0 > x1: break
                if p1 >= x0: parents[find(index)] = find(pi)
        previous = current
    boxes = {}
    for x0, y, x1, index in runs:
        root = find(index)
        if root not in boxes: boxes[root] = [x0, y, x1, y + 1, 0]
        box = boxes[root]
        box[0] = min(box[0], x0); box[1] = min(box[1], y)
        box[2] = max(box[2], x1); box[3] = max(box[3], y + 1); box[4] += x1 - x0
    ranked = sorted(boxes.items(), key=lambda item: item[1][4], reverse=True)
    if len(ranked) < expected or ranked[expected - 1][1][4] < im.width * im.height / expected * 0.04:
        raise ValueError("Atlas lacks expected separate character silhouettes")
    if len(ranked) > expected and ranked[expected][1][4] > ranked[expected - 1][1][4] * 0.08:
        raise ValueError("Atlas has ambiguous detached components; review original geometry")
    retained = {root: box for root, box in ranked[:expected]}
    masks = {root: Image.new("L", im.size) for root in retained}
    draws = {root: ImageDraw.Draw(m) for root, m in masks.items()}
    for x0, y, x1, index in runs:
        root = find(index)
        if root in draws: draws[root].line((x0, y, x1 - 1, y), fill=255)
    return [(box[:4], masks[root], box[4]) for root, box in retained.items()]


def safe_relative(root: Path, text: str) -> Path:
    path = (root / text).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("Input path escapes asset folder")
    return path


def collect(mapping, assets):
    frames, sources = {}, []
    threshold = int(mapping["alpha_threshold"])
    if not 1 <= threshold <= 255:
        raise ValueError("alpha_threshold must be 1..255")
    for atlas in mapping["atlases"]:
        file = safe_relative(assets, atlas["file"])
        if not file.is_file():
            if atlas.get("required", False):
                raise FileNotFoundError(file)
            continue
        raw = file.read_bytes()
        with Image.open(file) as opened:
            im = opened.convert("RGBA")
        rows, columns = atlas["characters"], atlas["frames"]
        if not rows or not columns or len(set(columns)) != len(columns) or any(f not in FRAMES for f in columns):
            raise ValueError("Invalid atlas grid")
        sources.append({"file": atlas["file"], "sha256": sha(raw), "size": list(im.size), "alpha": alpha_stats(im),
                        "grid_rows": len(rows), "grid_columns": len(columns)})
        components = sorted(component_boxes(im, threshold, len(rows) * len(columns)), key=lambda item: (item[0][1] + item[0][3]) / 2)
        for row, character in enumerate(rows):
            if not 0 <= character < 12:
                raise ValueError("Character ID outside 0..11")
            row_components = sorted(components[row * len(columns):(row + 1) * len(columns)], key=lambda item: (item[0][0] + item[0][2]) / 2)
            for column, frame in enumerate(columns):
                key = character, frame
                if key in frames:
                    raise ValueError(f"Duplicate character/frame {key}")
                nominal = (round(column * im.width / len(columns)), round(row * im.height / len(rows)),
                       round((column + 1) * im.width / len(columns)), round((row + 1) * im.height / len(rows)))
                component, isolated_mask, component_pixels = row_components[column]
                box = tuple(component)
                tile = im.crop(box)
                mask = isolated_mask.crop(box)
                tile.putalpha(mask)
                bbox = mask.getbbox()
                if bbox is None:
                    raise ValueError(f"Empty alpha silhouette: {key}")
                # Use cell-height units, centered X and each frame's visible bottom as ground.
                # This preserves aspect ratio even when source atlases have different sizes.
                cell_height = im.height / len(rows)
                normal = [(bbox[0] - tile.width / 2) / cell_height, (bbox[1] - bbox[3]) / cell_height,
                          (bbox[2] - tile.width / 2) / cell_height, 0.0]
                frames[key] = {"tile": tile, "mask": mask, "bbox": bbox, "normal_bbox": normal,
                               "source": atlas["file"], "source_sha256": sha(raw), "tile_box": list(box),
                               "nominal_tile_box": list(nominal), "cell_height": cell_height,
                               "component_pixels": component_pixels, "row": row, "column": column, "frame": frame}
    return frames, sources


def prepare_character(character, spec, frames, mapping):
    available = [(f, frames[character, f]) for f in FRAMES if (character, f) in frames]
    bounds = [min(v["normal_bbox"][0] for _, v in available), min(v["normal_bbox"][1] for _, v in available),
              max(v["normal_bbox"][2] for _, v in available), 0.0]
    padding = int(mapping.get("padding", 2))
    baseline = HEIGHT - padding
    scale = min((WIDTH - 2 * padding) / (bounds[2] - bounds[0]), (HEIGHT - 2 * padding) / -bounds[1])
    origin_x = WIDTH / 2 - (bounds[0] + bounds[2]) / 2 * scale
    scaled = []
    for frame, data in available:
        tile = data["tile"]
        ratio = data["cell_height"] / scale
        affine = (ratio, 0, tile.width / 2 - origin_x * ratio, 0, ratio, data["bbox"][3] - baseline * ratio)
        image = tile.transform((WIDTH, HEIGHT), Image.Transform.AFFINE, affine, resample=Image.Resampling.NEAREST,
                               fillcolor=(0, 0, 0, 0))
        alpha = image.getchannel("A").point(lambda a: 255 if a >= mapping["alpha_threshold"] else 0)
        image.putalpha(alpha)
        if alpha.getbbox() is None:
            raise ValueError("Sampling removed entire character")
        scaled.append((frame, image, data))
    # Quantize visible samples only; transparent RGB/edge matte cannot consume the palette.
    counts = Counter(pixel[:3] for _, im, _ in scaled for pixel in pixels(im) if pixel[3])
    samples = [rgb for rgb, count in counts.items() for _ in range(count)]
    strip = Image.new("RGB", (len(samples), 1)); strip.putdata(samples)
    quantized = strip.quantize(colors=PALETTE_SIZE - 1, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    colors = quantized.getpalette()
    used = sorted({index for _, index in quantized.getcolors()})
    visible565 = list(dict.fromkeys(rgb565(colors[i * 3:i * 3 + 3]) for i in used))
    palette = [0] + visible565
    actual_count = len(palette)
    palette += [0] * (PALETTE_SIZE - actual_count)
    expanded = [expand565(p) for p in palette]
    # Assign only indices 1..N. Opaque black must never become transparent index 0.
    lookup = {}
    for rgb in counts:
        lookup[rgb] = min(range(1, actual_count), key=lambda i: sum((rgb[j] - expanded[i][j]) ** 2 for j in range(3)))
    output = []
    for frame, image, data in scaled:
        indices = bytes(lookup[pixel[:3]] if pixel[3] else 0 for pixel in pixels(image))
        decoded = decode(indices, palette)
        output.append((frame, decoded, indices, data))
    # A per-character shared ROI referenced to the original idle tile, not an extra stored portrait bitmap.
    reference = frames[character, "idle"]
    portrait = spec.get("portrait_idle_cell_fraction", [0.10, 0.0, 0.90, 0.65])
    tile = reference["tile"]
    nominal = reference["nominal_tile_box"]; cell_w = nominal[2] - nominal[0]; cell_h = nominal[3] - nominal[1]
    px0, py0, px1, py1 = (nominal[0] + portrait[0] * cell_w - reference["tile_box"][0],
        nominal[1] + portrait[1] * cell_h - reference["tile_box"][1],
        nominal[0] + portrait[2] * cell_w - reference["tile_box"][0],
        nominal[1] + portrait[3] * cell_h - reference["tile_box"][1])
    cell_height = reference["cell_height"]
    box = [math.floor(origin_x + (px0 - tile.width / 2) / cell_height * scale),
           math.floor(baseline + (py0 - reference["bbox"][3]) / cell_height * scale),
           math.ceil(origin_x + (px1 - tile.width / 2) / cell_height * scale),
           math.ceil(baseline + (py1 - reference["bbox"][3]) / cell_height * scale)]
    box = [max(0, box[0]), max(0, box[1]), min(WIDTH, box[2]), min(HEIGHT, box[3])]
    # Atlases may draw an ear/helmet above the nominal row; include the real
    # crown in every face animation instead of silently flattening the head.
    portrait_frames = [im for frame, im, _, _ in output if frame in FRAMES[:3]]
    box[1] = min(box[1], min(im.getchannel("A").getbbox()[1] for im in portrait_frames))
    if not (0 <= box[0] < box[2] <= WIDTH and 0 <= box[1] < box[3] <= HEIGHT):
        raise ValueError("Invalid portrait bounds")
    return output, palette, {"id": character, "name": spec["name"], "slug": spec["slug"],
        "normalized_union_bbox": bounds, "scale_pixels_per_cell_height": scale, "origin_x": origin_x,
        "baseline_exclusive_y": baseline, "portrait_crop": [box[0], box[1], box[2] - box[0], box[3] - box[1]],
        "palette_count": PALETTE_SIZE, "used_palette_count": actual_count,
        "palette_rgb565": palette, "palette_rgba8888": [[*expanded[i], 0 if i == 0 else 255] for i in range(PALETTE_SIZE)]}


def decode(indices, palette):
    if len(indices) != WIDTH * HEIGHT or len(palette) != PALETTE_SIZE or any(i >= PALETTE_SIZE for i in indices):
        raise ValueError("Invalid indexed sprite")
    im = Image.new("RGBA", (WIDTH, HEIGHT))
    im.putdata([(*expand565(palette[i]), 255) if i else (0, 0, 0, 0) for i in indices])
    return im


def contact_sheet(records, folder, filename="contact-sheet.png"):
    frames_present = [f for f in FRAMES if any(r["frame"] == f for r in records)]
    ids = sorted({r["character"] for r in records})
    zoom, gap, label = 4, 14, 30
    cell_w, cell_h = WIDTH * zoom + gap, HEIGHT * zoom + label + gap
    sheet = Image.new("RGB", (cell_w * len(frames_present) * 2 + gap, cell_h * len(ids) + gap), "#edf1f3")
    draw = ImageDraw.Draw(sheet)
    try: font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 18)
    except OSError: font = ImageFont.load_default()
    lookup = {(r["character"], r["frame"]): r for r in records}
    for row, character in enumerate(ids):
        for col, frame in enumerate(frames_present):
            if (character, frame) not in lookup: continue
            record = lookup[character, frame]
            with Image.open(folder / record["png"]) as opened: sprite = opened.convert("RGBA").resize((WIDTH * zoom, HEIGHT * zoom), Image.Resampling.NEAREST)
            for side, background in enumerate(("#10242e", "#f8f2e6")):
                x, y = gap + (col + side * len(frames_present)) * cell_w, gap + row * cell_h
                tile = Image.new("RGBA", sprite.size, background); tile.alpha_composite(sprite)
                sheet.paste(tile.convert("RGB"), (x, y)); draw.text((x, y + HEIGHT * zoom + 4), f"{record['slug']} / {frame}", fill="#24323d", font=font)
    sheet.save(folder / filename)


def portrait_contact_sheet(manifest, folder):
    """Review the exact shared ROI; this is a preview, not another device bitmap."""
    zoom, gap, cw, ch = 3, 12, 210, 214
    records = {(r["character"], r["frame"]): r for r in manifest["frames"]}
    characters = manifest["characters"]
    sheet = Image.new("RGB", (6 * cw + gap, len(characters) * ch + gap), "#edf1f3")
    draw = ImageDraw.Draw(sheet)
    try: font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 16)
    except OSError: font = ImageFont.load_default()
    for row, character in enumerate(characters):
        x, y, w, h = character["portrait_crop"]
        for col, frame in enumerate(FRAMES[:3]):
            if (character["id"], frame) not in records: continue
            with Image.open(folder / records[character["id"], frame]["png"]) as opened:
                portrait = opened.convert("RGBA").crop((x, y, x + w, y + h)).resize((w * zoom, h * zoom), Image.Resampling.NEAREST)
            for side, background in enumerate(("#10242e", "#f8f2e6")):
                left, top = gap + (side * 3 + col) * cw, gap + row * ch
                tile = Image.new("RGBA", (cw - gap, ch - 34), background)
                tile.alpha_composite(portrait, ((tile.width - portrait.width) // 2, (tile.height - portrait.height) // 2))
                sheet.paste(tile.convert("RGB"), (left, top))
                draw.text((left, top + ch - 30), f"{character['slug']} {frame} {w}x{h}", fill="#24323d", font=font)
    sheet.save(folder / "contact-portraits.png")


def emit_firmware(manifest, indices_blob, palettes_blob, directory):
    records = {(r["character"], r["frame"]): r for r in manifest["frames"]}
    if len(records) != 60 or any((i, f) not in records for i in range(12) for f in FRAMES):
        raise ValueError("Firmware generation requires all 12 characters x 5 source frames; no placeholders")
    header = """#pragma once
#include <stdint.h>
namespace deskart {
enum class Frame:uint8_t { Idle=0, Blink=1, Happy=2, WalkA=3, WalkB=4 };
struct Sprite { const uint8_t* indices; const uint16_t* palette;
  uint8_t width,height,paletteCount,transparentIndex;
  uint8_t portraitX,portraitY,portraitW,portraitH; };
const Sprite& get(uint8_t character,Frame frame);
}
"""
    lines = ['// Generated by scripts/compile-companion-art.py. Source provenance is in the compiled manifest.', '#include "SpriteArt.h"', 'namespace deskart { namespace {']
    for c in manifest["characters"]:
        lines.append(f"const uint16_t palette_{c['id']}[32]={{" + ",".join(f"0x{x:04X}" for x in c["palette_rgb565"]) + "};")
    for i in range(12):
        for frame in FRAMES:
            record = records[i, frame]; offset = record["indices_offset"]
            data = indices_blob[offset:offset + WIDTH * HEIGHT]
            lines.append(f"const uint8_t pixels_{i}_{frame}[3840]={{")
            lines.extend(",".join(str(b) for b in data[n:n + 64]) + "," for n in range(0, len(data), 64))
            lines.append("};")
    lines.append("const Sprite sprites[12][5]={")
    chars = {c["id"]: c for c in manifest["characters"]}
    for i in range(12):
        portrait = ",".join(str(v) for v in chars[i]["portrait_crop"])
        lines.append("{" + ",".join(f"{{pixels_{i}_{f},palette_{i},64,60,32,0,{portrait}}}" for f in FRAMES) + "},")
    lines += ["};", "} // anonymous namespace", "const Sprite& get(uint8_t character,Frame frame){", "  const uint8_t f=static_cast<uint8_t>(frame);", "  return sprites[character<12?character:0][f<5?f:0];", "}", "} // namespace deskart", ""]
    source = "\n".join(lines)
    for c in manifest["characters"]:
        found = re.search(rf"palette_{c['id']}\[32\]=\{{(.*?)\}};", source)
        recovered = struct.pack("<32H", *[int(x, 16) for x in re.findall(r"0x[0-9A-F]+", found.group(1))])
        if recovered != palettes_blob[c["palette_offset"]:c["palette_offset"] + 64]:
            raise AssertionError("C++ palette initializer roundtrip mismatch")
    # Read the actual generated decimal C++ initializer back; do not trust only the in-memory array.
    for i in range(12):
        for frame in FRAMES:
            found = re.search(rf"pixels_{i}_{frame}\[3840\]=\{{(.*?)\}};", source, re.S)
            recovered = bytes(int(x) for x in re.findall(r"\d+", found.group(1)))
            record = records[i, frame]
            if recovered != indices_blob[record["indices_offset"]:record["indices_offset"] + WIDTH * HEIGHT]:
                raise AssertionError("C++ initializer roundtrip mismatch")
    directory.mkdir(parents=True, exist_ok=True)
    (directory / "SpriteArt.h").write_text(header, "utf-8")
    (directory / "SpriteArt.cpp").write_text(source, "utf-8")
    return {name: sha((directory / name).read_bytes()) for name in ("SpriteArt.h", "SpriteArt.cpp")}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mapping", type=Path, default=ROOT / "assets/companions/r2/atlas-map.json")
    parser.add_argument("--output", type=Path, default=ROOT / "assets/companions/r2/compiled")
    parser.add_argument("--emit-firmware", action="store_true")
    parser.add_argument("--firmware-dir", type=Path, default=ROOT / "firmware/DeskController")
    args = parser.parse_args()
    mapping = json.loads(args.mapping.read_text("utf-8"))
    if mapping.get("schema_version") != 1: raise ValueError("Unknown atlas mapping schema")
    assets = args.mapping.parent.resolve()
    frames, sources = collect(mapping, assets)
    if not frames: raise ValueError("No atlas source available")
    specs = {c["id"]: c for c in mapping["characters"]}
    args.output.mkdir(parents=True, exist_ok=True)
    manifest = {"schema_version": 1, "compiler_sha256": sha(Path(__file__).read_bytes()), "mapping_sha256": sha(args.mapping.read_bytes()),
        "pillow_version": PIL.__version__, "dimensions": [WIDTH, HEIGHT], "palette_size": PALETTE_SIZE,
        "transparent_index": 0, "alpha_threshold": mapping["alpha_threshold"], "sampling": "nearest-neighbour",
        "palette_method": "shared character median-cut 31 visible colors; nearest RGB565 palette; no dithering",
        "rgb565_decode": "bit replication: r5<<3|r5>>2, g6<<2|g6>>4, b5<<3|b5>>2",
        "extraction": "8-connected alpha>=threshold whole silhouettes, assigned by row/column; nominal grid is reference only",
        "alignment": "per-frame silhouette center X and ground Y; one normalized-cell-height scale per character across all five frames",
        "source_pngs_modified": False, "sources": sources, "characters": [], "frames": []}
    indices_blob, palettes_blob = bytearray(), bytearray()
    checks = 0
    for character in sorted({key[0] for key in frames}):
        output, palette, info = prepare_character(character, specs[character], frames, mapping)
        info["palette_offset"] = len(palettes_blob)
        palettes_blob.extend(struct.pack("<32H", *palette)); manifest["characters"].append(info)
        for frame, decoded, indices, data in output:
            name = f"{character:02d}-{info['slug']}-{frame}.png"
            decoded.save(args.output / name)
            with Image.open(args.output / name) as reopened:
                if reopened.convert("RGBA").tobytes() != decode(indices, palette).tobytes():
                    raise AssertionError("Saved PNG differs from RGB565/index decode")
            for index, pixel in zip(indices, pixels(decoded)):
                if index and (pixel[3] != 255 or rgb565(pixel[:3]) != palette[index]):
                    raise AssertionError("Decoded PNG cannot recover device RGB565 value")
                if not index and pixel != (0, 0, 0, 0): raise AssertionError("Transparency mismatch")
                checks += 1
            manifest["frames"].append({"character": character, "slug": info["slug"], "frame": frame,
                "source": data["source"], "source_sha256": data["source_sha256"], "tile_box": data["tile_box"],
                "nominal_tile_box": data["nominal_tile_box"], "source_component_pixels": data["component_pixels"],
                "source_alpha_bbox": list(data["bbox"]), "source_normalized_bbox": data["normal_bbox"],
                "indices_offset": len(indices_blob), "indices_bytes": len(indices), "indices_sha256": sha(indices),
                "png": name, "png_sha256": sha((args.output / name).read_bytes()), "visible_pixels": sum(i != 0 for i in indices)})
            indices_blob.extend(indices)
    (args.output / "companions.indices.bin").write_bytes(indices_blob)
    (args.output / "companions.palettes.rgb565.bin").write_bytes(palettes_blob)
    # Read back combined binaries, independently decode every frame and compare saved PNG pixels.
    raw_indices = (args.output / "companions.indices.bin").read_bytes()
    raw_palettes = (args.output / "companions.palettes.rgb565.bin").read_bytes()
    infos = {c["id"]: c for c in manifest["characters"]}
    for record in manifest["frames"]:
        palette = struct.unpack_from("<32H", raw_palettes, infos[record["character"]]["palette_offset"])
        index = raw_indices[record["indices_offset"]:record["indices_offset"] + WIDTH * HEIGHT]
        with Image.open(args.output / record["png"]) as saved:
            if decode(index, palette).tobytes() != saved.convert("RGBA").tobytes(): raise AssertionError("Binary readback mismatch")
    manifest["missing_frames"] = [{"character": i, "frame": f} for i in range(12) for f in FRAMES if (i, f) not in frames]
    manifest["complete"] = not manifest["missing_frames"]
    manifest["budget"] = {"indices_bytes": len(indices_blob), "palette_bytes": len(palettes_blob),
        "arm_sprite_table_bytes": len(manifest["frames"]) * 16, "complete_set_data_estimate_bytes": 12 * 5 * 64 * 60 + 12 * 32 * 2 + 12 * 5 * 16,
        "maximum_data_bytes": 260 * 1024}
    if manifest["budget"]["complete_set_data_estimate_bytes"] >= 260 * 1024: raise AssertionError("Flash data budget exceeded")
    manifest["verification"] = {"png_pixel_checks": checks, "binary_readback": "PASS", "rgb565_roundtrip": "PASS"}
    manifest["motion_differences"] = []
    for character in sorted(infos):
        frame_records = {r["frame"]: r for r in manifest["frames"] if r["character"] == character}
        if "walk_a" not in frame_records or "walk_b" not in frame_records: continue
        a, b = (raw_indices[frame_records[f]["indices_offset"]:frame_records[f]["indices_offset"] + WIDTH * HEIGHT] for f in ("walk_a", "walk_b"))
        difference = sum(x != y for x, y in zip(a, b))
        lower = sum(x != y for x, y in zip(a[WIDTH * 40:], b[WIDTH * 40:]))
        if not difference or not lower: raise AssertionError("Walk frames lack visible whole/lower-body variation")
        manifest["motion_differences"].append({"character": character, "walk_changed_pixels": difference, "walk_lower_20_rows_changed_pixels": lower})
    if args.emit_firmware:
        manifest["firmware_sha256"] = emit_firmware(manifest, raw_indices, raw_palettes, args.firmware_dir)
        manifest["verification"]["cpp_initializer_readback"] = "PASS"
    for source in sources:
        if sha((assets / source["file"]).read_bytes()) != source["sha256"]: raise AssertionError("Source PNG changed")
    contact_sheet(manifest["frames"], args.output)
    for group, ids in (("woodland", (2, 8, 4, 9)), ("fantasy", (3, 5, 6, 7)), ("classic", (0, 1, 10, 11))):
        subset = [r for r in manifest["frames"] if r["character"] in ids]
        if subset: contact_sheet(subset, args.output, "contact-" + group + ".png")
    portrait_contact_sheet(manifest, args.output)
    manifest["contact_sheet_sha256"] = sha((args.output / "contact-sheet.png").read_bytes())
    (args.output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", "utf-8")
    print(json.dumps({"frames": len(manifest["frames"]), "complete": manifest["complete"], "verification": manifest["verification"], "budget": manifest["budget"]}, indent=2))


if __name__ == "__main__":
    main()
