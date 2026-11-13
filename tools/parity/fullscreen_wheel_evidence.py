#!/usr/bin/env python3
"""Freeze raw differential, compact test outcomes, source bindings and physical PTY projections."""
import argparse
import hashlib
import json
import pathlib
import subprocess
import xml.etree.ElementTree as ET
from fullscreen_wheel_cases import PIN, BASE
from verify_fullscreen_wheel import PROJECTIONS

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT/'docs/parity/fixtures'


def write(name, value):
    (FIXTURES/('settings-fullscreenwheelscrolllines-'+name+'.json')).write_text(json.dumps(value,indent=2)+'\n')


def tests(path):
    root=ET.parse(path).getroot()
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    return sorted([dict(name=x.attrib['testName'],outcome=x.attrib['outcome'])
                   for x in root.findall('.//t:UnitTestResult',ns)],key=lambda x:x['name'])


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--implementation',required=True)
    parser.add_argument('--artifacts',required=True)
    args=parser.parse_args()
    artifacts=pathlib.Path(args.artifacts)
    implementation=args.implementation
    differential=json.loads((artifacts/'final/report.json').read_text())
    assert differential['pisharpSha']==implementation
    write('differential',differential)
    write('pty',{p.stem:json.loads(p.read_text()) for p in sorted((artifacts/'pty-final').glob('*.json'))})
    changed=subprocess.check_output(['git','diff','--name-only',BASE,implementation],cwd=ROOT,text=True).splitlines()
    paths=[p for p in changed if p.endswith(('.cs','.csproj','.py','.mjs'))]
    paths += ['src/PiSharp.Cli/Configuration/UserSettings.cs','src/PiSharp.Cli/Configuration/UserSettingsWriter.cs',
              'src/PiSharp.Cli/Tui/Rendering/TerminalPresentation.cs','src/PiSharp.Cli/Tui/Commands/TerminalSettingsCommands.cs',
              'src/PiSharp.Cli/Configuration/UserSettingsPersistence.cs','src/PiSharp.Cli/Sessions/ProjectRuntimeContext.cs']
    bindings={p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in sorted(set(paths))}
    focused=tests(artifacts/'tests/focused-final.trx')
    full=tests(artifacts/'tests/full-final.trx')
    quality={}
    for kind in ['build','format']:
        receipt=json.loads((artifacts/(kind+'-gate.json')).read_text())
        assert receipt['exitCode']==0 and receipt['source']==implementation
        quality[kind]=receipt
    write('acceptance',dict(piSha=PIN,baseSha=BASE,mergeSha=BASE,
          reviewedHead='5133403d7943574da59cfb0c0fd2cc3e3a98b4e7',
          baselineCommit='02bc8c921',implementationSha=implementation,projections=PROJECTIONS,
          sourceBindings=bindings,tests=dict(focused=focused,full=full),requiredTests=[x['name'] for x in focused],quality=quality))
    print(f'Frozen {len(focused)} focused / {len(full)} full tests; {len(bindings)} source bindings')


if __name__=='__main__':
    main()
