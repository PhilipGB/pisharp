#!/usr/bin/env python3
"""Independently recompute reserve acceptance from raw JSON, requests, TRX and Git provenance."""
import argparse
import copy
import hashlib
import json
import math
import pathlib
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[2]
PIN = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
BASE = 'bf893d5ab22a7d0503847127e10cb58e46a2f374'
REVIEWED = 'd8da7c4e9900cf5f013961f2f251d28fae4ae695'
CAPABILITY = 'settings-branchsummary-reservetokens'
SKIP = 'settings-branchsummary-skipprompt'
FIXTURES = ROOT / 'docs/parity/fixtures'
PREFIX = 'settings-branchsummary-reservetokens-'
MARKERS = ['OLDEST-MARKER', 'MIDDLE-MARKER', 'RECENT-MARKER', 'SUMMARY-MARKER', 'COMPACTION-MARKER']
REQUIRED_METHODS = [
    'BranchSummaryReserveTokensSettingTests.RawSettingsThroughActualProviderRequestMatchPinnedPi',
    'BranchSummaryReserveTokensSettingTests.SummaryOutputCapIsIndependentOfHistoryReserve',
    'BranchSummaryReserveTokensSettingTests.RawReserveObjectsDeepMergeByPresenceWithoutChangingSkipPrompt',
    'BranchSummaryReserveTokensSettingTests.StronglyTypedNavigationStillRejectsNegativeReserve',
    'BranchSummaryReserveTokensSettingTests.HistoryBudgetRetainsPinnedArithmetic',
    'BranchSummaryReserveTokensSettingTests.NonNumericRadixDigitsResolveToNaN',
    'BranchSummaryReserveTokensPtyTests.TreeSummarizeChoiceUsesFileReserveInPhysicalProviderRequest',
    'NativeExtensionTreeEventTests.BranchSummaryFileDetailsStopAtTheSelectedTokenBudget',
    'TerminalPtyTests.BranchSummaryEscapeCancelsAndReopensTheTreeThroughLinuxPty',
    'TerminalPtyTests.BranchSummarySkipPromptNavigatesLikeChoosingNoSummaryThroughLinuxPty',
    'InteractiveSessionBranchSummaryTests.BranchCommandCanRequestAnExtensionSummary',
]


def require(value, message):
    if not value:
        raise ValueError(message)


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()


def load(path):
    return json.loads(pathlib.Path(path).read_text())


def digest(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def merge(lower, upper):
    result = copy.deepcopy(lower)
    for key, value in upper.items():
        result[key] = merge(result[key], value) if isinstance(result.get(key), dict) and isinstance(value, dict) else value
    return result


def array_string(values):
    def text(value):
        if value is None:
            return ''
        if isinstance(value, list):
            return array_string(value)
        if isinstance(value, dict):
            return '[object Object]'
        if isinstance(value, bool):
            return str(value).lower()
        return str(value)
    return ','.join(text(v) for v in values)


def number(value):
    if value is None:
        return 16384
    if isinstance(value, dict):
        return math.nan
    if isinstance(value, list):
        value = array_string(value)
    if isinstance(value, str):
        value = value.strip('\t\n\v\f\r \u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff')
        if not value:
            return 0
        if value in ['Infinity', '+Infinity', '-Infinity']:
            return float(value)
        if re.fullmatch(r'0[xX][0-9a-fA-F]+|0[bB][01]+|0[oO][0-7]+', value):
            return float(int(value, 0))
        if not re.fullmatch(r'[+-]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][+-]?[0-9]+)?', value):
            return math.nan
    return float(value)


def encoded(value):
    return value if math.isfinite(value) else 'NaN' if math.isnan(value) else 'Infinity' if value > 0 else '-Infinity'


def same_json(left, right):
    # Python equates True with 1 and False with 0; raw JSON representations must not.
    if isinstance(left, bool) or isinstance(right, bool):
        return type(left) is type(right) and left == right
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(same_json(a, b) for a, b in zip(left, right))
    if isinstance(left, dict) and isinstance(right, dict):
        return left.keys() == right.keys() and all(same_json(left[k], right[k]) for k in left)
    return left == right


def expected(item):
    settings = merge(json.loads(item['userRaw']), json.loads(item['projectRaw']) if item['projectTrusted'] else {})
    parent = settings.get('branchSummary')
    branch = parent if isinstance(parent, dict) else {}
    raw = branch.get('reserveTokens')
    reserve = number(raw)
    context = item['contextWindow'] or 128000
    budget = context - reserve
    skip = branch.get('skipPrompt')
    skips = bool(skip) or isinstance(skip, (dict, list))
    # The fixture messages have exactly 40 characters (10 estimated tokens).
    # Existing summary entries have 160 characters and use the pinned 90% exception.
    entries = [('OLDEST-MARKER', 10, False), ('MIDDLE-MARKER', 10, False), ('RECENT-MARKER', 10, False)]
    if item['history'] in ['summary', 'compaction']:
        entries[1] = ('SUMMARY-MARKER' if item['history'] == 'summary' else 'COMPACTION-MARKER', 40, True)
    selected, total = [], 0
    for marker, tokens, summary in reversed(entries):
        if budget > 0 and total + tokens > budget:
            if summary and total < budget * 0.9:
                selected.insert(0, marker)
            break
        selected.insert(0, marker)
        total += tokens
    prepared = [m for m in MARKERS if m in selected]
    calls = int(not skips and bool(selected))
    return dict(id=item['id'], loaded=True, parentPresent='branchSummary' in settings,
                reservePresent='reserveTokens' in branch, raw=raw if 'reserveTokens' in branch else '<absent>',
                effective=encoded(reserve), contextWindow=context, budget=encoded(budget), limiting=budget > 0,
                skips=skips, preparedMarkers=prepared, preparedCount=len(selected),
                markers=prepared if calls else [], providerCalls=calls,
                outputCaps=[min(4096, item['maxTokens']) if item['maxTokens'] > 0 else 4096] if calls else [],
                summaryCreated=not skips, targetSelected=True, emptySummary=not skips and not selected,
                readFiles=['nested.txt'] if item['history'] == 'summary' else ['old.txt'] if item['history'] == 'files' else [],
                modifiedFiles=[], customPrompt=bool(item.get('customInstructions')) and bool(calls), error=None), selected


def validate_report(report, cases):
    require(report['piSha'] == PIN, 'wrong Pi SHA')
    for product in ['pi', 'calibration', 'pisharp']:
        rows = report[product]
        require([r['id'] for r in rows] == [c['id'] for c in cases], f'{product}: missing or duplicated cases')
        for item, row in zip(cases, rows, strict=True):
            want, ordered = expected(item)
            for key, value in want.items():
                require(same_json(row.get(key), value), f'{product} {item["id"]}.{key}: {row.get(key)!r} != {value!r}')
            text = row.get('conversation', '')
            actual_order = sorted([m for m in MARKERS if m in text], key=text.index)
            require(actual_order == (ordered if want['providerCalls'] else []), f'{product}: provider request projection differs')
            require(all(text.count(m) == 1 for m in actual_order), 'provider duplicates selected history')
            require('TARGET' not in text and 'SHARED' not in text, 'provider includes target/shared path')
    require(report['pi'] == report['calibration'], 'same-Pi calibration failure')


def read_trx(path):
    root = ET.parse(path).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    return [(node.attrib['testName'], node.attrib['outcome']) for node in root.findall('.//t:UnitTestResult', ns)]


def validate_tests(focused, full, cases):
    require(bool(focused) and bool(full), 'missing test evidence')
    for results in [focused, full]:
        require(all(outcome == 'Passed' for _, outcome in results), 'failed or skipped test')
    for method in REQUIRED_METHODS:
        require(any(method in name for name, _ in focused), 'missing required test: ' + method)
    for item in cases:
        require(any('RawSettingsThroughActualProviderRequestMatchPinnedPi' in name and f'"{item["id"]}"' in name
                    for name, _ in focused), 'missing required corpus test: ' + item['id'])
    for fragment in ['BranchSummarySkipPromptSettingTests.', 'Compaction', 'ContextBudget']:
        names = [n for n, _ in full if fragment in n]
        require(names and all(n in dict(focused) for n in names), 'missing focused regression: ' + fragment)
    require(len(full) > 4650, 'full suite incomplete')
    require(any('NativeExtensionUiTests.' in n for n, _ in full), 'undocumented native UI exclusion')


def ledger_entries(ledger):
    tasks = ledger['tasks']
    return {t['id']: t for t in tasks}


def validate_ledger(ledger, complete):
    entries = ledger_entries(ledger)
    require(entries[SKIP]['status'] == 'verified', 'skipPrompt ledger regressed')
    if complete:
        require(entries[CAPABILITY]['status'] == 'verified', 'reserveTokens not verified')
    # No compaction capability is promoted by this milestone.
    base = ledger_entries(json.loads(git('show', BASE + ':docs/parity/execution-ledger.json')))
    for key in entries:
        if key != CAPABILITY:
            require(entries[key] == base[key], 'unrelated ledger task changed: ' + key)


def validate_provenance(manifest, report):
    implementation = git('log', '-1', '--format=%H', '--', 'src', 'tests')
    require(manifest['implementationSha'] == report['pisharpSha'] == implementation, 'stale PiSharp implementation SHA')
    require(manifest['piSha'] == PIN, 'wrong manifest Pi SHA')
    require(manifest['milestoneBase'] == manifest['pr54Merge'] == BASE, 'wrong milestone base')
    require(manifest['pr54ReviewedHead'] == REVIEWED and manifest['pr54CiRun'] == 43274890874, 'wrong reviewed merge provenance')
    require(manifest['pr54MergeMethod'] == 'merge commit' and manifest['cleanAtBranchCreation'] is True, 'merge/branch provenance missing')
    for ancestor, descendant in [(REVIEWED, BASE), (BASE, implementation), (implementation, 'HEAD')]:
        require(subprocess.run(['git', 'merge-base', '--is-ancestor', ancestor, descendant], cwd=ROOT).returncode == 0, 'invalid ancestry')
    for path, sha in manifest['sourceHashes'].items():
        require(digest(ROOT / path) == sha, 'source/evidence changed after acceptance: ' + path)
    required_paths = git('diff', '--name-only', BASE, implementation, '--', 'src', 'tests', 'tools').splitlines()
    require(set(required_paths) <= manifest['sourceHashes'].keys(), 'missing implementation source binding')
    require([q['command'][1] for q in manifest['quality']] == ['build', 'format'] and
            all(q['exitCode'] == 0 and q['logSha256'] for q in manifest['quality']), 'quality gates missing or failed')


def validate_skip(document):
    require(document['piSha'] == PIN and len(document['cases']) == 40, 'skipPrompt corpus incomplete')
    fields = ['loaded', 'prompts', 'skipPrompt', 'parentPresent', 'reserve']
    for case in document['cases']:
        require(all(case['pi'][key] == case['pisharp'][key] for key in fields), 'skipPrompt regression')


def validate_pty(rows):
    require(len(rows) == 6, 'missing real tree Summarize PTY requests')
    scenarios = {(row['reserve'], row['contextWindow'], row['skipPrompt']) for row in rows}
    require(scenarios == {('80', 100, False), ('100', 100, False), ('"bad"', 100, False),
                          ('-1', 100, False), ('2.5', 32, False), ('"bad"', 100, True)}, 'PTY scenario mismatch')
    for row in rows:
        item = dict(id='pty', userRaw=row['settings'], projectRaw='{}', projectTrusted=True,
                    contextWindow=row['contextWindow'], maxTokens=8192, history='messages')
        want, ordered = expected(item)
        require(row['targetSelected'] and row['summaryCreated'] == want['summaryCreated'], 'PTY persistence mismatch')
        require(len(row['requests']) == want['providerCalls'], 'PTY provider call mismatch')
        for request in row['requests']:
            text = '\n'.join(message.get('content', '') if isinstance(message.get('content'), str) else
                             '\n'.join(part.get('text', '') for part in message.get('content', []) if part.get('type') == 'text')
                             for message in request['messages'])
            require(sorted([m for m in MARKERS if m in text], key=text.index) == ordered, 'PTY request history mismatch')
            require(request.get('max_completion_tokens', request.get('max_tokens')) == 4096, 'PTY output cap mismatch')


def validate_publication(document, head):
    require(document['headSha'] == head and document['conclusion'] == 'success' and document['status'] == 'completed', 'exact-final-head CI not successful')
    require(len(document['jobs']) == 5 and all(j['conclusion'] == 'success' for j in document['jobs']), 'CI jobs not all green')
    require(document['pr']['headRefOid'] == head and document['pr']['isDraft'] and document['pr']['state'] == 'OPEN' and document['pr']['mergedAt'] is None,
            'new PR must remain draft, open, unmerged at exact head')


def self_test(report, cases, manifest, focused, full, ledger, skip):
    rejected = []

    def reject(label, check):
        try:
            check()
        except (ValueError, KeyError, AssertionError):
            rejected.append(label)
            return
        raise ValueError('mutation accepted: ' + label)

    mutations = [
        ('default changed', 'BR01-unset', 'effective', 16000),
        ('normal ignored', 'BR-normal', 'effective', 16384),
        ('project fails override', 'BR05-override', 'effective', 4096),
        ('sibling erases inherited', 'BR09-sibling', 'effective', 16384),
        ('trusted null falls through', 'BR06-null', 'effective', 4096),
        ('untrusted affects reserve', 'BR07-untrusted', 'effective', 1024),
        ('later duplicate loses', 'BR-duplicate-100-20', 'effective', 100),
        ('reverse duplicate loses', 'BR-duplicate-20-100', 'effective', 20),
        ('unknown sibling blocks', 'BR-unknown-sibling', 'loaded', False),
        ('negative rejected', 'BR-negative', 'loaded', False),
        ('fraction rejected', 'BR-fraction', 'loaded', False),
        ('fraction truncated', 'BR-fraction', 'markers', MARKERS[:3]),
        ('numeric string wrong', 'BR-numeric-string', 'effective', 16384),
        ('raw true becomes one', 'BR-true', 'raw', 1),
        ('raw false becomes zero', 'BR-false', 'raw', 0),
        ('bad defaults', 'BR-bad-string', 'effective', 16384),
        ('object defaults', 'BR-object', 'effective', 16384),
        ('array defaults', 'BR-array', 'effective', 16384),
        ('single array wrong', 'BR-single-array', 'effective', 0),
        ('zero reserve wrong', 'BR-zero', 'markers', []),
        ('zero budget limits', 'BR-equal', 'markers', []),
        ('context equal limits', 'BR-equal', 'limiting', True),
        ('negative budget limits', 'BR-greater', 'markers', []),
        ('context ignored', 'BR-second-context', 'budget', 100),
        ('selected history differs', 'BR-normal', 'markers', []),
        ('old messages wrongly included', 'BR-normal', 'markers', MARKERS[:3]),
        ('recent excluded', 'BR-threshold', 'markers', []),
        ('provider projection differs', 'BR-normal', 'conversation', '[User]: OLDEST-MARKER'),
        ('cap tied to reserve', 'BR-normal', 'outputCaps', [4096 - 20]),
        ('skipPrompt summarizes', 'BR-skip-true', 'providerCalls', 1),
        ('empty summary omitted', 'BR-tiny-budget', 'summaryCreated', False),
        ('file tracking lost', 'BR-cutoff-files', 'readFiles', []),
        ('summary cutoff wrong', 'BR-cutoff-summary', 'preparedCount', 1),
    ]
    for label, id, field, value in mutations:
        mutated = copy.deepcopy(report)
        next(c for c in mutated['pisharp'] if c['id'] == id)[field] = value
        reject(label, lambda: validate_report(mutated, cases))
    mutated = copy.deepcopy(report)
    mutated['calibration'][0]['budget'] = 12
    reject('calibration failure', lambda: validate_report(mutated, cases))
    mutated = copy.deepcopy(report)
    mutated['piSha'] = '0' * 40
    reject('wrong Pi SHA', lambda: validate_report(mutated, cases))
    mutated_manifest = copy.deepcopy(manifest)
    mutated_manifest['implementationSha'] = BASE
    reject('stale implementation', lambda: validate_provenance(mutated_manifest, report))
    for label, rows in [('missing required test', focused[1:]),
                        ('failed test', [(focused[0][0], 'Failed')] + focused[1:]),
                        ('skipped test', [(focused[0][0], 'NotExecuted')] + focused[1:])]:
        if label == 'missing required test':
            rows = [r for r in focused if REQUIRED_METHODS[0] not in r[0]]
        reject(label, lambda: validate_tests(rows, full, cases))
    reject('full-suite failure', lambda: validate_tests(focused, full + [('failure', 'Failed')], cases))
    for capability in [CAPABILITY, SKIP]:
        mutated = copy.deepcopy(ledger)
        next(t for t in mutated['tasks'] if t['id'] == capability)['status'] = 'not_started'
        reject('ledger regressed ' + capability, lambda: validate_ledger(mutated, True))
    mutated_skip = copy.deepcopy(skip)
    mutated_skip['cases'][0]['pisharp']['prompts'] = False
    reject('skipPrompt boundary regressed', lambda: validate_skip(mutated_skip))
    publication = dict(headSha='head', conclusion='success', status='completed', jobs=[dict(conclusion='success')] * 5,
                       pr=dict(headRefOid='head', isDraft=True, state='OPEN', mergedAt=None))
    validate_publication(publication, 'head')
    publication['headSha'] = 'stale'
    reject('CI stale head', lambda: validate_publication(publication, 'head'))
    print(f'{len(rejected)} mutation controls rejected')
    return rejected


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', default=str(FIXTURES / (PREFIX + 'acceptance-2026-11-12.json')))
    parser.add_argument('--report', default=str(FIXTURES / (PREFIX + 'differential-2026-11-12.json')))
    parser.add_argument('--focused-trx', required=True)
    parser.add_argument('--full-trx', required=True)
    parser.add_argument('--allow-in-progress', action='store_true')
    parser.add_argument('--publication')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    manifest, report = load(args.manifest), load(args.report)
    cases = load(ROOT / 'tools/parity/branch_summary_reserve_cases.json')['cases']
    require(len(cases) == 54, 'mandatory corpus changed')
    validate_report(report, cases)
    validate_provenance(manifest, report)
    focused, full = read_trx(args.focused_trx), read_trx(args.full_trx)
    validate_tests(focused, full, cases)
    require(digest(args.focused_trx) == manifest['focused']['trxSha256'] and
            digest(args.full_trx) == manifest['full']['trxSha256'], 'test evidence digest mismatch')
    require(manifest['focused']['passed'] == len(focused) and manifest['full']['passed'] == len(full), 'test count mismatch')
    ledger = load(ROOT / 'docs/parity/execution-ledger.json')
    validate_ledger(ledger, not args.allow_in_progress)
    skip = load(FIXTURES / (PREFIX + 'skip-regression-2026-11-12.json'))
    validate_skip(skip)
    validate_pty(load(FIXTURES / (PREFIX + 'pty-requests-2026-11-12.json')))
    if args.publication:
        validate_publication(load(args.publication), git('rev-parse', 'HEAD'))
    if args.self_test:
        self_test(report, cases, manifest, focused, full, ledger, skip)
    publication = 'exact-final-head CI verified' if args.publication else 'publication not evaluated (local gates only)'
    print(f'{CAPABILITY}: 54 executable cases verified; focused={len(focused)}, full={len(full)}; {publication}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError) as error:
        print('verification failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
