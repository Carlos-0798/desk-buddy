Desk Buddy R3 dedicated portraits

These are new isolated heads/busts generated for this project. They do not crop or
replace the existing R2 body sprites. All original PNGs are preserved byte-for-byte.

Active sources (three columns: idle, blink, happy; four rows each):
  woodland-portraits-original.png: Cat2, Fox8, Bunny4, Panda9.
  fantasy-portraits-original.png: Robot3, Ghost5, Mochi6, Orbit7.
  classic-portraits-v2-original.png: Pixel0, Round1, Sleepy10, Wink11.
The first classic-portraits-original.png is retained but unused: adjacent Sleepy
heads were physically joined in its alpha mask. V2 introduces transparent spacing.
atlas-map.json is the authoritative source/character/frame mapping.

Rebuild from the project root with Python and Pillow:
  python scripts/compile-companion-portraits.py --emit-firmware
  python tests/portrait_compiler_test.py
No other libraries, network connection or hardware are required. Omitting
--emit-firmware writes only preview assets and manifest. Firmware generation fails
unless all twelve characters have all three real source frames. There are no
invented/doubled placeholders. The compiler does not write any R2 files.

Extraction and color contract
  Alpha threshold is 128. Source interiors commonly use alpha 253 rather than 255;
  requiring opaque 255 would erase them. Low-alpha halos are not retained.
  Whole 8-connected silhouettes define the actual crop. Nominal grid coordinates
  assign row and column only. Ambiguous joined/extra large components fail rather
  than silently cutting ears, hats or another character into a frame.
  Every character uses one common scale across all three frames, in normalized
  source-cell-height units. Silhouette center X=24 and exclusive bottom Y=29 are
  shared. One-pixel margins preserve the complete outline on the 48x30 canvas.
  Sampling is nearest-neighbor. No dithering, creative painting or invented
  outlines are performed by the compiler.
  Each character has one 32-entry RGB565 palette shared across all three frames.
  Index 0 is transparent; indices 1..31 are visible colors, including opaque black.
  Pixels are 8-bit row-major. PNGs are decoded from the final device palette/index
  data, use binary alpha, and expand RGB565 by bit replication. WPF consumes these
  same 48x30 PNGs at 2x; no body-sprite portrait cropping is required.

Firmware contract
  PortraitArt.h includes SpriteArt.h for deskart::Frame and exposes:
    namespace deskportrait {
      struct Sprite { const uint8_t* indices; const uint16_t* palette;
        uint8_t width,height,paletteCount,transparentIndex; };
      const Sprite& get(uint8_t character,deskart::Frame frame);
    }
  Idle/Blink/Happy map to indices 0/1/2. WalkA/WalkB/invalid frames return Idle;
  invalid character IDs return character 0. All data is const; no framebuffer or
  heap copy is part of this generated asset API.

Files
  compiled/NN-slug-frame.png: 36 exact RGB565-decoded RGBA portraits.
  compiled/manifest.json: source/compiler/mapping hashes; actual/nominal crop
  coordinates; common scale, palette, frame offsets and hashes; verification.
  compiled/portraits.indices.bin and portraits.palettes.rgb565.bin: indices and
  little-endian palette values. Offsets are explicitly recorded in the manifest.
  compiled/contact-{woodland,fantasy,classic}.png: dark/light nearest-neighbor
  contact previews, enlarged for inspection, not real-device screenshots.
  firmware/DeskController/PortraitArt.h and PortraitArt.cpp: generated device data.

Validation and limits
  The compiler checks 51,840 decoded pixel positions, saved binary readback and
  generated C++ index AND palette initializers. Source PNG hashes are rechecked.
  Estimated 32-bit ARM data size: 51,840 indices + 768 palettes + 432 table = 53,040 bytes.
  Combined body + portrait data estimate is 285,168 bytes; firmware target linking
  establishes total Flash/RAM separately, rather than assuming this sum is the
  full application size. All earlier R2 artifacts remain independently rebuildable.
  Host contract tests cover all 65,536 RGB565 values, transparency vs opaque black,
  threshold boundaries, joined components, stable scaling, malformed index data,
  source-path rejection, missing-frame no-overwrite behavior and final hashes.
  These checks do not certify physical TFT color, animation rate, responsiveness
  or hardware acceptance. Firmware build, WPF rendering and physical tests have
  separate evidence records under outputs/hub-r3.
