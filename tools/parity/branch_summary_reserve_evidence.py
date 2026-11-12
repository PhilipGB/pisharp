#!/usr/bin/env python3
"""Freeze reviewed compact evidence after executing local quality and test gates."""
import argparse
import json
import subprocess
from verify_settings_branch_summary_reserve_tokens import (
    ROOT, FIXTURES, PREFIX, PIN, BASE, REVIEWED, CAPABILITY, digest, git, load,
    read_trx, validate_report, validate_tests, validate_skip,
)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', default='artifacts/parity/branch-summary-reserve')
    parser.add_argument('--write-ledger', action='store_true')
    args = parser.parse_args()
    run = ROOT / args.run
    report = load(run / 'final/report.json')
    cases = load(ROOT / 'tools/parity/branch_summary_reserve_cases.json')['cases']
    focused = read_trx(run / 'tests/focused.trx')
    full = read_trx(run / 'tests/full.trx')
    validate_report(report, cases)
    validate_tests(focused, full, cases)
    skip = load(run / 'skip-regression.json')
    validate_skip(skip)
    require_sha = git('log', '-1', '--format=%H', '--', 'src', 'tests')
    if report['pisharpSha'] != require_sha:
        raise ValueError('Run final differential at the committed implementation head first')
    quality = []
    for command in [['dotnet', 'build', 'PiSharp.slnx', '--warnaserror', '-m:1'],
                    ['dotnet', 'format', 'PiSharp.slnx', '--verify-no-changes']]:
        result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        log = run / ('quality-' + command[1] + '.log')
        log.write_text(result.stdout)
        if result.returncode:
            print(result.stdout)
            raise ValueError('Quality command failed: ' + ' '.join(command))
        quality.append(dict(command=command, exitCode=result.returncode, logSha256=digest(log)))
    for name, value in [('differential', report), ('skip-regression', skip),
                        ('pty-requests', [load(path) for path in sorted((run / 'pty').glob('*.json'))])]:
        (FIXTURES / (PREFIX + name + '-2026-11-12.json')).write_text(json.dumps(value, indent=2) + '\n')
    sources = git('diff', '--name-only', BASE, 'HEAD', '--', 'src', 'tests', 'tools').splitlines()
    sources += ['docs/parity/fixtures/' + PREFIX + name + '-2026-11-12.json'
                for name in ['baseline', 'differential', 'skip-regression', 'pty-requests']]
    manifest = dict(schema=1, capability=CAPABILITY, piSha=PIN, implementationSha=require_sha,
                    milestoneBase=BASE, pr54Merge=BASE, pr54ReviewedHead=REVIEWED,
                    pr54CiRun=43274890874, pr54MergeMethod='merge commit', cleanAtBranchCreation=True,
                    branch=git('branch', '--show-current'), quality=quality,
                    focused=dict(passed=len(focused), failed=0, skipped=0, trxSha256=digest(run / 'tests/focused.trx')),
                    full=dict(passed=len(full), failed=0, skipped=0, trxSha256=digest(run / 'tests/full.trx'), exclusions=[]),
                    sourceHashes={path: digest(ROOT / path) for path in sorted(set(sources))},
                    publication='Evidence commit, final PR head and exact-final-head CI are recorded in the PR description without another metadata commit.',
                    safetyProjections=[],
                    normalization='Stored tokens compared as parsed JSON; nonfinite doubles explicitly encoded. Raw provider transcripts retained; compare exact marker inclusion, order and multiplicity, not existing summary-wrapper newline differences.')
    (FIXTURES / (PREFIX + 'acceptance-2026-11-12.json')).write_text(json.dumps(manifest, indent=2) + '\n')
    if args.write_ledger:
        path = ROOT / 'docs/parity/execution-ledger.json'
        ledger = load(path)
        task = next(t for t in ledger['tasks'] if t['id'] == CAPABILITY)
        task['status'] = 'verified'
        task['implementation'] = [
            'Presence-carrying raw reserve preserves JSON file values and nested merge. Nullish values default to 16384; JSON Number coercion is isolated at the CLI settings boundary.',
            'ConversationTreeSelectionOptions keeps the validated integer API and adds ConfiguredReserveTokens for file-loaded double arithmetic. ContextWindow - reserve feeds the existing preparation with the pinned positive-budget gate.',
            'Positive budgets can select empty history and persist No content to summarize; zero, negative and NaN budgets leave history unlimited. No /settings row or interactive writer.',
            'With explicit scope approval, the independently exposed branch-only output-cap mismatch is corrected to min(4096, positive model max); compaction policy is unchanged.',
        ]
        task['evidence'] = [str(FIXTURES.relative_to(ROOT) / (PREFIX + name + '-2026-11-12.' + extension))
                            for name, extension in [('baseline', 'json'), ('differential', 'json'), ('acceptance', 'json'),
                                                    ('skip-regression', 'json'), ('pty-requests', 'json'), ('receipt', 'md')]]
        task['evidence'].append('tools/parity/verify_settings_branch_summary_reserve_tokens.py')
        path.write_text(json.dumps(ledger, indent=2) + '\n')
    print(json.dumps({key: manifest[key] for key in ['implementationSha', 'focused', 'full']}, indent=2))


if __name__ == '__main__':
    main()
