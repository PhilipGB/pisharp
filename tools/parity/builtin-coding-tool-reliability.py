#!/usr/bin/env python3
"""Bounded repeats of the existing tool safety, provider and persistence tests."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET

from terminal.thread_limits import apply_test_thread_limit, thread_limit_environment

ROOT = Path(__file__).resolve().parents[2]
CLASSES = [
    'FileMutationCoordinationTests', 'FileMutationAliasTests', 'FileMutationOwnershipTests', 'BashProcessLifetimeTests',
    'BuiltinBashResultParityTests', 'BashToolUpdateParityTests', 'ReadStreamingSafetyTests',
    'ReadImageCancellationTests', 'ReadImageProcessorTests', 'BuiltinSearchParityTests',
    'GrepCancellationTests', 'FindCancellationTests', 'LsCancellationTests',
    'SearchPreparationCancellationTests', 'BuiltinToolWorkflowTests',
    'BuiltinConcurrencyReliabilityTests', 'ReadImageToolTests',
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--iterations', type=int, default=5)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    apply_test_thread_limit()
    if not 3 <= args.iterations <= 10:
        parser.error('Use three to ten bounded repeats.')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    report = dict(schema=1, piSharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        dirtyPaths=subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).splitlines(),
        startedAt=datetime.now(timezone.utc).isoformat(), classes=CLASSES, iterations=[])
    expression = '|'.join('FullyQualifiedName~' + name for name in CLASSES)
    for iteration in range(args.iterations):
        with tempfile.TemporaryDirectory(prefix='pisharp-tool-reliability-') as temporary:
            environment = dict(os.environ, TMPDIR=temporary, **thread_limit_environment())
            environment.pop('NO_COLOR', None)
            trx = f'reliability-{iteration + 1}.trx'
            command = ['dotnet', 'test', str(ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'),
                       '--no-build', '--no-restore', '--filter', expression,
                       '--logger', 'trx;LogFileName=' + trx, '--results-directory', str(output)]
            started = time.monotonic()
            timed_out = False
            with (output / f'reliability-{iteration + 1}.log').open('w') as log:
                with subprocess.Popen(command, cwd=ROOT, env=environment, stdout=log,
                                      stderr=subprocess.STDOUT, start_new_session=True) as process:
                    try:
                        exit_code = process.wait(timeout=180)
                    except subprocess.TimeoutExpired:
                        timed_out = True
                        os.killpg(process.pid, signal.SIGKILL)
                        exit_code = process.wait(timeout=5)
            counters = {}
            if (output / trx).is_file():
                root = ET.parse(output / trx).getroot()
                counters = root.find('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters').attrib
            spills = [str(path.relative_to(temporary)) for path in Path(temporary).rglob('pi-bash-*.log')]
            passed = (exit_code == 0 and not timed_out and counters.get('failed') == '0' and
                      counters.get('notExecuted') == '0' and not spills)
            row = dict(iteration=iteration + 1, elapsedSeconds=round(time.monotonic() - started, 3),
                       exitCode=exit_code, timedOut=timed_out, counters=counters,
                       abandonedFullOutputFiles=spills, passed=passed)
            report['iterations'].append(row)
            report['finishedAt'] = datetime.now(timezone.utc).isoformat()
            report['passed'] = all(row['passed'] for row in report['iterations'])
            (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
            print(json.dumps(row), flush=True)
            if not passed:
                raise SystemExit(1)


if __name__ == '__main__':
    main()
