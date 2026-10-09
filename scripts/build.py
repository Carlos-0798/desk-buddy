"""Compile DeskController with Arduino CLI. This script never uploads firmware."""
import argparse
from pathlib import Path
import json
import os
import re
import subprocess

ROOT=Path(__file__).resolve().parents[1]
FQBN="adafruit:nrf52:feather52840:softdevice=s140v6,debug=l0"
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--cli",default=os.environ.get("ARDUINO_CLI","arduino-cli"))
    p.add_argument("--config",type=Path,help="Optional existing Arduino CLI configuration")
    p.add_argument("--build-dir",type=Path,default=ROOT/"build/arduino")
    p.add_argument("--backlight-pin",type=int,choices=(-1,11),default=-1,
                   help="Default -1: do not drive LITE. Choose 11 only for confirmed D11-to-LITE wiring.")
    a=p.parse_args();out=a.build_dir.resolve();out.mkdir(parents=True,exist_ok=True)
    command=[a.cli]
    if a.config:command += ["--config-file",str(a.config.resolve())]
    command += ["compile","--fqbn",FQBN,"--library",str(ROOT/"firmware/libraries/DeskWire"),"--build-path",str(out/"objects"),"--output-dir",str(out/"artifacts")]
    if a.backlight_pin==11:command += ["--build-property","compiler.cpp.extra_flags=-DDESK_BACKLIGHT_PIN=11"]
    command += [str(ROOT/"firmware/DeskController")]
    run=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,encoding="utf-8",errors="replace")
    text=run.stdout+run.stderr
    (out/"compile.log").write_text(text,encoding="utf-8")
    print(text)
    flash=re.search(r"Sketch uses (\d+) bytes",text);ram=re.search(r"Global variables use (\d+) bytes",text)
    result={"status":"PASS" if run.returncode==0 else "FAIL","fqbn":FQBN,"uploaded":False,"physical_tested":False,
        "effective_backlight_pin":a.backlight_pin,"vendored_bounded_wire":True,
        "flash_bytes":int(flash.group(1)) if flash else None,"ram_bytes":int(ram.group(1)) if ram else None}
    (out/"result.json").write_text(json.dumps(result,indent=2)+"\n",encoding="utf-8")
    raise SystemExit(run.returncode)

if __name__=="__main__":main()
