#!/usr/bin/env python3
"""Check semantic settings-array selection across a paired interactive /reload."""
import gzip
import json
from pathlib import Path
import sys

if len(sys.argv) != 2:
    raise SystemExit('usage: assert_resource_glob_settings_reload.py <assertions.json.gz>')

evidence = json.load(gzip.open(Path(sys.argv[1]), 'rt', encoding='utf-8'))
fixture_path = Path(__file__).with_name('fixtures') / 'resource-glob-settings-reload.json'
fixture = json.loads(fixture_path.read_text())
before = fixture['files']['pi']['settings.json']
after = next(action for action in fixture['actions'] if action['id'] == 'replace-settings-globs')['writeFiles']['agent']['settings.json']
for resource in ('skills', 'prompts', 'themes', 'extensions'):
    assert resource in before and resource in after, resource
    assert before[resource] != after[resource], f'{resource} settings did not change'
    assert any(pattern.startswith('!') for pattern in before[resource]), resource
    assert any(pattern.startswith('!') for pattern in after[resource]), resource

expected = [
    '<skill name="initial-guide"',
    'Initial prompt arg',
    '<skill name="reloaded-guide"',
    'Reloaded prompt arg',
]
product_inputs = {}
for product in ('pi', 'pisharp'):
    result = evidence[product]
    assert result['scenarioError'] is None, (product, result['scenarioError'])
    assert result['capturedAgentFiles'].get('extension-load.log') is not None, product
    trace = result['capturedAgentFiles']['extension-load.log'].splitlines()
    if product == 'pi':
        assert trace == ['configured:initial-extensions', 'configured:reloaded-extensions'], trace
    else:
        assert trace == ['configured:user:custom-extensions', 'configured:user:reloaded-extensions'], trace
    assert not any('blocked' in line for line in trace), trace
    inputs = []
    for request in result['http']:
        messages = request['body']['messages']
        user = next(message for message in reversed(messages) if message['role'] == 'user')['content']
        if isinstance(user, list):
            user = ''.join(part.get('text', '') for part in user if part.get('type') == 'text')
        inputs.append(user)
    assert len(inputs) == len(expected), (product, inputs)
    for actual, prefix in zip(inputs, expected, strict=True):
        assert actual.startswith(prefix), (product, actual, prefix)
        assert 'Blocked' not in actual and 'blocked-' not in actual, (product, actual)
    product_inputs[product] = inputs

assert product_inputs['pi'] == product_inputs['pisharp'], 'Pi/PiSharp provider input and order differ'
print('Passed: user settings changed and /reload selected the new skills, prompts, themes and extensions; exclusions remained inactive.')
