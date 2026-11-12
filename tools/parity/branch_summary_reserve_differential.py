#!/usr/bin/env python3
"""Run the same bounded settings-to-provider fixture in pinned Pi twice and PiSharp."""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
ROOT = pathlib.Path(__file__).resolve().parents[2]
TOOLS = ROOT / 'tools/parity'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    verify_pi_checkout(args.pi)
    pi = pathlib.Path(args.pi).resolve()
    out = pathlib.Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=True)
    cases = TOOLS / 'branch_summary_reserve_cases.json'
    for name in ['pi', 'calibration']:
        subprocess.run(['node', '--import', str(pi / 'packages/coding-agent/src/experimental/source-resolver.ts'),
                        str(TOOLS / 'branch_summary_reserve_oracle.mjs'), str(pi), str(cases), str(out / (name + '.json'))],
                       check=True, timeout=180)
    with tempfile.TemporaryDirectory(prefix='reserve-probe-', dir='/home/philip/.cache/tmp/opencode') as directory:
        work = pathlib.Path(directory)
        shutil.copyfile(TOOLS / 'branch_summary_reserve_probe.cs', work / 'Probe.cs')
        (work / 'Program.cs').write_text('using System.Text.Json;\nusing var input = JsonDocument.Parse(File.ReadAllText(args[0]));\n'
                                       'var results = new List<object>();\nforeach (var item in input.RootElement.GetProperty("cases").EnumerateArray()) '
                                       'results.Add(await BranchSummaryReserveProbe.ObserveAsync(item));\n'
                                       'Console.WriteLine(JsonSerializer.Serialize(results));\n')
        (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{ROOT}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
        subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--nologo', '-m:1'], check=True, timeout=300)
        sharp = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), str(cases)], timeout=180))
    pinned = json.loads((out / 'pi.json').read_text())
    calibration = json.loads((out / 'calibration.json').read_text())
    for results in [pinned['cases'], calibration['cases'], sharp]:
        for case in results:
            if case.get('raw', '<absent>') != '<absent>':
                case['raw'] = json.loads(case['raw'])
    differences = []
    for expected, actual in zip(pinned['cases'], sharp, strict=True):
        for key in expected.keys() | actual.keys():
            if key == 'conversation':
                continue  # Raw transcripts retained; compare selected semantic markers, not pre-existing serialization whitespace.
            if expected.get(key) != actual.get(key):
                differences.append(dict(id=expected['id'], field=key, pi=expected.get(key), pisharp=actual.get(key)))
    settings = ['loaded', 'parentPresent', 'reservePresent', 'raw', 'effective', 'contextWindow', 'budget', 'limiting', 'skips']
    settings_matches = sum(all(p.get(k) == s.get(k) for k in settings) for p, s in zip(pinned['cases'], sharp))
    report = dict(piSha=pinned['piSha'], pisharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip(),
                  pi=pinned['cases'], calibration=calibration['cases'], pisharp=sharp,
                  settingsMatches=settings_matches, settingsDivergences=len(sharp)-settings_matches,
                  runtimeMatches=len(sharp)-len({d['id'] for d in differences}), differences=differences)
    (out / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    print(f"Settings: {settings_matches}/{len(sharp)}; complete request/navigation matches: {report['runtimeMatches']}/{len(sharp)}; calibration exact: {pinned == calibration}")
    print(json.dumps(differences, indent=2))


if __name__ == '__main__':
    main()
