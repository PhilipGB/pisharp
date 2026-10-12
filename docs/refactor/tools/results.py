#!/usr/bin/env python3
"""Stable logical identities and semantic outcome-set comparison.

No duration, ordering, generated GUID, workspace path or timestamp participates.
Keep raw TRX/TAP/unittest/MCP/terminal reports alongside this normalized index.
Mapping files are explicit baseline-key -> candidate-key objects; omissions fail.
"""
import argparse
import collections
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

def add(out, key, value):
    if key in out:
        raise ValueError(f'Duplicate logical identity {key}')
    out[key] = value

def dotnet(path):
    root = ET.parse(path).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    out = {}
    grouped=collections.defaultdict(list)
    for result in root.findall('.//t:UnitTestResult', ns):
        name = result.attrib['testName']
        base, sep, row = name.partition('(')
        logical = '.'.join(base.split('.')[-2:]) + (sep+row if sep else '')
        skip = result.attrib['outcome'] in ['NotExecuted', 'Skipped']
        message = result.find('t:Output/t:ErrorInfo/t:Message', ns)
        grouped['dotnet/'+logical].append(dict(outcome=result.attrib['outcome'],
              skipped=skip, skipReason=message.text if skip and message is not None else None))
    for key, rows in grouped.items():
        # VSTest truncates long argument displays, so some distinct rows share a
        # display identity. Preserve their exact count and semantic outcome multiset.
        # The original TRX retains per-row adapter IDs for movement audits.
        add(out,key,dict(rowCount=len(rows),outcomes=sorted(rows,key=lambda r:json.dumps(r,sort_keys=True))))
    return out

def capture(folder):
    out = {}
    # Corrected standard invocations supersede the optional collector attempts.
    trx = folder / 'dotnet-standard/tests.trx'
    if not trx.exists():
        trx = folder / 'dotnet/tests.trx'
    out.update(dotnet(trx))
    for line in (folder/'node.log').read_text().splitlines():
        match = re.match(r'^(ok|not ok) \d+ - (.*?)(?: # (SKIP|TODO)(?: (.*))?)?$', line)
        if match:
            status, name, skip, reason = match.groups()
            add(out, 'node/'+name, dict(outcome=status, skipped=bool(skip), skipReason=reason))
    for line in (folder/'python.log').read_text().splitlines():
        match = re.match(r'^test_.* \((.*?)\) \.\.\. (ok|FAIL|ERROR|skipped.*)$', line)
        if match:
            name, status = match.groups()
            add(out, 'python/'+name, dict(outcome=status, skipped=status.startswith('skipped'),
                                        skipReason=status if status.startswith('skipped') else None))
    for invocation in folder.glob('*.invocation.json'):
        record = json.loads(invocation.read_text())
        key = record['id']
        if '.invalid-attempt.' in invocation.name or '.before-pi-build.' in invocation.name:
            continue
        if key in ['build', 'format', 'mcp'] or key.startswith('assert-') or key.endswith('differential') or 'differential-' in key:
            add(out, 'suite/'+key, dict(outcome='pass' if record['exitCode']==0 else 'fail', skipped=False, skipReason=None))
        if key.startswith('terminal-'):
            report = folder/key/'report.json'
            if not report.exists():
                add(out, 'suite/'+key, dict(outcome='not-run', skipped=True, skipReason='See invocation log'))
                continue
            data = json.loads(report.read_text())
            for case in data['cases']:
                # Scenario-error first lines retain readiness/exit diagnostics;
                # rendered screen contents are authoritative in the raw report.
                errors = {k:v.splitlines()[0] for k,v in case.get('scenarioErrors', {}).items()}
                add(out, 'terminal/'+key+'/'+case['id'], dict(outcome='match' if case['match'] else 'difference',
                    terminalMatch=case['terminalMatch'], renderMatch=case.get('renderDifferences',0)==0,
                    httpMatch=case.get('httpMatch'), scenarioErrors=errors, skipped=False, skipReason=None))
    # Official per-check MCP JSON capture, distinct from overall exit status.
    checks = collections.defaultdict(list)
    for path in (folder/'mcp-results').rglob('checks.json'):
        mode, scenario = path.relative_to(folder/'mcp-results').parts[:2]
        for check in json.loads(path.read_text()):
            if isinstance(check, dict) and 'id' in check:
                if str(check.get('status', '')).upper() != 'INFO':
                    checks[f'mcp/{mode}/{scenario}/{check["id"]}'].append(check)
    # Different package modes may use object wrappers; raw JSON remains retained.
    for key, values in checks.items():
        statuses = sorted({str(v.get('status','unknown')).upper() for v in values})
        add(out, key, dict(outcome=statuses, skipped='SKIPPED' in statuses,
                          skipReason=[v.get('errorMessage',v.get('description')) for v in values if str(v.get('status','')).upper()=='SKIPPED'] or None))
    for path in (folder/'mcp-results').glob('*/*/pisharp-client.json'):
        mode, scenario = path.relative_to(folder/'mcp-results').parts[:2]
        report=json.loads(path.read_text())
        add(out, f'mcp/{mode}/{scenario}/pisharp-client', dict(outcome='pass' if report.get('success') else 'fail', skipped=False, skipReason=None))
    return out

def freeze(root):
    runs = [capture(root/f'run-{n}') for n in [1,2]]
    identities = set(runs[0]) | set(runs[1])
    missing = [key for key in identities if any(key not in run for run in runs)]
    if missing:
        raise ValueError(f'Baseline runs have different identities: {missing}')
    outcomes, flaky = {}, []
    for key in sorted(identities):
        values = {json.dumps(run[key], sort_keys=True) for run in runs}
        outcomes[key] = [json.loads(v) for v in sorted(values)]
        if len(values)>1:
            flaky.append(key)
    return dict(identities=outcomes, baselineFlaky=flaky,
                counts=dict(collections.Counter(k.split('/')[0] for k in identities)),
                dotnetRowCount=sum(v['rowCount'] for k,v in runs[0].items() if k.startswith('dotnet/')))

def compare(baseline, candidate, mapping):
    failures = []
    for key, acceptable in baseline['identities'].items():
        target = mapping.get(key, key)
        if target not in candidate:
            failures.append(dict(identity=key, candidate=target, reason='missing'))
        elif candidate[target] not in acceptable:
            failures.append(dict(identity=key, candidate=target, reason='new semantic outcome',
                                 acceptable=acceptable, actual=candidate[target]))
    return dict(pass_=not failures, failures=failures,
                baselineFlaky=baseline['baselineFlaky'],
                expected=len(baseline['identities']), candidate=len(candidate))

if __name__=='__main__':
    cli=argparse.ArgumentParser()
    cli.add_argument('--baseline', type=Path, required=True)
    cli.add_argument('--candidate', type=Path)
    cli.add_argument('--mapping', type=Path)
    cli.add_argument('--output', type=Path, required=True)
    args=cli.parse_args()
    data=freeze(args.baseline) if args.baseline.is_dir() else json.loads(args.baseline.read_text())
    if args.candidate:
        data=compare(data, capture(args.candidate), json.loads(args.mapping.read_text()) if args.mapping else {})
    args.output.write_text(json.dumps(data,indent=2)+'\n')
    print(json.dumps({k:v for k,v in data.items() if k not in ['identities','failures']},indent=2))
    if 'pass_' in data and not data['pass_']:
        raise SystemExit(1)
