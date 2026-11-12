#!/usr/bin/env python3
"""Generate the bounded raw-settings/history corpus shared by both executable probes."""
import json
import pathlib


def corpus():
    cases = []

    def add(id, raw=None, context=100, user=None, project=None, trusted=True, **extra):
        cases.append(dict(id=id, userRaw=user if user is not None else
                          ('{}' if raw is None else '{"branchSummary":{"reserveTokens":' + raw + '}}'),
                          projectRaw=project or '{}', projectTrusted=trusted,
                          contextWindow=context, maxTokens=8192, history='messages', **extra))

    add('BR01-unset')
    for name, raw, context in [
        ('zero', '0', 100), ('one', '1', 31), ('normal', '4096', 4116),
        ('default', '16384', 16404), ('negative', '-1', 29), ('fraction', '2.5', 32),
        ('fraction-boundary', '2', 32), ('equal', '100', 100), ('greater', '101', 100),
        ('large', '1e100', 100), ('null', 'null', 16404),
        ('numeric-string', '"1000"', 1020), ('empty-string', '""', 20),
        ('bad-string', '"bad"', 100), ('true', 'true', 21), ('false', 'false', 20),
        ('object', '{}', 100), ('array', '[]', 20), ('single-array', '[1000]', 1020),
        ('multi-array', '[1,2]', 100), ('nested-array', '[[1000]]', 1020),
        ('null-array', '[null]', 20), ('hex-string', '"0x10"', 36),
        ('binary-string', '"0b10000"', 36), ('octal-string', '"0o20"', 36),
        ('infinity-string', '"Infinity"', 100), ('negative-zero', '-0', 20),
        ('wide-number', '2147483648', 100), ('tiny-budget', '99', 100),
        ('threshold', '90', 100), ('below-threshold', '90.5', 100),
        ('second-context', '180', 200), ('context-fallback', '127980', 0),
    ]:
        add('BR-' + name, raw, context)
    for first, last in [(100, 20), (20, 100)]:
        add(f'BR-duplicate-{first}-{last}', context=40,
            user='{"branchSummary":{"reserveTokens":%s,"reserveTokens":%s}}' % (first, last))
    for name, parent in [('null', 'null'), ('string', '"bad"'), ('number', '42'), ('array', '[]')]:
        add('BR-parent-' + name, context=16404, user='{"branchSummary":' + parent + '}')
    user = '{"branchSummary":{"reserveTokens":4096}}'
    for name, project, trusted in [
        ('BR04-skip-only', '{"skipPrompt":false}', True),
        ('BR05-override', '{"reserveTokens":1024}', True),
        ('BR06-null', '{"reserveTokens":null}', True),
        ('BR07-untrusted', '{"reserveTokens":1024}', False),
        ('BR08-numeric', '{"reserveTokens":"1000"}', True),
        ('BR08-bad', '{"reserveTokens":"bad"}', True),
        ('BR08-zero', '{"reserveTokens":0}', True),
        ('BR09-sibling', '{"futureSetting":42}', True),
    ]:
        add(name, context=4116, user=user, project='{"branchSummary":' + project + '}', trusted=trusted)
    add('BR-unknown-sibling', context=4116,
        user='{"branchSummary":{"reserveTokens":4096,"futureSetting":42}}')
    add('BR-skip-true', context=100, user='{"branchSummary":{"reserveTokens":"bad","skipPrompt":true}}')
    add('BR-custom', '80', customInstructions='CUSTOM BRANCH FOCUS')
    for kind in ['summary', 'compaction', 'files']:
        add('BR-cutoff-' + kind, '80')
        cases[-1]['history'] = kind
    return {'schema': 1, 'cases': cases}


if __name__ == '__main__':
    path = pathlib.Path(__file__).with_name('branch_summary_reserve_cases.json')
    path.write_text(json.dumps(corpus(), indent=2) + '\n')
