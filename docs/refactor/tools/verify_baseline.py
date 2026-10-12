#!/usr/bin/env python3
"""Verify capture completeness against the unchanged repository suite inventory."""
import argparse
import itertools
import json
from pathlib import Path
import subprocess
from results import capture, freeze

cli=argparse.ArgumentParser()
cli.add_argument('--repo',type=Path,required=True)
cli.add_argument('--baseline',type=Path,required=True)
args=cli.parse_args()
repo,root=args.repo.resolve(),args.baseline.resolve()
selection=json.loads((root/'source-selection.json').read_text())
metrics=json.loads((root/'architecture.json').read_text())
assert metrics['sha']==selection['pisharp']
hotspots=json.loads((root/'frozen-hotspots.json').read_text())
assert hotspots['sha']==selection['pisharp'] and len(hotspots['hotspots'])==10
assert len({h['id'] for h in hotspots['hotspots']})==10
assert all(h['disposition']=='OPEN' for h in hotspots['hotspots'])
assert all(h['path'] in {f['path'] for f in metrics['files']} for h in hotspots['hotspots'])
fixtures=sorted((repo/'tools/parity/terminal/fixtures').glob('*.json'))
summary=[]
for number in [1,2]:
    folder=root/f'run-{number}'
    outcomes=capture(folder)
    dotnet=[v for k,v in outcomes.items() if k.startswith('dotnet/')]
    assert sum(v['rowCount'] for v in dotnet)==1356
    assert all(r['outcome']=='Passed' and not r['skipped'] for v in dotnet for r in v['outcomes'])
    assert len([k for k in outcomes if k.startswith('node/')])==24
    assert len([k for k in outcomes if k.startswith('python/')])==38
    assert len([k for k in outcomes if k.startswith('mcp/')])==269
    assert len([k for k in outcomes if k.startswith('terminal/')])==136
    for fixture in fixtures:
        definition=json.loads(fixture.read_text())
        expected={f"{dim['columns']}x{dim['rows']}-{theme}-{mode}" for dim,theme,mode in
                  itertools.product(definition['dimensions'],definition['themes'],definition['modes'])}
        for label in ['calibration','paired']:
            report=json.loads((folder/f'terminal-{fixture.stem}-{label}'/'report.json').read_text())
            assert report['piSha']==selection['pi'] and report['pisharpSha']==selection['pisharp']
            assert {case['id'] for case in report['cases']}==expected
            assert len(report['cases'])==len(expected)
    for script in (repo/'tools/parity').glob('*differential.py'):
        assert (folder/f'{script.stem}.invocation.json').exists()
    for suffix in ['context-compaction','session-transitions','state-identity','thinking-minimal','thinking-max']:
        assert (folder/f'virtual-model-routing-differential-{suffix}.invocation.json').exists()
    for name in ['format','build','mcp','node','python','dotnet-standard']:
        invocation=json.loads((folder/f'{name}.invocation.json').read_text())
        assert invocation['exitCode']==0
    summary.append(dict(run=number,dotnetRows=1356,node=24,python=38,mcpChecks=269,
                        terminalCases=136,fixtureCount=len(fixtures)))
frozen=freeze(root)
assert len(frozen['identities'])>=1356
assert subprocess.run(['git','diff','--quiet',selection['pisharp'],'--','src','tests','tools','.github','PiSharp.slnx'],cwd=repo).returncode==0
(root/'completeness-verification.json').write_text(json.dumps(dict(passed=True,runs=summary,
                    semanticCounts=frozen['counts'],dotnetRows=frozen['dotnetRowCount'],
                    baselineFlaky=frozen['baselineFlaky']),indent=2)+'\n')
print(json.dumps(summary,indent=2))
