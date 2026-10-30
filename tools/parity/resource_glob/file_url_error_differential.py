#!/usr/bin/env python3
"""Compare malformed file URL pattern errors from pinned Pi and PiSharp."""
import argparse
import hashlib
import json
import sys
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout

INDEX = HERE / 'file_url_error_cases.json'
INDEX_SHA256 = 'e886f081820a24fa25f4f4493010bc8ab5b06e9d0f3948e93c4b8d75f0992cd5'
PIN = (ROOT / 'tools/parity/pi-reference.txt').read_text().strip()


def run(command, **kwargs):
    result = subprocess.run(command, text=True, capture_output=True, **kwargs)
    if result.returncode:
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(command)}\n{result.stdout}{result.stderr}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != 'linux':
        raise RuntimeError('Frozen file URL error cases target Linux Pi behavior.')
    pi = args.pi.resolve()
    pi_sha = verify_pi_checkout(pi)
    cases = json.loads(INDEX.read_text())
    ids = [case['id'] for case in cases]
    if len(ids) != len(set(ids)):
        raise SystemExit('duplicate file URL error case IDs')
    digest = hashlib.sha256(INDEX.read_bytes()).hexdigest()
    if digest != INDEX_SHA256:
        raise SystemExit(f'file URL error index SHA-256 mismatch: expected {INDEX_SHA256}, found {digest}')

    oracle = HERE / 'file_url_error_oracle.mjs'
    oracle_command = ['node', '--experimental-transform-types', str(oracle), str(pi), str(INDEX)]
    pi_first = json.loads(run(oracle_command, cwd=ROOT))
    pi_second = json.loads(run(oracle_command, cwd=ROOT))
    if pi_first != pi_second:
        raise SystemExit('pinned Pi file URL errors were nondeterministic')
    if [item['id'] for item in pi_first] != ids:
        raise SystemExit('Pi file URL error output IDs differ from frozen cases')
    for case, observed in zip(cases, pi_first, strict=True):
        if observed.get('actual') != {'error': case['expectedPiError']}:
            raise SystemExit(f"pinned Pi error expectation changed for {case['id']}: {observed}")

    with tempfile.TemporaryDirectory(prefix='pisharp-file-url-errors-') as temporary:
        temp = Path(temporary)
        project = temp / 'probe.csproj'
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '</PropertyGroup><ItemGroup><ProjectReference Include="'
            + str(ROOT / 'src/PiSharp.Runtime/PiSharp.Runtime.csproj')
            + '" /></ItemGroup></Project>\n')
        (temp / 'Program.cs').write_text((HERE / 'probe.cs').read_text())
        run(['dotnet', 'build', str(project), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false',
             '-p:NuGetAudit=false', f'-p:RestoreFallbackFolders={Path.home() / ".nuget/packages"}'], cwd=ROOT)
        actual = json.loads(run(['dotnet', str(temp / 'bin/Debug/net10.0/probe.dll',),
                                 str(INDEX), str(Path.home() / 'resource-file-url-errors'), 'file-url-errors'], cwd=ROOT))

    reports = []
    for case, pi_result, sharp in zip(cases, pi_first, actual, strict=True):
        expected_error = case['expectedPiError']
        operation_results = {}
        for operation in ('glob', 'exact'):
            result = sharp.get(operation, {})
            passed = result.get('threw') is True and result.get('message') == expected_error['message']
            operation_results[operation] = result
            if not passed:
                raise SystemExit(f"PiSharp did not match Pi {operation} error for {case['id']}: {result}")
        reports.append({
            'id': case['id'],
            'pattern': case['pattern'],
            'expectedPiError': expected_error,
            'piActual': pi_result['actual'],
            'piSharpActual': operation_results,
            'status': 'passed'
        })

    output = dict(piSha=pi_sha,
                  piSharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  caseIndexSha256=digest, required=len(cases), passed=len(reports), failed=0, skipped=0,
                  cases=reports)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2) + '\n')
    print(f'Matched {len(reports)} malformed file URL pattern errors for glob and exact overrides.')


if __name__ == '__main__':
    main()
