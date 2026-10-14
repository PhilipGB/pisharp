#!/usr/bin/env python3
"""Compare actual instruction file loading, ordering and contents against pinned Pi."""
import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import tempfile

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
if not hasattr(os, 'geteuid') or os.geteuid() == 0:
    raise RuntimeError('Real unreadable-file evidence requires a non-root Unix runner.')
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]


def file(value, denied=False):
    return dict(bytes=base64.b64encode(value.encode('utf8') if isinstance(value, str) else value).decode(), denied=denied)


cases = [
    dict(id='global-ancestor-override-order', cwd='project/child', agent='agent', files={
        'agent/CLAUDE.md': file('GLOBAL'), 'AGENTS.md': file('ANCESTOR'),
        'project/AGENTS.md': file('SHADOWED'), 'project/AGENTS.override.md': file('OVERRIDE'),
        'project/child/AGENTS.MD': file('CHILD')}),
    dict(id='agent-also-ancestor', cwd='project/child', agent='project', files={'project/AGENTS.md': file('ONCE')}),
    dict(id='uppercase-claude', cwd='project', agent='agent', files={'project/CLAUDE.MD': file('UPPERCASE')}),
    dict(id='one-bom', cwd='project', agent='agent', files={'project/AGENTS.md': file('\ufeffONE')}),
    dict(id='two-boms', cwd='project', agent='agent', files={'project/AGENTS.md': file('\ufeff\ufeffTWO')}),
    dict(id='file-over-64k', cwd='project', agent='agent', files={'project/AGENTS.md': file('x' * (65 * 1024))}),
    dict(id='combined-over-256k', cwd='project/child', agent='agent', files={
        'agent/AGENTS.md': file('a' * (140 * 1024)), 'project/AGENTS.md': file('b' * (140 * 1024)),
        'project/child/AGENTS.md': file('c' * (140 * 1024))}),
    dict(id='utf8-replacement', cwd='project', agent='agent', files={'project/AGENTS.md': file(b'bad \xff \xe2\x82 tail')}),
    dict(id='unreadable-priority-falls-back', cwd='project', agent='agent', files={
        'project/AGENTS.override.md': file('DENIED', True), 'project/AGENTS.md': file('FALLBACK')}),
    dict(id='unreadable-ancestor-does-not-stop-child', cwd='project/child', agent='agent', files={
        'project/AGENTS.md': file('DENIED', True), 'project/child/AGENTS.md': file('CHILD')}),
    dict(id='warning-order-is-global-then-child-to-ancestor', cwd='project/child', agent='agent', files={
        'agent/AGENTS.override.md': file('DENIED GLOBAL', True), 'agent/AGENTS.md': file('GLOBAL'),
        'project/AGENTS.override.md': file('DENIED ANCESTOR', True), 'project/AGENTS.md': file('ANCESTOR'),
        'project/child/AGENTS.override.md': file('DENIED CHILD', True), 'project/child/AGENTS.md': file('CHILD')}),
    dict(id='named-pipe-is-skipped-before-reading', cwd='project', agent='agent', files={
        'project/AGENTS.override.md': dict(**file(''), fifo=True), 'project/AGENTS.md': file('FALLBACK')}),
]
for identifier, own_files in [
    ('unreadable-worktree-context-inherits-main', {'main/worktrees/feature/AGENTS.md': file('DENIED', True)}),
    ('unreadable-worktree-override-shadows-by-fallback-name', {'main/worktrees/feature/AGENTS.override.md': file('DENIED', True),
        'main/worktrees/feature/AGENTS.md': file('WORKTREE')}),
    ('readable-worktree-context-shadows-main', {'main/worktrees/feature/AGENTS.md': file('WORKTREE')}),
]:
    metadata = {'main/.git/HEAD': file('ref: refs/heads/main\n'),
                'main/.git/worktrees/feature/HEAD': file('ref: refs/heads/feature\n'),
                'main/.git/worktrees/feature/commondir': file('../..\n'),
                'main/worktrees/feature/.git': dict(**file(''), gitDirectory='main/.git/worktrees/feature'),
                'main/AGENTS.md': file('MAIN')}
    cases.append(dict(id=identifier, cwd='main/worktrees/feature/child', agent='agent', files={**metadata, **own_files}))
with tempfile.TemporaryDirectory(prefix='pisharp-context-differential-') as directory:
    work = Path(directory)
    (work / 'inputs.json').write_text(json.dumps(cases))
    (work / 'Program.cs').write_text((repo / 'tools/parity/context-instructions-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    expected = json.loads(subprocess.check_output(['node', '--experimental-transform-types',
        str(repo / 'tools/parity/context-instructions-probe.mjs'), str(args.pi.resolve()), str(work / 'inputs.json'), str(work)], env=env, timeout=30))
    try:
        actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/probe.dll'),
            str(work / 'inputs.json'), str(work)], env=env, timeout=10))
        differences = [dict(id=pi['id'], pi=pi, pisharp=native) for pi, native in zip(expected, actual, strict=True) if pi != native]
    except subprocess.TimeoutExpired:
        actual = None
        differences = [dict(id='named-pipe-is-skipped-before-reading', nativeTimedOut=True, deadlineSeconds=10,
            finding='The real native loader did not settle; Pi skips the pipe and loads the fallback immediately.')]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, scenarios=[dict(id=case['id'], cwd=case['cwd'], agent=case['agent'],
        files={name: dict(bytes=len(base64.b64decode(value['bytes'])), denied=value['denied'], fifo=value.get('fifo', False)) for name, value in case['files'].items()}) for case in cases],
        pi=expected, pisharp=actual, differences=differences, match=not differences,
        comparison='Exact loaded path/warning order and SHA256 of full file contents in the native project-instructions envelope. Only isolated workspace prefixes are normalized; large text, BOMs, invalid UTF8 replacement, linked-worktree shadowing and read-failure continuation are retained.',
        runnerUid=os.geteuid(),
        limitations='Real file reads on Linux; unreadable fixtures use mode 000 under the actual non-root runner. Linked-worktree metadata is deterministic; broader real git/symlink/platform topology, exact warning color/native error wording and complete startup/reload/trust UI remain wider work.'), indent=2)+'\n')
    assert not differences, json.dumps(differences, indent=2)
    print(f'Matched {len(cases)} instruction file loading/order/content scenarios.')
