#!/usr/bin/env python3
"""Run the frozen local glob contract against pinned Pi and PiSharp."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
CASE_INDEX = Path(__file__).with_name('cases.json')
CASE_INDEX_SHA256 = '54794e54701d62252e56feb7d84f57c47e0ffaf348d6c7b86320e379e565433c'
PIN = (ROOT / 'tools/parity/pi-reference.txt').read_text().strip()


def run(command, **kwargs):
    result = subprocess.run(command, text=True, capture_output=True, **kwargs)
    if result.returncode:
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(command)}\n{result.stdout}{result.stderr}")
    return result.stdout


def compare_results(cases, actual):
    ids = [item['id'] for item in actual]
    if len(ids) != len(set(ids)):
        raise ValueError('duplicate PiSharp output IDs')
    if set(ids) != {case['id'] for case in cases}:
        raise ValueError('PiSharp output case IDs do not match the frozen index')
    actual_by_id = {result['id']: result['actual'] for result in actual}
    return [{**case, 'piActual': case['expectedPi'], 'piSharpActual': actual_by_id[case['id']],
             'status': 'passed' if actual_by_id[case['id']] == case['expectedPi'] else 'failed'}
            for case in cases]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--case-index', type=Path, default=CASE_INDEX)
    args = parser.parse_args()
    case_index = args.case_index.resolve()
    cases = json.loads(case_index.read_text())
    ids = [case['id'] for case in cases]
    if len(ids) != len(set(ids)):
        raise SystemExit('duplicate case IDs')
    frozen = hashlib.sha256(case_index.read_bytes()).hexdigest()
    if frozen != CASE_INDEX_SHA256:
        raise SystemExit(f'case index SHA-256 mismatch: expected {CASE_INDEX_SHA256}, found {frozen}')
    oracle = ROOT / 'tools/parity/resource_glob/oracle.mjs'
    oracle_args = ['node', str(oracle), str(case_index), str(args.pi.resolve())]
    pi_first = json.loads(run(oracle_args, cwd=ROOT))
    pi_second = json.loads(run(oracle_args, cwd=ROOT))
    if pi_first != pi_second:
        raise SystemExit('pinned Pi oracle was not deterministic across two runs')
    for case, observed in zip(cases, pi_first, strict=True):
        if case['id'] != observed['id'] or case['expectedPi'] != observed['expected']:
            raise SystemExit(f"frozen Pi expectation changed for {case['id']}")

    with tempfile.TemporaryDirectory(prefix='pisharp-resource-glob-') as temporary:
        temp = Path(temporary)
        project = temp / 'probe.csproj'
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '</PropertyGroup><ItemGroup><ProjectReference Include="'
            + str(ROOT / 'src/PiSharp.Runtime/PiSharp.Runtime.csproj')
            + '" /></ItemGroup></Project>\n')
        (temp / 'Program.cs').write_text((Path(__file__).with_name('probe.cs')).read_text())
        run(['dotnet', 'build', str(project), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false',
             '-p:NuGetAudit=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'], cwd=ROOT)
        actual = json.loads(run(['dotnet', str(temp / 'bin/Debug/net10.0/probe.dll'),
                                 str(case_index), str(Path.home() / 'resource-glob-contract')], cwd=ROOT))
    report_cases = compare_results(cases, actual)
    report = {'piSha': PIN, 'minimatchVersion': pi_first[0]['minimatchVersion'],
              'piSharpSha': run(['git', 'rev-parse', 'HEAD'], cwd=ROOT).strip(),
              'caseIndexSha256': frozen, 'required': len(cases),
              'passed': sum(item['status'] == 'passed' for item in report_cases),
              'failed': sum(item['status'] == 'failed' for item in report_cases), 'skipped': 0,
              'cases': report_cases}
    output = json.dumps(report, indent=2) + '\n'
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(output)
    print(output, end='')
    if report['failed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
