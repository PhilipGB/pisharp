#!/usr/bin/env python3
"""Independent acceptance verifier. Recompute raw settings, wheel math, offsets and gates."""
import argparse
import copy
import hashlib
import json
import math
import pathlib
import subprocess
from fullscreen_wheel_cases import corpus, PIN, BASE, KEY

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / 'docs/parity/fixtures'
CAPABILITY = 'settings-fullscreenwheelscrolllines'
PROJECTIONS = {
    'duplicates': 'Existing general top-level duplicate-key rejection is retained; Pi JSON.parse keeps the later value.',
    'scopeUi': 'PiSharp adds Inherit / default and user/project scope controls; canonical choices use the resolved value.',
    'viewport': 'Both fullscreen fixtures have an 18-row transcript viewport at 80x24; editor/footer chrome is not compared.',
    'list': 'Pinned SelectList projects any nonzero wheel delta to one selection step; delta and gesture history are still compared.',
    'customSeven': '7 is offered only when already resolved (here inherited from project); no arbitrary text entry was added.'
}


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def equal(a, b):
    return json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True)


def run(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def normalized(value):
    return max(1, min(100, math.floor(value))) if type(value) in [int, float] and math.isfinite(value) else 'auto'


class Wheel:
    def __init__(self, setting, accelerate=True):
        self.accelerate = accelerate
        self.set(setting)

    def set(self, value):
        self.setting = value
        self.last = None
        self.direction = 0
        self.average = None
        self.carry = 0

    def next(self, direction, now):
        if self.setting != 'auto':
            return max(1, math.floor(self.setting))
        if not self.accelerate:
            return 1
        gap = math.inf if self.last is None else now-self.last
        same = direction == self.direction and gap <= 200
        self.last, self.direction = now, direction
        if not same:
            self.average, self.carry = None, 0
            return 1
        if gap < 5:
            return 1
        self.average = gap if self.average is None else (self.average+gap)/2
        total = min(6, max(1, 100/self.average)) + self.carry
        result = math.floor(total)
        self.carry = total-result
        return result


def tasks(ledger):
    return {x['id']: x for x in ledger['tasks']}


def verify(bundle, bindings=True, github=False):
    d, a = bundle['differential'], bundle['acceptance']
    require(d['piSha'] == PIN and a['piSha'] == PIN, 'wrong Pi SHA')
    require(equal(d['corpus'], corpus()), 'corpus changed or missing mandatory cases')
    require(d['pisharpSha'] == a['implementationSha'], 'stale implementation SHA')
    require(a['baseSha'] == BASE and a['mergeSha'] == BASE and a['reviewedHead'] ==
            '5133403d7943574da59cfb0c0fd2cc3e3a98b4e7', 'wrong merge provenance')
    require(equal(a['projections'], PROJECTIONS), 'projection declaration removed or changed')
    require(equal(d['pi'], d['calibration']), 'same-Pi calibration differs')
    for product in ['pi', 'calibration', 'pisharp']:
        observations = d[product]
        for group in ['settings', 'sequences', 'viewports', 'overlays']:
            require([x['id'] for x in observations[group]] == [x['id'] for x in corpus()[group]], f'{product} missing {group}')
        for case, observed in zip(corpus()['settings'], observations['settings'], strict=True):
            user, project = json.loads(case['userRaw']), json.loads(case['projectRaw'])
            raw = project[KEY] if case['projectTrusted'] and KEY in project else user.get(KEY)
            value = normalized(raw)
            if product == 'pisharp' and case['id'].startswith('duplicate-'):
                require(observed['loaded'] is False and 'duplicate property' in observed['error'], 'duplicate projection not preserved')
                continue
            require(observed['loaded'] is True, f'{product} failed loading {case["id"]}')
            require(equal(observed['effective'], value), f'{product} effective {case["id"]}')
            require(observed['current'] == str(value), f'{product} selector current {case["id"]}')
            require(observed['label'] == 'Fullscreen wheel scrolling', 'selector label')
            require(observed['description'] == "Lines per mouse-wheel event in fullscreen mode; 'auto' speeds up fast wheel spins where the terminal does not", 'selector description')
            numbers = sorted({1, 2, 3, 5, 10} | ({value} if type(value) is int else set()))
            require(observed['options'] == ['auto'] + list(map(str, numbers)), 'selector option inclusion/order')
        for case, observed in zip(corpus()['sequences'], observations['sequences'], strict=True):
            accelerated = not (case.get('platform') == 'darwin' and case.get('ssh') is None) if 'platform' in case else case['accelerate']
            require(observed['accelerate'] is accelerated, 'platform acceleration decision')
            wheel = Wheel(case['setting'], accelerated)
            expected = []
            for event in case['events']:
                if 'set' in event:
                    wheel.set(event['set'])
                expected.append(wheel.next(event['direction'], event['t']))
            require(observed['lines'] == expected, f'{product} wheel sequence {case["id"]}')
        for case, observed in zip(corpus()['viewports'], observations['viewports'], strict=True):
            regular = case['mode'] == 'regular'
            maximum = 0 if regular else 282
            if not regular:
                require(observed['maximum'] == maximum, 'unequal fullscreen transcript geometry')
            position = {'top': maximum, 'near-top': maximum-2, 'near-bottom': 2}.get(case['start'], 0)
            require(observed['initial'] == position, 'viewport initial offset')
            require(len(observed['events']) == len(case['events']), 'missing viewport event')
            wheel = Wheel(case['setting'])
            for event, actual in zip(case['events'], observed['events'], strict=True):
                if 'set' in event:
                    wheel.set(event['set'])
                lines = 0 if regular else wheel.next(event['direction'], event['t'])
                multiplier = 5 if event['alt'] else 1
                after = min(maximum, max(0, position - event['direction']*lines*multiplier))
                require(equal(actual, dict(before=position, lines=lines, multiplier=multiplier, after=after, movement=after-position)),
                        f'{product} actual viewport movement {case["id"]}')
                position = after
        for case, observed in zip(corpus()['overlays'], observations['overlays'], strict=True):
            wheel = Wheel(case['setting'])
            deltas = [-wheel.next(-1, t)*(5 if case['alt'] else 1) for t in [0,20]]
            expected = dict(id=case['id'], selected=18, deltas=deltas, transcriptMovement=0, nextTranscriptMovement=wheel.next(-1, 40))
            require(equal(observed, expected), f'{product} overlay routing/history')
        require(equal(observations['setters'], [dict(input=x, stored=normalized(x), effective=normalized(x)) for x in corpus()['setters']]),
                'setter normalization/canonical persistence')
    # Frozen baseline must predate all production changes, and expose the actual initial failures.
    b = bundle['baseline']
    require(b['pisharpSha'] == BASE and b['piSha'] == PIN and equal(b['pi'], b['calibration']), 'baseline provenance/calibration')
    matches = sum(equal(p.get('effective'), s.get('effective')) and p['loaded'] == s['loaded']
                  for p,s in zip(b['pi']['settings'], b['pisharp']['settings'], strict=True))
    require(matches == 28 and len(b['pi']['settings']) == 42, 'fail-first resolution counts')
    require(equal(b['pi']['sequences'], b['pisharp']['sequences']), 'accelerator baseline changed')
    old, new = tasks(bundle['ledgerBefore']), tasks(bundle['ledger'])
    require(new.keys() == old.keys(), 'capability inventory changed')
    require(new[CAPABILITY]['status'] == 'verified', 'wheel capability not verified')
    for key in old.keys()-{CAPABILITY}:
        require(equal(old[key], new[key]), f'unrelated ledger capability changed: {key}')
    for key in ['settings-treefiltermode', 'settings-doubleescapeaction', 'settings-branchsummary-skipprompt', 'settings-branchsummary-reservetokens']:
        require(new[key]['status'] == 'verified', f'previous milestone lost: {key}')
    required = a['requiredTests']
    require(len(required) >= 120 and len(required) == len(set(required)), 'missing mandatory focused test inventory')
    for kind in ['focused', 'full']:
        tests = a['tests'][kind]
        names = [x['name'] for x in tests]
        require(len(names) == len(set(names)), 'duplicate test evidence')
        require(set(required) <= set(names), 'missing test')
        require(all(x['outcome'] == 'Passed' for x in tests), f'{kind} failed/skipped test')
    require(len(a['tests']['full']) >= 4874, 'full-suite exclusion or missing tests')
    for kind in ['build', 'format']:
        require(a['quality'][kind]['exitCode'] == 0 and a['quality'][kind]['source'] == a['implementationSha'], f'{kind} gate failed/stale')
    pty = bundle['pty']
    required_checkpoints = {'startup-fixed-1':1,'live-fixed-5':5,'restart-fixed-5':5,'live-auto-reset':1,
                            'restart-auto-first':1,'selected-custom-7':7,'restart-fixed-7':7,
                            'trusted-project-3':3,'reset-inherited-7':7}
    observed_checkpoints = {x['checkpoint']: x for report in pty.values() for x in report['observations']}
    for key, movement in required_checkpoints.items():
        x = observed_checkpoints[key]
        require(x['before']-x['after'] == movement == x['movement'], 'PTY live/startup/persistence movement')
        require(f'ROW-{x["after"]:03}' in x['frame'], 'PTY frame not bound to movement')
    persisted = {x['checkpoint']:x['stored'] for report in pty.values() for x in report['observations'] if 'stored' in x}
    require(equal(persisted, {'persist-5':5,'persist-7':7,'persist-auto':'auto'}), 'PTY canonical writes')
    if bindings:
        require(run('git','merge-base',BASE,a['implementationSha']) == BASE, 'implementation not based on post-55 main')
        for path, digest in a['sourceBindings'].items():
            current = (ROOT/path).read_bytes()
            committed = subprocess.check_output(['git','show',a['implementationSha']+':'+path],cwd=ROOT)
            require(hashlib.sha256(current).hexdigest() == digest == hashlib.sha256(committed).hexdigest(), 'stale implementation/source binding: '+path)
        require(run('git','merge-base','--is-ancestor',a['baselineCommit'],a['implementationSha']) == '', 'baseline not ancestor')
    if github:
        ci = bundle['ci']
        require(ci['head'] == run('git','rev-parse','HEAD'), 'CI final head is stale')
        require(ci['state'] == 'OPEN' and ci['draft'] is True and ci['merged'] is False, 'milestone PR must remain draft/open/unmerged')
        require(len(ci['runs']) >= 2, 'missing push or PR CI')
        require({x['event'] for x in ci['runs']} >= {'push','pull_request'}, 'missing exact-head CI event')
        for item in ci['runs']:
            require(item['head'] == ci['head'] and item['conclusion'] == 'success', 'exact-head CI stale or failed')
            require(len(item['jobs']) == 5 and all(x['conclusion'] == 'success' and x['head_sha'] == ci['head'] for x in item['jobs']), 'CI job failed or stale')
    return True


def negative_controls(bundle):
    controls = []
    def mutate(name, fn):
        changed = copy.deepcopy(bundle)
        fn(changed)
        try:
            verify(changed, bindings=False, github=True)
        except (AssertionError, KeyError, ValueError):
            controls.append(name)
        else:
            raise AssertionError('Mutation accepted: '+name)
    def setting(b, name):
        return next(x for x in b['differential']['pisharp']['settings'] if x['id']==name)
    for name, case, value in [('default-away-from-auto','absent',1),('numeric-ignored','stored-7','auto'),
        ('zero-not-clamped','stored-0',0),('negative-not-clamped','stored--1',-1),('round-instead-of-floor','stored-1.9',2),
        ('maximum-not-clamped','stored-1000',1000),('malformed-inherits','project-bogus',7),
        ('null-falls-through','project-null',7),('trusted-ignored','project-fixed',7),('untrusted-applied','project-untrusted',3)]:
        mutate(name, lambda b,c=case,v=value: setting(b,c).update(effective=v))
    mutate('wrong-selector-current',lambda b:setting(b,'stored-7').update(current='auto'))
    mutate('custom-option-omitted',lambda b:setting(b,'stored-7')['options'].remove('7'))
    mutate('option-order',lambda b:setting(b,'stored-4')['options'].reverse())
    for name, case, index, value in [('fixed-count','fixed-7',0,1),('first-auto','gap-20',0,5),('burst-4','gap-4',1,6),
        ('boundary-5','gap-5',1,1),('boundary-200','gesture-200',3,5),('boundary-201','gesture-201',3,1),
        ('direction-retained','direction',2,5),('reset-missing','auto-fixed-auto',3,5),('max-exceeded','gap-5',1,7),
        ('local-mac-accelerates','platform-darwin-local',1,5),('ssh-not-accelerated','platform-darwin-SSH_CLIENT',1,1),
        ('carry-lost','gap-30',3,3)]:
        mutate(name,lambda b,c=case,i=index,v=value:next(x for x in b['differential']['pisharp']['sequences'] if x['id']==c)['lines'].__setitem__(i,v))
    mutate('alt-not-five',lambda b:b['differential']['pisharp']['viewports'][4]['events'][2].update(multiplier=4))
    mutate('viewport-wrong',lambda b:b['differential']['pisharp']['viewports'][2]['events'][0].update(after=2))
    mutate('regular-affected',lambda b:next(x for x in b['differential']['pisharp']['viewports'] if x['id']=='regular-100')['events'][0].update(movement=100))
    mutate('overlay-bypasses-policy',lambda b:b['differential']['pisharp']['overlays'][1].update(deltas=[-1,-1]))
    mutate('calibration-differs',lambda b:b['differential']['calibration']['settings'][0].update(effective=1))
    mutate('wrong-pin',lambda b:b['differential'].update(piSha='wrong'))
    mutate('stale-implementation',lambda b:b['acceptance'].update(implementationSha=BASE))
    mutate('missing-test',lambda b:b['acceptance']['tests']['focused'].pop())
    mutate('failed-test',lambda b:b['acceptance']['tests']['focused'][0].update(outcome='Failed'))
    mutate('skipped-test',lambda b:b['acceptance']['tests']['focused'][0].update(outcome='NotExecuted'))
    mutate('full-suite-failure',lambda b:b['acceptance']['tests']['full'][0].update(outcome='Failed'))
    mutate('ledger-unverified',lambda b:next(x for x in b['ledger']['tasks'] if x['id']==CAPABILITY).update(status='not_started'))
    mutate('unrelated-fullscreen-promoted',lambda b:next(x for x in b['ledger']['tasks'] if x['id']=='settings-fullscreenscrollbar').update(status='verified'))
    mutate('duplicate-declaration-missing',lambda b:b['acceptance']['projections'].pop('duplicates'))
    mutate('stale-ci',lambda b:b['ci']['runs'][0].update(head=BASE))
    mutate('failed-ci',lambda b:b['ci']['runs'][0].update(conclusion='failure'))
    return controls


def load_bundle(ci_path=None):
    load = lambda name: json.loads((FIXTURES/('settings-fullscreenwheelscrolllines-'+name+'.json')).read_text())
    return dict(differential=load('differential'),baseline=load('baseline'),acceptance=load('acceptance'),pty=load('pty'),
                ledger=json.loads((ROOT/'docs/parity/execution-ledger.json').read_text()),
                ledgerBefore=json.loads(run('git','show',BASE+':docs/parity/execution-ledger.json')),
                ci=json.loads(pathlib.Path(ci_path).read_text()) if ci_path else None)


def collect_ci(number):
    pr=json.loads(run('gh','pr','view',str(number),'--json','state,isDraft,mergedAt,headRefOid,headRefName,body'))
    head=pr['headRefOid']
    runs=json.loads(run('gh','api',f'repos/PhilipGB/pisharp/actions/runs?head_sha={head}&per_page=100'))['workflow_runs']
    observed=[]
    for item in runs:
        if item['event'] not in ['push','pull_request']:
            continue
        jobs=json.loads(run('gh','api',f'repos/PhilipGB/pisharp/actions/runs/{item["id"]}/attempts/{item["run_attempt"]}/jobs'))['jobs']
        observed.append(dict(id=item['id'],event=item['event'],head=item['head_sha'],conclusion=item['conclusion'],
                             attempt=item['run_attempt'],jobs=[{k:x[k] for k in ['name','head_sha','conclusion']} for x in jobs]))
    require(head in pr['body'], 'PR description lacks exact final head')
    return dict(head=head,state=pr['state'],draft=pr['isDraft'],merged=pr['mergedAt'] is not None,runs=observed)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ci-json')
    parser.add_argument('--pr',type=int,help='Independently fetch current PR state and exact-head CI from GitHub')
    parser.add_argument('--mutations',action='store_true')
    args=parser.parse_args()
    bundle=load_bundle(args.ci_json)
    if args.pr:
        bundle['ci']=collect_ci(args.pr)
    verify(bundle,github=bundle['ci'] is not None)
    if args.mutations:
        require(bundle['ci'] is not None, 'mutation controls need actual exact-head CI evidence')
        controls=negative_controls(bundle)
        print(f'{len(controls)} mutation controls rejected: '+', '.join(controls))
    print('Fullscreen wheel acceptance verified'+(' including exact-head CI' if bundle['ci'] else ' (publication/CI gate pending)'))


if __name__ == '__main__':
    main()
