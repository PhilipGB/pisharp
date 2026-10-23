#!/usr/bin/env python3
"""Run real pinned built-in tools and native AIFunctions in disposable workspaces."""
import argparse
import base64
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import re
import shutil
import stat
import subprocess
import tempfile
import xml.sax.saxutils

from builtin_coding_tool_cases import cases
from builtin_coding_tool_image_comparison import assess_jpeg_codec, only_codec_pixel_differences
from builtin_coding_tool_deviations import assess_read_safety_deviation
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit


def encoded_bytes(spec):
    if isinstance(spec, str):
        return spec.encode('utf-8')
    if 'base64' in spec:
        return base64.b64decode(spec['base64'], validate=True)
    if 'repeat' in spec:
        return spec['repeat'].encode('utf-8') * spec['count']
    if 'base64Repeat' in spec:
        return base64.b64decode(spec['base64Repeat'], validate=True) * spec['count']
    return b''.join(encoded_bytes(part) for part in spec['concat'])


def setup(root, case):
    root.mkdir(parents=True)
    for relative in case.get('directories', {}):
        (root / relative).mkdir(parents=True, exist_ok=True)
    for relative, content in case['files'].items():
        target = root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(encoded_bytes(content))
    if generated := case.get('generatedFiles'):
        directory = root / generated['directory']
        directory.mkdir(parents=True, exist_ok=True)
        for index in range(generated['count']):
            (directory / f'{index:06d}.txt').write_text(generated['text'])
    for relative, target in case.get('symlinks', {}).items():
        (root / relative).symlink_to(target)
    for relative, target in case.get('hardlinks', {}).items():
        (root / relative).hardlink_to(root / target)
    for relative in case.get('fifos', []):
        os.mkfifo(root / relative)
    for relative in case.get('gitDirectories', []):
        subprocess.run(['git', '-C', str(root / relative), 'init', '-q'], check=True,
                       capture_output=True, timeout=10)
    for relative, mode in {**case.get('directories', {}), **case.get('modes', {})}.items():
        (root / relative).chmod(mode)


def filesystem(root, before=None):
    """Capture bytes, modes, ownership, aliases and inode preservation, without following links."""
    entries, identities = {}, {}

    def visit(target):
        relative = target.relative_to(root).as_posix()
        if relative == '.git' or '/.git/' in '/' + relative + '/' or relative.endswith('/.git'):
            return
        info = target.lstat()
        mode = stat.S_IMODE(info.st_mode)
        entry = {'mode': mode, 'uid': info.st_uid, 'gid': info.st_gid}
        identities[relative] = (info.st_dev, info.st_ino)
        if before is not None and relative in before:
            entry['inodePreserved'] = identities[relative] == before[relative]
        if stat.S_ISLNK(info.st_mode):
            entry.update(kind='symlink', target=os.readlink(target))
        elif stat.S_ISDIR(info.st_mode):
            entry['kind'] = 'directory'
            # Snapshot unreadable owned fixtures after the tool has settled; restore their mode.
            needs_access = mode & 0o500 != 0o500
            if needs_access:
                target.chmod(mode | 0o700)
            try:
                for child in sorted(target.iterdir()):
                    visit(child)
            finally:
                if needs_access:
                    target.chmod(mode)
        elif stat.S_ISREG(info.st_mode):
            entry.update(kind='file', size=info.st_size)
            needs_access = not mode & 0o400
            if needs_access:
                target.chmod(mode | 0o400)
            try:
                with target.open('rb') as content:
                    entry['sha256'] = hashlib.file_digest(content, 'sha256').hexdigest()
                if info.st_size <= 4096:
                    entry['base64'] = base64.b64encode(target.read_bytes()).decode('ascii')
            finally:
                if needs_access:
                    target.chmod(mode)
        else:
            entry['kind'] = 'fifo' if stat.S_ISFIFO(info.st_mode) else 'special'
        entries[relative] = entry

    visit(root)
    alias_groups = {}
    for relative, identity in identities.items():
        alias_groups.setdefault(identity, []).append(relative)
    for relative, entry in entries.items():
        if entry['kind'] == 'file':
            entry['hardlinkPeers'] = sorted(name for name in alias_groups[identities[relative]] if name != relative)
    return dict(sorted(entries.items())), identities


def replace_values(value, replacements):
    if isinstance(value, str):
        for original, replacement in replacements.items():
            value = value.replace(original, replacement)
        return value
    if isinstance(value, list):
        return [replace_values(item, replacements) for item in value]
    if isinstance(value, dict):
        return {key: replace_values(item, replacements) for key, item in value.items()}
    return value


def differences(left, right, path=''):
    if type(left) is not type(right) and not (isinstance(left, (int, float)) and isinstance(right, (int, float))):
        return [path or '/']
    if isinstance(left, dict):
        output = []
        for key in sorted(left.keys() | right.keys()):
            key_path = f'{path}/{key}'
            output.extend([key_path] if key not in left or key not in right
                          else differences(left[key], right[key], key_path))
        return output
    if isinstance(left, list):
        output = [f'{path}/length'] if len(left) != len(right) else []
        for index, (a, b) in enumerate(zip(left, right)):
            output.extend(differences(a, b, f'{path}/{index}'))
        return output
    return [] if left == right else [path or '/']


def capture_artifacts(result, allowed_root):
    paths = set()

    def find(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if key in ('fullOutputPath', 'full_output_path') and isinstance(item, str):
                    paths.add(item)
                find(item)
        elif isinstance(value, list):
            for item in value:
                find(item)
        elif isinstance(value, str):
            paths.update(re.findall(r'Full output: ([^\]\n]+)', value))

    find(result)
    artifacts, replacements = [], {}
    for index, name in enumerate(sorted(paths)):
        target = Path(name)
        if not target.resolve().is_relative_to(allowed_root.resolve()):
            raise RuntimeError(f'Tool output path escaped isolated temp root: {target}')
        artifact = {'safeName': bool(re.fullmatch(r'pi-bash-[0-9a-f]+\.log', target.name))}
        try:
            info = target.lstat()
            if not stat.S_ISREG(info.st_mode):
                raise RuntimeError('Tool output artifact is not a regular private file.')
            artifact.update(exists=True, size=info.st_size, mode=stat.S_IMODE(info.st_mode))
            with target.open('rb') as content:
                artifact['sha256'] = hashlib.file_digest(content, 'sha256').hexdigest()
        except OSError as error:
            artifact.update(exists=target.exists(), errorCode=error.errno)
        artifacts.append(artifact)
        replacements[name] = f'<full-output-{index + 1}>'
    return artifacts, replacements


def normalize_result(result, root, artifact_replacements, image_policy):
    result = replace_values(result, {str(root): '<workspace>', **artifact_replacements})
    structured = result.get('structuredContent')
    if isinstance(structured, dict) and 'wall_time_seconds' in structured:
        duration = structured['wall_time_seconds']
        if not isinstance(duration, (int, float)) or duration < 0:
            raise ValueError(f'Invalid recorded Bash duration: {duration!r}')
        structured['wall_time_seconds'] = '<measured duration>'
    # Preserve every raw update in evidence; compare the contract's initial and final snapshots.
    updates = result.pop('updates')
    result['updateContract'] = {'initial': updates[0] if updates else None,
                               'final': updates[-1] if updates else None}
    if image_policy == 'decoded-rgba':
        from PIL import Image

        def decoded(data):
            with Image.open(io.BytesIO(base64.b64decode(data))) as image:
                rgba = image.convert('RGBA')
                return {'width': rgba.width, 'height': rgba.height,
                        'rgbaSha256': hashlib.sha256(rgba.tobytes()).hexdigest()}
        for block in result['content']:
            if block['type'] == 'image':
                block['data'] = decoded(block['data'])
        if isinstance(structured, dict) and structured.get('type') == 'image':
            structured['data'] = decoded(structured['data'])
    return result


def run_probe(command, environment, log_file, timeout=240):
    with log_file.open('w') as log:
        subprocess.run(command, env=environment, stdout=log, stderr=subprocess.STDOUT,
                       check=True, timeout=timeout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True, help='Ignored report directory')
    parser.add_argument('--tools', nargs='+', choices=['read', 'write', 'edit', 'bash', 'grep', 'find', 'ls'])
    parser.add_argument('--cases', nargs='+', help='Exact case IDs; defaults to all selected tools')
    parser.add_argument('--calibrate', action='store_true', help='Run pinned Pi on both sides; PiSharp is unused')
    args = parser.parse_args()
    apply_test_thread_limit()
    pin = verify_pi_checkout(args.pi)
    repo = Path(__file__).resolve().parents[2]
    if Path.cwd().resolve() != repo:
        parser.error('Run from the PiSharp repository root.')
    output = args.output.resolve()
    if not output.is_relative_to(repo / 'artifacts/parity'):
        parser.error('--output must be under ignored artifacts/parity/')
    output.mkdir(parents=True, exist_ok=True)
    selected = [case for case in cases() if (not args.tools or case['tool'] in args.tools)
                and (not args.cases or case['caseId'] in args.cases)]
    if not selected or len({case['caseId'] for case in selected}) != len(selected):
        parser.error('The case selection must be nonempty and have unique IDs.')
    if args.cases and set(args.cases) != {case['caseId'] for case in selected}:
        parser.error('Some requested case IDs were not found.')
    source = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    dirty = subprocess.check_output(['git', 'status', '--porcelain'], text=True).splitlines()
    versions = {}
    for name in ['rg', 'fdfind', 'fd', 'node', 'dotnet']:
        executable = shutil.which(name)
        versions[name] = {'path': executable, 'version': subprocess.check_output(
            [executable, '--version'], text=True).splitlines()[0] if executable else None}
    if not versions['rg']['path'] or not (versions['fd']['path'] or versions['fdfind']['path']):
        parser.error('A real rg and fd/fdfind must be installed; oracle substitution/download is forbidden.')
    start = datetime.now(timezone.utc).isoformat()
    with tempfile.TemporaryDirectory(prefix='pisharp-builtin-parity-', dir='/tmp') as temporary:
        work = Path(temporary)
        try:
            (work / 'Program.cs').write_text(Path(__file__).with_name('builtin-coding-tool-probe.cs').read_text())
            project_path = xml.sax.saxutils.escape(str(repo / 'src/PiSharp.Cli/PiSharp.Cli.csproj'))
            (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType>
<AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{project_path}" /></ItemGroup></Project>''')
            environment = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http', PI_OFFLINE='1',
                               PI_CODING_AGENT_DIR=str(work / 'pi-agent'),
                               PISHARP_AGENT_DIR=str(work / 'native-agent'),
                               PISHARP_SESSION_DIR=str(work / 'native-sessions'),
                               PI_SESSION_ID='stale-session', PI_SESSION_FILE='stale-file',
                               PI_PROVIDER='stale-provider', PI_MODEL='stale-model', PI_REASONING_LEVEL='stale-level')
            if not args.calibrate:
                run_probe(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                          environment, output / 'build.log', timeout=180)
            roots, snapshots, inputs, temps = {}, {}, {}, {}
            for implementation in ['pi', 'pisharp']:
                inputs[implementation] = []
                temps[implementation] = work / implementation / 'temps'
                temps[implementation].mkdir(parents=True)
                for case in selected:
                    root = work / implementation / case['caseId']
                    setup(root, case)
                    _, identities = filesystem(root)
                    roots[implementation, case['caseId']] = root
                    snapshots[implementation, case['caseId']] = identities
                    request = {key: case[key] for key in ['caseId', 'tool', 'arguments', 'options',
                               'context', 'preCancelled', 'cancelOnOutput'] if key in case}
                    request['workspace'] = str(root)
                    request = replace_values(request, {'{WORKSPACE}': str(root)})
                    inputs[implementation].append(request)
                (output / f'{implementation}-input.json').write_text(json.dumps(inputs[implementation], indent=2) + '\n')
            commands = {
                'pi': ['node', str(Path(__file__).with_name('builtin-coding-tool-probe.mjs')), str(args.pi.resolve()),
                       str(output / 'pi-input.json'), str(output / 'pi-results.json')],
                'pisharp': ['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'),
                            str(output / 'pisharp-input.json'), str(output / 'pisharp-results.json')]}
            if args.calibrate:
                commands['pisharp'] = [*commands['pi'][:-2], str(output / 'pisharp-input.json'),
                                      str(output / 'pisharp-results.json')]
            with ThreadPoolExecutor(max_workers=2) as pool:
                futures = []
                for implementation, command in commands.items():
                    child_environment = dict(environment, TMPDIR=str(temps[implementation]))
                    futures.append(pool.submit(run_probe, command, child_environment,
                                               output / f'{implementation}.log'))
                for future in futures:
                    future.result()
            raw = {name: json.loads((output / f'{name}-results.json').read_text()) for name in commands}
            report_cases = []
            for case in selected:
                case_id = case['caseId']
                observations = {}
                raw_results = {}
                for implementation in commands:
                    result = next(value for value in raw[implementation]['results'] if value['caseId'] == case_id)
                    raw_results[implementation] = result
                    root = roots[implementation, case_id]
                    fs, _ = filesystem(root, snapshots[implementation, case_id])
                    artifacts, replacements = capture_artifacts(result, temps[implementation])
                    policy = case.get('imagePolicy', 'decoded-rgba' if case_id == 'read-png-resize' else 'exact-bytes')
                    observations[implementation] = {'result': normalize_result(result, root, replacements, policy),
                                                    'filesystem': fs, 'fullOutputArtifacts': artifacts}
                paths = differences(observations['pi'], observations['pisharp'])
                strict_paths = paths.copy()
                codec_comparison = None
                if case.get('jpegComparison') and only_codec_pixel_differences(paths):
                    codec_comparison = assess_jpeg_codec(raw_results['pi'], raw_results['pisharp'], case['jpegComparison'])
                    if codec_comparison and codec_comparison['accepted']:
                        paths = []
                safety_excluded = bool(paths) and not args.calibrate and assess_read_safety_deviation(case, observations)
                if args.calibrate:
                    observations['controlPi'] = observations.pop('pisharp')
                report_cases.append({'caseId': case_id, 'tool': case['tool'], 'pinnedPiSha': pin,
                    'piSharpSha': None if args.calibrate else source,
                    'piSharpUsed': not args.calibrate, 'sourceWorktreeDirty': bool(dirty), 'platform': platform.platform(),
                    'setup': case, 'observations': observations, 'differences': paths,
                    'strictDifferences': strict_paths, 'strictPass': not strict_paths,
                    'codecComparison': codec_comparison,
                    'strictParityExcluded': safety_excluded,
                    'pass': not paths, 'classification': 'intentional_safety_deviation' if safety_excluded else
                    'native_jpeg_codec_equivalent' if codec_comparison and not paths else
                    'exact_with_documented_normalization' if not paths else 'unresolved'})
            declaration_differences = {}
            for name in raw['pi']['declarations']:
                expected, actual = raw['pi']['declarations'][name], raw['pisharp']['declarations'][name]
                declaration_differences[name] = differences(
                    {key: expected[key] for key in ['name', 'description', 'parameters', 'outputSchema', 'constrainedSampling']},
                    {key: actual[key] for key in ['name', 'description', 'parameters', 'outputSchema', 'constrainedSampling']})
            report = {'schema': 1, 'mode': 'same-pi-control' if args.calibrate else 'pi-vs-pisharp',
                'piSha': pin, 'piSharpSha': None if args.calibrate else source,
                'piSharpUsed': not args.calibrate, 'harnessRepositorySha': source, 'dirtyPaths': dirty,
                'startedAt': start, 'finishedAt': datetime.now(timezone.utc).isoformat(),
                'platform': platform.platform(), 'executables': versions,
                'comparisonPolicy': {'temporaryRoots': 'Each root becomes <workspace> in result strings.',
                    'fullOutputNames': 'Random names mapped by references; exact file hashes/sizes/modes/safe-name checks compared.',
                    'duration': 'Nonnegative numeric wall_time_seconds retained raw; excluded from value equality.',
                    'images': 'Original payloads exact bytes; selected reencoded images compare decoded RGBA dimensions/pixels; raw payloads retained. MIME, notes, structured results and failures remain exact.',
                    'jpegCodecs': 'Only named calibrated cases may use the explicit JPEG envelope. Strict hashes/differences remain recorded. Quantization, sampling, fidelity and every non-pixel contract are checked; see setup.jpegComparison.',
                    'safetyExclusions': 'Two named image allocation limits remain failed strict comparisons, classified separately only when their complete expected results and unchanged filesystem match. They never increase passed/strictPassed.',
                    'updates': 'Raw complete updates retained; initial/final snapshots compared. Timing/chunk count not asserted identical.',
                    'filesystem': 'Exact content hashes, short-file bytes, kinds, modes, uid/gid, link targets/peers and inode-preservation flags. No mtime/ctime/absolute inode equality.'},
                'declarationDifferences': declaration_differences, 'cases': report_cases,
                'summary': {'passed': sum(case['pass'] for case in report_cases), 'total': len(report_cases),
                            'strictPassed': sum(case['strictPass'] for case in report_cases),
                            'jpegCodecEquivalent': sum(case['classification'] == 'native_jpeg_codec_equivalent' for case in report_cases),
                            'intentionalSafetyDeviations': sum(case['strictParityExcluded'] for case in report_cases),
                            'unresolved': sum(not case['pass'] and not case['strictParityExcluded'] for case in report_cases),
                            'perTool': dict(Counter(case['tool'] for case in report_cases))}}
            (output / 'report.json').write_text(json.dumps(report, indent=2, ensure_ascii=True) + '\n')
            print(json.dumps(report['summary']), flush=True)
            for case in report_cases:
                if not case['pass']:
                    print(case['caseId'], ', '.join(case['differences'][:8]), flush=True)
            return 0 if report['summary']['unresolved'] == 0 and not any(declaration_differences.values()) else 1
        finally:
            # Permission fixtures must not prevent disposable-workspace cleanup.
            for directory, children, files in os.walk(work, topdown=True, followlinks=False):
                os.chmod(directory, 0o700)
                for name in children:
                    target = Path(directory) / name
                    if not target.is_symlink():
                        target.chmod(0o700)


if __name__ == '__main__':
    raise SystemExit(main())
