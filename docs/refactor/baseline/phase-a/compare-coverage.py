from pathlib import Path
import hashlib,json,xml.etree.ElementTree as ET
repo=Path.cwd();out=Path('/tmp/pisharp-phase-a');hot=json.loads((repo/'docs/refactor/baseline/frozen-hotspots.json').read_text())['hotspots']
paths=[next((repo/f'docs/refactor/baseline/run-{i}/dotnet').glob('*/coverage.cobertura.xml')) for i in [1,2]]+[next((out/'coverage').glob('*/coverage.cobertura.xml'))]
runs=[]
for label,path in zip(['baseline-run-1','baseline-run-2','candidate-diagnostic'],paths):
 root=ET.parse(path).getroot();classes=root.findall('.//class');files={}
 for c in classes:
  filename=c.attrib['filename'];entry=files.setdefault(filename,{'classCount':0,'lines':{}});entry['classCount']+=1
  for line in c.findall('./lines/line'):
   number=int(line.attrib['number']);entry['lines'][number]=max(entry['lines'].get(number,0),int(line.attrib['hits']))
 for f in files.values():
  lines=f.pop('lines');f['coveredDistinctSourceLines']=sum(h>0 for h in lines.values());f['distinctSourceLines']=len(lines);f['lineRate']=f['coveredDistinctSourceLines']/len(lines) if lines else None
 runs.append({'id':label,'path':str(path),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'overall':dict(root.attrib),'originalHotspotFiles':{h['id']:{'path':h['path'],'coverage':files.get(h['path'])} for h in hot},'extractedOwners':{p:v for p,v in files.items() if p.startswith('src/') and p not in {h['path'] for h in hot} and any(x in Path(p).name for x in ['TerminalDialog','TerminalProgress','TerminalTranscriptSearchInput','TerminalAuthorizationInput','TerminalRenderScheduler','TerminalConversationPresentation','TerminalScreenNotifications','AgentToolCatalog','PiCompactionSummarizer','ProviderTurnJournal','ProviderRequestAttribution','ProviderAttemptStream'])}})
trx=ET.parse(out/'coverage/tests.trx').getroot();ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};failures=[]
for n in trx.findall('.//t:UnitTestResult',ns):
 if n.attrib['outcome']!='Passed':
  message=n.find('t:Output/t:ErrorInfo/t:Message',ns).text;reason='Injected Coverlet tracker adds actual FileStream calls to instrumented Core; ordinary source/binary boundary remains enforced.' if n.attrib['testName']=='PiSharp.Tests.ArchitectureTests.CoreDoesNotCallTerminalFilesystemProcessOrNetworkApis' else 'Child instrumented process shutdown exits 134/abandoned tracker mutex, reproduced in original diagnostic baseline.'
  assert 'Coverlet.Core.Instrumentation.Tracker' in message or '134' in message or 'AbandonedMutexException' in message
  failures.append({'identity':n.attrib['testName'],'message':message,'classification':reason})
result={'sourceSha':'10e0d60eb8b192a5afcccf813281a753c3767553','runs':runs,'candidateDiagnosticFailures':failures,'limitations':['All three full collections are diagnostic because instrumented child-process tracker faults alter outcomes; compare rates as measured evidence, not an ordinary full-success coverage guarantee.','Raw distinct-file line counts consolidate compiler-generated class source locations. Flow moves distribute coverage over new owner files; root percentage alone is not a capability comparison.','No numeric threshold is introduced; full ordinary 1382-row/1858-identity verification remains authoritative.','The six architecture tests are not disabled or filtered from the full collection; one correctly detects the injected tracker I/O. Rebuild and ordinary boundary/doc tests check the restored product binary.','Other platforms/live endpoints are not established by these Linux measurements.']}
(out/'coverage-comparison.json').write_text(json.dumps(result,indent=2)+'\n');print([(r['id'],r['overall']['line-rate'],r['overall']['branch-rate']) for r in runs]);print('Diagnostic failures',len(failures))
