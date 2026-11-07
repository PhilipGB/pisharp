#!/usr/bin/env python3
"""Publish a capture-owned PiSharp artifact and run all evaluation captures.

The source tree must be clean before the publish and must remain at the same
commit and clean afterwards. The published DLL, deps manifest and runtime config
are hashed into ``pisharp-artifact.json``; ``run.py`` copies those hashes into
every capture's provenance.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[3]
FIXTURE_ROOT = ROOT / 'tools/parity/terminal/fixtures/interactive-streaming-cancellation-001'
SCENARIOS = [
    's01-text-streaming',
    's02-reasoning-streaming',
    's03-tool-progress',
    's04-text-cancellation',
    's05-tool-cancellation',
]


def git(*arguments, cwd=ROOT):
    return subprocess.check_output(['git', *arguments], cwd=cwd, text=True).strip()


def status(path):
    return git('status', '--porcelain', cwd=path)


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(command, cwd=ROOT):
    print('+', ' '.join(str(part) for part in command), flush=True)
    return subprocess.run([str(part) for part in command], cwd=cwd).returncode


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path,
                        default=ROOT / 'artifacts/parity/interactive-streaming-cancellation-001')
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()

    source_status = status(ROOT)
    if source_status:
        raise SystemExit(f'Refusing to publish from a dirty source tree:\n{source_status}')
    pi_status = status(args.pi)
    if pi_status:
        raise SystemExit(f'Refusing to capture from a dirty Pi checkout:\n{pi_status}')

    source_sha = git('rev-parse', 'HEAD')
    source_branch = git('rev-parse', '--abbrev-ref', 'HEAD')
    output = args.output.resolve()
    if output.exists():
        shutil.rmtree(output)
    publish_dir = output / 'pisharp-artifact'
    publish_dir.mkdir(parents=True)

    command = [args.dotnet, 'publish', ROOT / 'src/PiSharp.Cli/PiSharp.Cli.csproj',
               '-c', 'Debug', '--no-restore', '-o', publish_dir]
    if run(command) != 0:
        raise SystemExit('PiSharp publish failed')
    if git('rev-parse', 'HEAD') != source_sha or status(ROOT):
        raise SystemExit('Source changed during the PiSharp publish; capture is not reproducible')

    dll = publish_dir / 'PiSharp.Cli.dll'
    deps = publish_dir / 'PiSharp.Cli.deps.json'
    runtime = publish_dir / 'PiSharp.Cli.runtimeconfig.json'
    for path in (dll, deps, runtime):
        if not path.is_file():
            raise SystemExit(f'PiSharp publish did not produce {path}')

    artifact = dict(
        sourceSha=source_sha,
        sourceBranch=source_branch,
        sourceDirty=False,
        buildCommand=' '.join(str(part) for part in command),
        file=dll.name,
        sha256=sha256(dll),
        depsFile=deps.name,
        depsSha256=sha256(deps),
        runtimeConfigFile=runtime.name,
        runtimeConfigSha256=sha256(runtime),
    )
    (publish_dir / 'pisharp-artifact.json').write_text(json.dumps(artifact, indent=2) + '\n')
    print(json.dumps(artifact, indent=2), flush=True)

    for scenario in SCENARIOS:
        fixture = FIXTURE_ROOT / f'{scenario}.json'
        for mode in ('calibration', 'comparison'):
            destination = output / mode / scenario
            capture_command = [
                sys.executable, ROOT / 'tools/parity/terminal/run.py',
                '--pi', args.pi.resolve(),
                '--pisharp', dll,
                '--fixture', fixture,
                '--output', destination,
            ]
            if mode == 'calibration':
                capture_command.append('--calibrate')
            code = run(capture_command)
            if code not in (0, 1):
                raise SystemExit(f'Capture failed for {scenario} {mode} with exit {code}')
    print('CAPTURE_COMPLETE', flush=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
