#!/usr/bin/env python3
"""Physical pinned/source CLI shutdown observations with disposable process trees."""
import argparse
import json
import os
from pathlib import Path
import select
import shlex
import signal
import subprocess
import tempfile
from datetime import datetime, timezone

PIN = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
ROOT = Path(__file__).resolve().parents[2]


def state(pid):
    try:
        stat = Path(f'/proc/{pid}/stat').read_text()
        return stat[stat.rindex(')') + 2:].split(' ')[0]
    except (FileNotFoundError, ProcessLookupError):
        return 'gone'


def capture(command, shutdown):
    with tempfile.TemporaryDirectory(prefix='pisharp-bash-shutdown-') as temporary:
        root = Path(temporary)
        agent = root / 'agent'
        agent.mkdir()
        ready = root / 'ready'
        os.mkfifo(ready)
        descriptor = os.open(ready, os.O_RDWR | os.O_NONBLOCK)
        child_path, grandchild_path = root / 'child.pid', root / 'grandchild.pid'
        quote = shlex.quote
        grandchild = f"printf '%s\\n' \"$$\" > {quote(str(grandchild_path))}; printf ready > {quote(str(ready))}; exec sleep 60"
        child = f"printf '%s\\n' \"$$\" > {quote(str(child_path))}; setsid /bin/sh -c {quote(grandchild)} & wait"
        bash = f'/bin/sh -c {quote(child)} & wait'
        environment = {key: os.environ[key] for key in ['PATH', 'HOME', 'DOTNET_ROOT', 'LD_LIBRARY_PATH'] if key in os.environ}
        environment.update(PI_OFFLINE='1', PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
                           PISHARP_SESSION_DIR=str(agent / 'sessions'), DOTNET_PROCESSOR_COUNT='2',
                           DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
        process = None
        try:
            process = subprocess.Popen(command, cwd=root, env=environment, stdin=subprocess.PIPE,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            process.stdin.write((json.dumps({'id': 'shutdown-bash', 'type': 'bash', 'command': bash}) + '\n').encode())
            process.stdin.flush()
            if not select.select([descriptor], [], [], 10)[0]:
                raise RuntimeError(f'CLI did not reach process barrier; exit={process.poll()}')
            if os.read(descriptor, 16) != b'ready':
                raise RuntimeError('Incorrect process barrier payload')
            child_id, grandchild_id = int(child_path.read_text()), int(grandchild_path.read_text())
            if shutdown == 'eof':
                process.stdin.close()
                process.stdin = None
            else:
                process.send_signal(getattr(signal, shutdown))
            timed_out = False
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                timed_out = True
            observed = {'child': state(child_id), 'detachedGrandchild': state(grandchild_id)}
            if timed_out:
                process.kill()
            stdout, stderr = process.communicate(timeout=5)
            return {'shutdown': shutdown, 'timedOut': timed_out, 'exitCode': process.returncode,
                    'processesAtToolSettlement': observed,
                    'stdout': stdout.decode('utf8', 'replace').replace(str(root), '<workspace>'),
                    'stderr': stderr.decode('utf8', 'replace').replace(str(root), '<workspace>'),
                    'cleanup': 'Recorded fixture descendants killed in finally after observation'}
        finally:
            if process is not None:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=5)
            for file in [grandchild_path, child_path]:
                if not file.exists():
                    continue
                pid = int(file.read_text())
                try:
                    os.kill(pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            os.close(descriptor)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    pi = args.pi.resolve()
    if subprocess.check_output(['git', '-C', str(pi), 'rev-parse', 'HEAD'], text=True).strip() != PIN:
        raise ValueError('Upstream revision does not match the required pin')
    if subprocess.check_output(['git', '-C', str(pi), 'status', '--porcelain'], text=True).strip():
        raise ValueError('Pinned source worktree must be clean')
    common = ['--mode', 'rpc', '--provider', 'openai', '--model', 'gpt-4o-mini', '--offline', '--no-session',
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-context-files']
    commands = {
        'pi': ['node', '--import', (pi / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri(),
               str(pi / 'packages/coding-agent/src/cli.ts'), *common],
        'pisharp': ['dotnet', str(args.pisharp.resolve()), *common],
    }
    report = {'piSha': PIN, 'piSharpSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'dirtyPaths': subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).splitlines(),
              'startedAt': datetime.now(timezone.utc).isoformat(), 'cases': []}
    for shutdown in ['eof', 'SIGTERM', 'SIGHUP']:
        row = {'caseId': 'bash-rpc-shutdown-' + shutdown.lower(), 'observations': {}}
        for product, command in commands.items():
            row['observations'][product] = capture(command, shutdown)
        report['cases'].append(row)
        print(row['caseId'], {key: {field: value[field] for field in ['timedOut', 'exitCode', 'processesAtToolSettlement']}
                             for key, value in row['observations'].items()}, flush=True)
    report['finishedAt'] = datetime.now(timezone.utc).isoformat()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')


if __name__ == '__main__':
    main()
