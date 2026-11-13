#!/usr/bin/env python3
"""Bounded, deterministic fullscreenWheelScrollLines corpus shared by both products."""
import json
import sys

PIN = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
BASE = '36774988c9e7088f0af2e4560d52cbbce80f9c47'
KEY = 'fullscreenWheelScrollLines'


def corpus():
    settings = []
    def setting(name, user='{}', project='{}', trusted=True):
        settings.append(dict(id=name, userRaw=user, projectRaw=project, projectTrusted=trusted))
    def raw(value):
        return '{"' + KEY + '":' + value + '}'
    setting('absent')
    for value in ['"auto"', '1', '2', '3', '4', '5', '7', '10', '100', '0', '-1', '-100',
                  '1.1', '1.9', '3.9', '7.9', '99.9', '100.1', '100.9', '101', '1000',
                  'null', '"3"', '"bogus"', 'true', 'false', '{}', '[]', '1e400']:
        setting('stored-' + value, raw(value))
    for name, value, trusted in [('absent', None, True), ('fixed', '3', True), ('auto', '"auto"', True),
                                 ('null', 'null', True), ('bogus', '"bogus"', True), ('untrusted', '3', False),
                                 ('clamp', '1000', True), ('fraction', '3.9', True),
                                 ('object', '{}', True), ('array', '[]', True)]:
        setting('project-' + name, raw('7'), '{}' if value is None else raw(value), trusted)
    for a, b in [(3, 7), (7, 3)]:
        setting(f'duplicate-{a}-{b}', '{"' + KEY + f'":{a},"' + KEY + f'":{b}' + '}')
    sequences = []
    def sequence(name, value, times, directions=None, alts=None, sets=None, accelerate=True):
        sequences.append(dict(id=name, setting=value, accelerate=accelerate, events=[dict(
            t=t, direction=(directions or [-1]*len(times))[i], alt=i in (alts or []),
            **({'set': sets[i]} if sets and i in sets else {})) for i, t in enumerate(times)]))
    for value in [1, 2, 3, 5, 7, 100]:
        sequence('fixed-' + str(value), value, [0, 20, 40], alts=[2])
    for gap in [4, 5, 20, 30, 50, 60, 100, 200, 201]:
        sequence('gap-' + str(gap), 'auto', [0, gap, gap*2, gap*3, gap*4])
    sequence('direction', 'auto', [0, 20, 21, 41], [-1, -1, 1, 1])
    sequence('gesture-200', 'auto', [0, 20, 220, 240])
    sequence('gesture-201', 'auto', [0, 20, 221, 241])
    sequence('auto-alt', 'auto', [0, 20, 40], alts=[1])
    for name, changes in [('auto-fixed-auto', {2: 5, 3: 'auto'}), ('auto-auto', {2: 'auto'}),
                          ('fixed-fixed-auto', {1: 5, 2: 'auto'})]:
        sequence(name, 5 if name.startswith('fixed') else 'auto', [0, 20, 40, 60], sets=changes)
    for platform, ssh in [('darwin', None), ('darwin', 'SSH_CONNECTION'), ('darwin', 'SSH_CLIENT'),
                          ('darwin', 'SSH_TTY'), ('linux', None)]:
        name = 'platform-' + platform + '-' + (ssh or 'local')
        sequence(name, 'auto', [0, 20, 40], accelerate=not(platform == 'darwin' and ssh is None))
        sequences[-1].update(platform=platform, ssh=ssh)
    viewports = []
    for value in [1, 2, 3, 5, 7, 100, 'auto']:
        viewports.append(dict(id='viewport-' + str(value), setting=value, mode='fullscreen', start='bottom',
                              events=[dict(t=t, direction=-1, alt=i == 2) for i,t in enumerate([0,20,40])]))
    for boundary, direction in [('top', -1), ('bottom', 1)]:
        viewports.append(dict(id='boundary-' + boundary, setting=7, mode='fullscreen', start=boundary,
                              events=[dict(t=0, direction=direction, alt=False)]))
        viewports.append(dict(id='near-' + boundary, setting=7, mode='fullscreen', start='near-' + boundary,
                              events=[dict(t=0, direction=direction, alt=False)]))
    viewports.append(dict(id='regular-100', setting=100, mode='regular', start='bottom',
                          events=[dict(t=0, direction=-1, alt=False)]))
    viewports.append(dict(id='live-1-5-auto', setting=1, mode='fullscreen', start='bottom', events=[
        dict(t=0,direction=-1,alt=False), dict(t=20,direction=-1,alt=False,set=5),
        dict(t=40,direction=-1,alt=False,set='auto'), dict(t=60,direction=-1,alt=False),
        dict(t=80,direction=-1,alt=False,set='auto')]))
    return dict(piSha=PIN, baseSha=BASE, settings=settings, sequences=sequences, viewports=viewports,
                setters=['auto',1,7,0,1.9,101], overlays=[dict(id='overlay-' + str(x) + ('-alt' if a else ''), setting=x, alt=a)
                    for x,a in [(1,False),(7,False),(7,True),('auto',False)]])


if __name__ == '__main__':
    with open(sys.argv[1], 'w') as f:
        json.dump(corpus(), f, indent=2)
        f.write('\n')
