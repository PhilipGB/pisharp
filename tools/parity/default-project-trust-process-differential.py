#!/usr/bin/env python3
"""Compare current Pi and PiSharp CLI trust-setting precedence in separate processes."""
import argparse
import json
import os
import pathlib
import re
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--pi', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
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
base_args = ['--provider', 'openai', '--model', 'gpt-4o-mini', '--offline', '--no-session', '--no-tools', '--print', 'trust probe']


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
        pisharp_environment = dict(environment, PISHARP_AGENT_DIR=str(pisharp_agent), PISHARP_OFFLINE='1')
        extra = [scenario['override']] if 'override' in scenario else []
        pi_result = run(['node', str(pi_cli), *extra, *base_args], cwd, pi_environment)
        pisharp_result = run(['dotnet', str(pisharp_cli), *extra, *base_args], cwd, pisharp_environment)
        results.append(dict(id=scenario['id'], pi=pi_result, pisharp=pisharp_result,
                            match=pi_result['outcome'] == pisharp_result['outcome']))

evidence = dict(
    piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
    pisharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip(),
    normalization='Launch both CLI applications with isolated agent directories, an offline unauthenticated OpenAI request, a protected project settings file and identical trust/default inputs. Compare whether each process reaches provider authentication or rejects trust settings; exit codes and product-specific diagnostics are recorded separately.',
    scenarios=scenarios, results=results, match=all(result['match'] for result in results))
pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
for result in results:
    print(f"{result['id']}: Pi {result['pi']['outcome']} ({result['pi']['exitCode']}), PiSharp {result['pisharp']['outcome']} ({result['pisharp']['exitCode']})")
if not evidence['match']:
    raise SystemExit(1)
