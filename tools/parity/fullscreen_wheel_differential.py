#!/usr/bin/env python3
"""Execute pinned Pi twice and the identical PiSharp wheel corpus; retain raw results."""
import argparse
import json
import pathlib
import shutil
import subprocess
import tempfile
from fullscreen_wheel_cases import corpus, PIN
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
ROOT = pathlib.Path(__file__).resolve().parents[2]
TOOLS = ROOT / 'tools/parity'


def compare(pi, sharp):
    differences = []
    for group in ['settings', 'sequences', 'viewports', 'overlays']:
        for p, s in zip(pi[group], sharp[group], strict=True):
            if group == 'settings':
                keys = ['loaded','effective','current','label','description','options']
            elif group == 'viewports':
                # Different editor/footer heights are outside this setting. Compare relative wheel movement,
                # retaining each renderer's raw offsets and available range for independent boundary checks.
                p = dict(id=p['id'], events=[{k:e[k] for k in ['lines','multiplier','movement']} for e in p['events']])
                s = dict(id=s['id'], events=[{k:e[k] for k in ['lines','multiplier','movement']} for e in s['events']])
                keys = ['events']
            else:
                keys = p.keys()
            fields = [k for k in keys if p.get(k) != s.get(k)]
            if fields:
                differences.append(dict(group=group,id=p['id'],fields=fields))
    return differences


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi',required=True)
    parser.add_argument('--output',required=True)
    args = parser.parse_args()
    verify_pi_checkout(args.pi)
    pi = pathlib.Path(args.pi).resolve()
    out = pathlib.Path(args.output).resolve()
    out.mkdir(parents=True,exist_ok=True)
    (out/'cases.json').write_text(json.dumps(corpus(),indent=2)+'\n')
    for name in ['pi','calibration']:
        subprocess.run(['node','--import',str(pi/'packages/coding-agent/src/experimental/source-resolver.ts'),
                        str(TOOLS/'fullscreen_wheel_oracle.mjs'),str(pi),str(out/'cases.json'),str(out/(name+'.json'))],
                       check=True,timeout=180)
    with tempfile.TemporaryDirectory(prefix='wheel-probe-',dir='/home/philip/.cache/tmp/opencode') as d:
        work = pathlib.Path(d)
        shutil.copyfile(TOOLS/'fullscreen_wheel_probe.cs',work/'Probe.cs')
        shutil.copyfile(ROOT/'tests/PiSharp.Tests/TestInfrastructure/ManualTimeProvider.cs',work/'Clock.cs')
        (work/'Program.cs').write_text('using System.Text.Json;\nusing var input = JsonDocument.Parse(File.ReadAllText(args[0]));\n'
                                     'Console.WriteLine(JsonSerializer.Serialize(await FullscreenWheelProbe.ObserveAsync(input.RootElement)));\n')
        (work/'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="{ROOT}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
        subprocess.run(['dotnet','build',str(work/'probe.csproj'),'--nologo','-m:1'],check=True,timeout=300)
        sharp=json.loads(subprocess.check_output(['dotnet',str(work/'bin/Debug/net10.0/PiSharp.Tests.dll'),str(out/'cases.json')],timeout=180))
    pinned=json.loads((out/'pi.json').read_text())
    calibration=json.loads((out/'calibration.json').read_text())
    differences=compare(pinned,sharp)
    report=dict(piSha=PIN,pisharpSha=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),
                corpus=corpus(),pi=pinned,calibration=calibration,pisharp=sharp,differences=differences)
    (out/'report.json').write_text(json.dumps(report,indent=2)+'\n')
    for group in ['settings','sequences','viewports','overlays']:
        count=len(pinned[group]); failures=sum(d['group']==group for d in differences)
        print(f'{group}: {count-failures} matches / {failures} divergences')
    print('Same-Pi exact:',pinned==calibration)
    print(json.dumps(differences,indent=2))


if __name__ == '__main__':
    main()
