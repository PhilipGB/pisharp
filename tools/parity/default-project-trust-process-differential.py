#!/usr/bin/env python3
"""Compare current Pi and PiSharp CLI trust-setting precedence in separate processes."""
import argparse
import json
import os
import pathlib
import re
import subprocess
import tempfile
from terminal.fixture_http import FixtureServer

from pi_reference import verify_pi_checkout

from terminal.thread_limits import apply_test_thread_limit
apply_test_thread_limit()

parser = argparse.ArgumentParser()
parser.add_argument('--pi', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
verify_pi_checkout(args.pi)
pi = pathlib.Path(args.pi).resolve()
repo = pathlib.Path(__file__).resolve().parents[2]
pi_cli = pi / 'packages/coding-agent/dist/cli.js'
pisharp_cli = repo / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll'
scenarios = [
    dict(id='malformed-global-string-falls-back-to-ask', user={'defaultProjectTrust': 'sometimes'}),
    dict(id='malformed-global-type-falls-back-to-ask', user={'defaultProjectTrust': True}),
    dict(id='project-always-does-not-override-global-never', user={'defaultProjectTrust': 'never'},
         project={'defaultProjectTrust': 'always', 'theme': 'light'}),
    dict(id='project-never-does-not-override-global-always', user={'defaultProjectTrust': 'always'},
         project={'defaultProjectTrust': 'never', 'theme': 'light'}),
    dict(id='malformed-project-setting-does-not-fail-trusted-project', user={'defaultProjectTrust': 'always'},
         project={'defaultProjectTrust': True, 'theme': 'light'}),
    dict(id='saved-deny-precedes-global-always', user={'defaultProjectTrust': 'always'},
         project={'defaultProjectTrust': 'never', 'theme': 'light'}, saved=False),
    dict(id='saved-allow-precedes-global-never', user={'defaultProjectTrust': 'never'},
         project={'defaultProjectTrust': 'never', 'theme': 'light'}, saved=True),
    dict(id='approve-precedes-saved-deny', user={'defaultProjectTrust': 'never'},
         project={'defaultProjectTrust': 'never', 'theme': 'light'}, saved=False, override='--approve'),
    dict(id='no-approve-precedes-saved-allow', user={'defaultProjectTrust': 'always'},
         project={'defaultProjectTrust': 'never', 'theme': 'light'}, saved=True, override='--no-approve'),
]
base_args = ['--provider', 'fixture', '--model', 'fixture-model', '--offline', '--no-session', '--no-tools',
             '--no-extensions', '--print', '/trust-probe']


def classify(return_code, stdout, stderr):
    message = (stdout + '\n' + stderr).lower()
    if 'defaultprojecttrust' in message:
        return 'trust-setting-rejected'
    if re.search(r'api key|not authenticated|missing credentials|authentication required', message):
        return 'provider-auth-required'
    if return_code == 0:
        return 'completed'
    return 'other-error'


def run(command, cwd, environment):
    result = subprocess.run(command, cwd=cwd, env=environment, text=True, capture_output=True, timeout=20)
    return dict(outcome=classify(result.returncode, result.stdout, result.stderr), exitCode=result.returncode)


results = []
server = FixtureServer()
with tempfile.TemporaryDirectory(prefix='pisharp-default-project-trust-') as directory:
    root = pathlib.Path(directory)
    for scenario in scenarios:
        cwd = root / scenario['id'] / 'project'
        pi_agent = root / scenario['id'] / 'pi-agent'
        pisharp_agent = root / scenario['id'] / 'pisharp-agent'
        (cwd / '.pi').mkdir(parents=True)
        pi_agent.mkdir(parents=True)
        pisharp_agent.mkdir(parents=True)
        (pi_agent / 'settings.json').write_text(json.dumps(scenario['user']))
        (pisharp_agent / 'settings.json').write_text(json.dumps(scenario['user']))
        (cwd / '.pi' / 'settings.json').write_text(json.dumps(scenario.get('project', {})))
        (cwd / '.pi' / 'prompts').mkdir()
        (cwd / '.pi' / 'prompts' / 'trust-probe.md').write_text('TRUSTED_PROJECT_RESOURCE_MARKER')
        provider = dict(baseUrl=server.url + '/v1', api='openai-completions', models=[dict(
            id='fixture-model', name='Fixture', api='openai-completions', contextWindow=128000, maxTokens=128,
            reasoning=False, input=['text'], cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))])
        (pi_agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=dict(provider, apiKey='fixture')))))
        (pisharp_agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=dict(provider, apiKeyEnv='PISHARP_FIXTURE_KEY')))))
        if 'saved' in scenario:
            for agent in (pi_agent, pisharp_agent):
                (agent / 'trust.json').write_text(json.dumps({str(cwd): scenario['saved']}))

        environment = dict(os.environ)
        for name in list(environment):
            if name.endswith('_API_KEY') or name.endswith('_TOKEN'):
                environment.pop(name, None)
        for name in ('PI_CODING_AGENT_DIR', 'PI_OFFLINE', 'PISHARP_AGENT_DIR', 'PISHARP_OFFLINE', 'PISHARP_SETTINGS_PATH'):
            environment.pop(name, None)
        pi_environment = dict(environment, PI_CODING_AGENT_DIR=str(pi_agent), PI_OFFLINE='1')
        pisharp_environment = dict(environment, PISHARP_AGENT_DIR=str(pisharp_agent), PISHARP_OFFLINE='1', PISHARP_FIXTURE_KEY='fixture')
        extra = [scenario['override']] if 'override' in scenario else []
        def run_with_request(command, env):
            start = len(server.requests)
            result = run(command, cwd, env)
            requests = [request for request in server.requests[start:] if request['method'] == 'POST'
                        and request['path'].endswith('/chat/completions')]
            assert len(requests) == 1 and result['exitCode'] == 0, (scenario, result, requests)
            result['projectResourceSelected'] = 'TRUSTED_PROJECT_RESOURCE_MARKER' in json.dumps(requests[0]['body'])
            return result
        pi_result = run_with_request(['node', str(pi_cli), *extra, *base_args], pi_environment)
        pisharp_result = run_with_request(['dotnet', str(pisharp_cli), *extra, *base_args], pisharp_environment)
        expected_trust = {'project-never-does-not-override-global-always', 'malformed-project-setting-does-not-fail-trusted-project',
                          'saved-allow-precedes-global-never', 'approve-precedes-saved-deny'}
        assert pi_result['projectResourceSelected'] == (scenario['id'] in expected_trust), (scenario, pi_result)
        results.append(dict(id=scenario['id'], pi=pi_result, pisharp=pisharp_result,
                            match=pi_result == pisharp_result))
server.close()

evidence = dict(
    piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
    pisharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
    normalization='Launch both CLI applications with isolated agent directories and a deterministic loopback provider. Require exactly one successful provider request and compare whether a project prompt template expands into that request. Explicit expected trust outcomes prevent two equally wrong clients passing. Exit codes must both be zero. TUI selector rendering remains outside this setting contract.',
    scenarios=scenarios, results=results, match=all(result['match'] for result in results))
pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
for result in results:
    print(f"{result['id']}: Pi {result['pi']['outcome']} ({result['pi']['exitCode']}), PiSharp {result['pisharp']['outcome']} ({result['pisharp']['exitCode']})")
if not evidence['match']:
    raise SystemExit(1)
