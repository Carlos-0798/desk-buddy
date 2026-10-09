"""Compile-time checks with synthetic timestamps; never executes hardware or native fixtures."""
import argparse
import json
from pathlib import Path
import subprocess

ROOT=Path(__file__).resolve().parents[1]

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--cxx',default='zig',help='Zig or Clang C++ compiler executable')
    p.add_argument('--output-dir',type=Path,default=ROOT/'build/display-timeout-checks')
    args=p.parse_args();out=args.output_dir.resolve();out.mkdir(parents=True,exist_ok=True)
    compiler=[args.cxx]
    if Path(args.cxx).stem.lower()=='zig':compiler+=['c++']
    command=compiler+['-std=c++14','-fconstexpr-steps=10000000','-Wall','-Wextra','-Werror','-I',str(ROOT/'firmware/DeskController'),'-c',str(ROOT/'tests/r3h_custom_timeout_test.cpp'),'-o',str(out/'timeouts.obj')]
    run=subprocess.run(command,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace')
    (out/'compile.log').write_text(run.stdout+run.stderr,'utf-8')
    result={'status':'PASS' if run.returncode==0 else 'FAIL','method':'Compiler-evaluated actual State with synthetic timestamps','native_execution':False,'hardware_access':False,'real_long_interval_wait':False}
    (out/'result.json').write_text(json.dumps(result,indent=2)+'\n','utf-8')
    print(json.dumps(result));print(run.stdout+run.stderr,end='');raise SystemExit(run.returncode)

if __name__=='__main__':main()
