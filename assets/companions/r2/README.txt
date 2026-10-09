Desk Buddy companion sprites — R2 art revision

Original assets generated for this project with the built-in imagegen tool.
Original PNG atlases are preserved. The generated drawings are not a strict regular
grid: some feet, hats and helmets cross nominal cell boundaries. The compiler therefore
finds the complete 8-connected alpha silhouette of each character before assigning it
to the nominal row/column. It never slices a character at an equal-grid boundary. The
actual crop and nominal cell coordinates are both recorded in compiled/manifest.json.
These conversions are asset compilation, not a claim that the source atlas is itself
native 64-pixel hand-drawn art.

References studied for general technique only (their images/assets are not copied):
https://www.slynyrd.com/blog/2018/1/10/pixelblog-1-color-palettes
https://www.slynyrd.com/blog/2018/8/19/pixelblog-8-intro-to-animation
https://saint11.art/pixel_articles/

Principles applied: coherent color clusters, warm highlights/cool shadows, hue-shifted
ramps, clear silhouette, compact body proportions, matched scale/baseline across frames,
and expressive idle/blink/happy keyframes rather than increasing frame count alone.

Sources and character IDs
  Woodland rows: Cat=2, Fox=8, Bunny=4, Panda=9.
  Fantasy rows: Robot=3, Ghost=5, Mochi=6, Orbit=7.
  Classic rows: Pixel=0 (game-console buddy), Round=1 (chick), Sleepy=10, Wink=11.
  Each *-atlas-original.png has 3 columns: idle, blink, happy.
  Each selected *-walk-original.png has 2 columns: walk_a, walk_b.
  Woodland walking uses woodland-walk-v2-original.png. The previous
  woodland-walk-original.png is retained for provenance but is not compiled.
  atlas-map.json is the authoritative source/frame/character mapping.

Compilation
  Run from the project root with Python and Pillow available:
    python scripts/compile-companion-art.py --emit-firmware
  Without --emit-firmware, only compiled PNGs/binaries/previews/manifest are written.
  Firmware generation requires all 12 characters and all 5 real frames; missing frames
  cause an error rather than duplicated frames or generated placeholders.

  Alpha is thresholded explicitly at 128, as source interiors commonly use alpha 253
  rather than 255. Low-alpha glow/fringe is excluded. Only the 8/12 large connected
  silhouettes are selected from each atlas; an ambiguous extra large component fails
  the build. No painting, invented outlines, image generation or dithering is done by
  the compiler. The original PNG file bytes are checked unchanged after compilation.

  Each character uses one scale in normalized source-cell-height coordinates across
  all five frames. Actual visible silhouette centers share the X center, and visible
  bottoms share a ground line at exclusive Y=58, leaving a 2-pixel bottom margin.
  Source cells may have different sizes/aspect ratios; scaling preserves aspect ratio.
  This is nearest-neighbor sampling onto a 64x60 canvas, not per-frame resizing to fill.
  A shared portrait crop is referenced to the idle source and expanded upward to include
  the real idle/blink/happy crown. It is metadata, not another stored device bitmap.

  The five frames share one 32-entry RGB565 palette per character: index 0 is fully
  transparent; up to 31 entries are visible colors, including opaque black if present.
  Pixel indices are 8-bit row-major. Median-cut selects visible colors jointly; pixels
  then use the nearest RGB565 palette color without dithering. PNGs are decoded from
  those exact indices/palettes, with alpha only 0 or 255. Their RGB values use 5/6-bit
  bit replication and therefore exactly match the device color values.

Outputs and verification
  firmware/DeskController/SpriteArt.h and SpriteArt.cpp: deskart::Sprite/get API,
  const Flash data (12x5x3840 index bytes plus palettes and sprite table).
  compiled/NN-slug-frame.png: exact decoded device pixels for WPF Resource use.
  compiled/companions.indices.bin and companions.palettes.rgb565.bin: little-endian
  palette values and concatenated indexed frames; offsets are in the manifest.
  compiled/manifest.json: source hashes, source alpha statistics, crops, shared scale,
  palette, byte counts, PNG hashes, compiler/mapping hashes, and verification results.
  compiled/contact-{woodland,fantasy,classic}.png: dark/light whole-body previews.
  compiled/contact-portraits.png: the same portrait crop across three face frames.

  All 230400 decoded pixel positions are checked, saved binaries are read back, and
  actual generated C++ index AND palette initializers are parsed back and compared.
  The 32-bit ARM data estimate is 232128 bytes: 230400 indices + 768 palette + 960 table.
  This is below the 260 KiB sprite-data budget; the target linker establishes total
  firmware Flash/RAM use separately. A changed-pixel count detects duplicate walking
  frames, but visual review establishes whether the feet/ghost tail actually alternate.

Evidence boundary
  These checks establish source preservation, pixel equivalence and data bounds.
  They do not establish physical TFT colors, visible frame rate, motion smoothness,
  input latency, RAM margin under live transport, or complete hardware acceptance.
  WPF and firmware consume the same pixel art, but their rendering paths are tested
  separately. Firmware target build and physical display checks belong to their own
  evidence records, not this asset manifest.
