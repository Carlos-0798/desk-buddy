# R6 enclosure CAD — original print revision

The first enclosure was **printed and assembled by October 8, 2026**. These files preserve the original R6 print geometry. **The USB opening does not align with the actual Feather/TFT orientation.** The assembled prototype uses a 180-degree display rotation and an internally connected USB cable exiting the battery-side opening; this workaround is not incorporated into these meshes. Read the [as-built record](../docs/as-built.md) before reusing the model.

The selected full-enclosure print set contains six pieces:

| File | Quantity | Exported bounds, mm |
| --- | ---: | --- |
| [10_base.stl](stl/10_base.stl) | 1 | 160 × 166 × 60 |
| [11_lid.stl](stl/11_lid.stl) | 1 | 160 × 166 × 5.4, including inward ribs |
| [12_tft_spacer_3mm.stl](stl/12_tft_spacer_3mm.stl) | 4 | 4.4 × 4.4 × 3 |

Nominal closed-enclosure size is **160 × 166 × 62.4 mm**, excluding the external knob and screw heads. Do not add the two exported part heights: the lid ribs extend into the base. Import at **100%, millimetres**; do not scale to fit the printer.

See [mechanical design](../docs/mechanical-design.md) for the design history, mounting hardware, clearance assumptions, and remaining checks. Enclosure closure and a powered front photograph are recorded; connector retention, cable strain relief and a full closed-case functional regression are not established.

## Rebuild

From the repository root, with Python 3.11 or later:

```powershell
python -m venv .venv-cad
.venv-cad\Scripts\python.exe -m pip install -r cad/source/requirements.txt
.venv-cad\Scripts\python.exe cad/source/build_enclosure.py
```

This writes to `cad/generated/`, leaving the reference STL files in `cad/stl/` unchanged. The script resolves its parameter file relative to its own location and can be called from another working directory. To choose a separate output folder:

```powershell
.venv-cad\Scripts\python.exe cad/source/build_enclosure.py --output-dir cad/rebuilt
```

The generator creates all seven historical part types: the three final enclosure types above plus four optional fit/tolerance coupons. It also produces the same geometry as 3MF, mounting coordinates, a simplified preview, and a mesh manifest. The 3MF output contains geometry only, not printer settings or G-code. Generated coupons are available for risk reduction; no coupon has been physically validated.

The mesh assertions check watertightness, consistent winding, positive volume, one connected component, and dimensions below the nominal 180 mm build extent. They do not validate slicing, printer reserved areas, material shrinkage, hardware mating, connector access, cable movement, or load capacity. Never include orange preview keep-outs in a print job.

## Reference data and reproducibility

- [parameters.json](source/parameters.json): the original R6 nominal dimensions and component placement parameters; not a revised model of the final cable arrangement.
- [mount-coordinates-mm.csv](source/mount-coordinates-mm.csv): 26 R6 fastener axes. X is right, Y is rear/USB, and Z is up. `reference_z_mm` is a model reference plane, not screw length, thread engagement, or drilling depth.
- [R4 solder reference](reference-r4/solder-42-points.csv): 42 unique assembly joints. Its historical world coordinates use X right, **Y up**, Z rear; they are not R6 drilling coordinates. Board/hole/net identities are the relevant wiring reference.
- [rebuild-check.json](qa/rebuild-check.json): source and final STL hashes, exact rebuild comparison, scope, and unresolved fit limits. Rebuilding with the recorded dependency versions reproduced all seven historical STL files byte for byte; only the three final types are included prebuilt here.

No original vendor PCB CAD, private device logs, flash backups, pairing records, or machine-specific absolute source paths are included. The model uses nominal dimensions derived from manufacturer references and the project's own layout decisions. Preview hardware is simplified; it is not a full mechanical model of every real lead, solder joint, or cable.
