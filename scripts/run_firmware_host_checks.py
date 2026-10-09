"""Compile selected current firmware modules against synthetic host fakes.

Default: compilation only. --run explicitly runs the resulting native fixtures
where local application-control policy permits. Never bypass a blocked binary.
No hardware, serial, Bluetooth, real filesystem image or upload is involved.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
FW = ROOT / "firmware/DeskController"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cxx", default=os.environ.get("CXX", "c++"),
                        help="Compiler executable; Zig is supported via zig c++")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "build/firmware-host")
    parser.add_argument("--run", action="store_true", help="Explicitly execute compiled host fixtures")
    args = parser.parse_args()
    out = args.output_dir.resolve()
    out.mkdir(parents=True, exist_ok=True)
    compiler = [args.cxx]
    if Path(args.cxx).stem.lower() == "zig":
        compiler.append("c++")
    cases = [
        ("controller-core", "controller_core_test.cpp", [], []),
        ("battery-gauge", "battery_gauge_test.cpp", [], []),
        ("hub-config", "hub_config_test.cpp", [ROOT / "tests/hub-store-fakes"], []),
        ("usb-console", "r3d6_usb_console_test.cpp", [ROOT / "tests/usb-console-stubs"], [FW / "UsbConsole.cpp"]),
    ]
    result = {"schema": 1, "hardware_access": False, "native_run_requested": args.run,
              "scope": "Selected logic and API-fake checks; not a full controller/BSP/hardware suite", "cases": []}
    for name, source, includes, extra in cases:
        exe = out / (name + (".exe" if os.name == "nt" else ""))
        command = compiler + ["-std=c++17", "-O1", "-I" + str(FW)]
        command += ["-I" + str(path) for path in includes]
        command += [str(ROOT / "tests" / source)] + [str(path) for path in extra] + ["-o", str(exe)]
        run = subprocess.run(command, cwd=ROOT, text=True, encoding="utf-8", errors="replace", capture_output=True)
        (out / (name + "-compile.log")).write_text(run.stdout + run.stderr, encoding="utf-8")
        entry = {"name": name, "compile_exit_code": run.returncode, "native_executed": False}
        result["cases"].append(entry)
        if run.returncode:
            print(run.stdout + run.stderr, flush=True)
            break
        entry["binary_sha256"] = hashlib.sha256(exe.read_bytes()).hexdigest()
        if args.run:
            try:
                executed = subprocess.run([str(exe)], cwd=ROOT, text=True, encoding="utf-8",
                                          errors="replace", capture_output=True)
                entry.update(native_executed=True, run_exit_code=executed.returncode)
                (out / (name + "-run.log")).write_text(executed.stdout + executed.stderr, encoding="utf-8")
                if executed.returncode:
                    break
            except OSError as error:
                entry["execution_error"] = str(error)
                break
        print(name + ": compiled" + (" and executed" if entry["native_executed"] else "; not executed"), flush=True)
    ok = len(result["cases"]) == len(cases) and all(
        entry["compile_exit_code"] == 0 and (not args.run or entry.get("run_exit_code") == 0)
        for entry in result["cases"])
    result["status"] = ("PASS" if args.run else "COMPILE_ONLY_PASS") if ok else "FAIL_OR_BLOCKED"
    result["native_fixtures_executed"] = sum(entry["native_executed"] for entry in result["cases"])
    (out / "result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
