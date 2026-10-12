#!/usr/bin/env python3
"""Capture untouched baseline twice; invocation metadata and outputs remain separate.

Run sequentially to avoid making process/terminal timing tests compete for resources.
No tracked files are edited. Terminal reports and assertion captures are retained;
bulky raw viewport streams are measured and omitted from the durable baseline.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
from terminal_assertions import assertions

parser = argparse.ArgumentParser()
parser.add_argument('--repo', type=Path, required=True)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--runs', type=int, default=2)
parser.add_argument('--candidate', action='store_true', help='Capture a later branch HEAD against the frozen Pi SHA')
args = parser.parse_args()
repo, pi, output = args.repo.resolve(), args.pi.resolve(), args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
selection = json.loads((output / 'source-selection.json').read_text())
actual_head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
assert args.candidate or actual_head == selection['pisharp']
assert subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip() == selection['pi']
assert args.candidate or subprocess.run(['git', 'diff', '--quiet'], cwd=repo).returncode == 0

def working_source():
    changed = subprocess.check_output(['git', 'diff', '--name-only', 'HEAD'], cwd=repo, text=True).splitlines()
    untracked = subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard'], cwd=repo, text=True).splitlines()
    user_files = set(json.loads((Path(__file__).resolve().parent.parent / 'baseline/starting-user-files.json').read_text()))
    return {path: hashlib.sha256((repo/path).read_bytes()).hexdigest() if (repo/path).is_file() else None
            for path in sorted(set(changed) | (set(untracked)-user_files))}

source_before = working_source()
if args.candidate:
    (output / 'candidate-working-tree.patch').write_bytes(subprocess.check_output(['git', 'diff', '--binary', 'HEAD'], cwd=repo))
env = dict(os.environ)
env.update(TERM='xterm-256color', DOTNET_PROCESSOR_COUNT='2', UV_THREADPOOL_SIZE='2',
           OPENBLAS_NUM_THREADS='2', OMP_NUM_THREADS='2', MKL_NUM_THREADS='2',
           NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
metadata = dict(versions={name: subprocess.check_output(command, text=True).strip() for name, command in
                        [('dotnet', ['dotnet', '--version']), ('node', ['node', '--version']),
                         ('python', ['python3', '--version'])]},
                environment={key: env[key] for key in ['TERM', 'DOTNET_PROCESSOR_COUNT',
                             'UV_THREADPOOL_SIZE', 'OPENBLAS_NUM_THREADS', 'OMP_NUM_THREADS',
                             'MKL_NUM_THREADS', 'NUGET_HTTP_CACHE_PATH']},
                cwd=str(repo), pi=str(pi), pisharpHead=actual_head, workingSource=source_before, reference=selection)
(output / 'capture-metadata.json').write_text(json.dumps(metadata, indent=2) + '\n')

def run(key, command, folder, cwd=repo, timeout=1800):
    folder.mkdir(parents=True, exist_ok=True)
    print(f'START {folder.name}/{key}', flush=True)
    start = time.time()
    with (folder / f'{key}.log').open('w') as stream:
        try:
            result = subprocess.run(command, cwd=cwd, env=env, stdout=stream,
                                    stderr=subprocess.STDOUT, timeout=timeout)
            code = result.returncode
        except subprocess.TimeoutExpired:
            code = 124
    record = dict(id=key, command=command, cwd=str(cwd), exitCode=code,
                  seconds=round(time.time()-start, 3), logger='stdout+stderr text')
    (folder / f'{key}.invocation.json').write_text(json.dumps(record, indent=2)+'\n')
    print(f'DONE {folder.name}/{key} exit={code}', flush=True)
    return code

setup = output / 'setup'
assert run('restore', ['dotnet', 'restore', 'PiSharp.slnx'], setup) == 0, 'Dependency setup failed'
assert run('terminal-install', ['npm', 'ci', '--prefix', 'tools/parity/terminal', '--ignore-scripts', '--no-audit', '--no-fund'], setup) == 0, 'Dependency setup failed'
assert run('pi-install', ['npm', 'ci', '--ignore-scripts', '--no-audit', '--no-fund'], setup, cwd=pi) == 0, 'Pi dependency setup failed'
if not (pi / 'packages/ai/src/providers/data/.manifest.json').exists():
    assert run('pi-hydrate', ['npm', 'run', 'hydrate:model-data'], setup, cwd=pi) == 0, 'Pi model-data hydration failed'
assert run('pi-build', ['npm', 'run', 'build:offline'], setup, cwd=pi) == 0, 'Pi build failed'
assert subprocess.run(['git', 'diff', '--quiet'], cwd=pi).returncode == 0, 'Pi build changed tracked source'

for number in range(1, args.runs+1):
    folder = output / f'run-{number}'
    run('format', ['dotnet', 'format', 'PiSharp.slnx', '--verify-no-changes', '--no-restore'], folder)
    run('build', ['dotnet', 'build', 'PiSharp.slnx', '--no-restore', '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'], folder)
    run('dotnet', ['dotnet', 'test', 'PiSharp.slnx', '--no-build', '--no-restore',
        '--logger', 'trx;LogFileName=tests.trx', '--results-directory', str(folder / 'dotnet')], folder)
    run('node', ['node', '--test', '--test-reporter=tap', 'terminal-state.test.mjs'], folder,
        cwd=repo / 'tools/parity/terminal')
    run('python', ['python3', '-m', 'unittest', 'discover', '-s', 'tools/parity/terminal', '-p', 'test_*.py', '-v'], folder)
    if args.candidate:
        run('docs-paths', ['python3', 'tools/validate_doc_paths.py'], folder)
    run('mcp', ['node', 'tools/parity/mcp-conformance/run.mjs', '--keep-results'], folder)
    # The conformance launcher preserves its raw per-scenario JSON under /tmp.
    # Copy the result tree into the capture directory without mutating its baseline.
    import re
    log = (folder / 'mcp.log').read_text()
    match = re.search(r'Results kept in (.+)', log)
    if match:
        shutil.copytree(match[1].strip(), folder / 'mcp-results', dirs_exist_ok=True)
    for script in sorted((repo / 'tools/parity').glob('*differential.py')):
        key = script.stem
        run(key, ['python3', str(script), '--pi', str(pi), '--output', str(folder / f'{key}.json')], folder)
        if key == 'virtual-model-routing-differential':
            for option in ['--context-compaction', '--session-transitions', '--state-identity', '--thinking=minimal', '--thinking=max']:
                suffix = option.removeprefix('--').replace('=', '-')
                run(f'{key}-{suffix}', ['python3', str(script), '--pi', str(pi), option,
                    '--output', str(folder / f'{key}-{suffix}.json')], folder)
    for fixture in sorted((repo / 'tools/parity/terminal/fixtures').glob('*.json')):
        for calibrate in [True, False]:
            key = f'terminal-{fixture.stem}-' + ('calibration' if calibrate else 'paired')
            target = folder / key
            command = ['python3', 'tools/parity/terminal/run.py', '--pi', str(pi),
                '--fixture', str(fixture), '--output', str(target), '--pisharp-extension',
                str(repo / 'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll')]
            if calibrate:
                command.append('--calibrate')
            run(key, command, folder, timeout=3600)
            # Keep exact comparison reports and semantic assertion evidence.
            omitted = []
            if target.exists():
                for artifact in target.glob('*.gz'):
                    if '.assertions.' not in artifact.name:
                        omitted.append(dict(name=artifact.name, bytes=artifact.stat().st_size,
                                            sha256=hashlib.sha256(artifact.read_bytes()).hexdigest()))
                        artifact.unlink()
                (target / 'omitted-raw-streams.json').write_text(json.dumps(omitted, indent=2)+'\n')
        assertions(repo, folder, fixture, run)
    assert working_source() == source_before, 'Candidate source changed while verification was running'
    print(f'COMPLETE run-{number}', flush=True)
