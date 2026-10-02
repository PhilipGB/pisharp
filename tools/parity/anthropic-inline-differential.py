#!/usr/bin/env python3
"""Compare real SDK request bodies with the pinned Pi Anthropic adapter."""
import argparse
import copy
import json
import os
import pathlib
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--pi', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
pi = pathlib.Path(args.pi).resolve()
repo = pathlib.Path(__file__).resolve().parents[2]
probes = pathlib.Path(__file__).resolve().parent

def tool(name, description=None):
    return dict(name=name, description=description or name + ' tool', parameters=dict(type='object', properties={}, required=[]))

base = dict(role='system', content='base prompt', sections=dict(rules='old rules', docs='read docs'), toolsAdded=[tool('base_tool')], timestamp=0)
user = dict(role='user', content=[dict(type='text', text='before')], timestamp=1)
update = dict(role='system', content='updated guidance', sections=dict(rules='new rules', docs=None), toolsRemoved=[dict(name='base_tool')], toolsAdded=[tool('late_tool')], timestamp=2)
usage = dict(input=0, output=0, cacheRead=0, cacheWrite=0, totalTokens=0, cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0, total=0))
call = dict(role='assistant', content=[dict(type='toolCall', id='call_1', name='base_tool', arguments={})], api='anthropic-messages', provider='anthropic', model='claude-opus-5', stopReason='toolUse', usage=usage, timestamp=2)
result = dict(role='toolResult', content=[dict(type='text', text='done')], toolCallId='call_1', toolName='base_tool', isError=False, timestamp=3)
assistant = dict(role='assistant', content=[dict(type='text', text='finished')], api='anthropic-messages', provider='anthropic', model='claude-opus-5', stopReason='stop', usage=usage, timestamp=4)
cases = [
    ('initial', [base, user], True, True),
    ('addition-removal', [base, user, update], True, True),
    ('redefinition', [base, user, dict(role='system', content='', toolsRemoved=[dict(name='base_tool')], toolsAdded=[tool('base_tool', 'changed')], timestamp=2)], True, True),
    ('fallback-no-system', [base, user, update], False, True),
    ('fallback-no-tool-changes', [base, user, update], True, False),
    ('fallback-both', [base, user, update], False, False),
    ('no-initial-tools', [dict(role='system', content='base prompt', timestamp=0), user, update], True, True),
    ('call-result-adjacency', [base, user, call, update, result, assistant], True, True),
    ('pending-updates', [base, user, call, update, result, dict(role='system', content='second update', timestamp=4), user, assistant], True, True),
    ('remove-all', [base, user, dict(role='system', content='', toolsRemoved=[dict(name='base_tool')], timestamp=2)], True, True),
]
scenarios = [dict(id=name + ('-stream' if streaming else '-response'), messages=messages, systemSupport=system, toolSupport=changes, streaming=streaming)
             for name, messages, system, changes in cases for streaming in (False, True)]
for name, messages in [
    ('oauth-addition-removal', [dict(role='system', content='base', toolsAdded=[tool('bash')], timestamp=0), user,
                               dict(role='system', content='', toolsRemoved=[dict(name='bash')], toolsAdded=[tool('read')], timestamp=2)]),
    ('oauth-redefinition', [dict(role='system', content='base', toolsAdded=[tool('bash')], timestamp=0), user,
                           dict(role='system', content='', toolsRemoved=[dict(name='bash')], toolsAdded=[tool('bash', 'changed')], timestamp=2)]),
    ('oauth-call-replay', [dict(role='system', content='base', toolsAdded=[tool('bash')], timestamp=0), user,
                          dict(call, content=[dict(type='toolCall', id='call_1', name='bash', arguments={})]), result, user])
]:
    scenarios += [dict(id=name + ('-stream' if streaming else '-response'), messages=messages, systemSupport=True, toolSupport=True, streaming=streaming, oauth=True) for streaming in (False, True)]

def normalize(rows):
    normalized = []
    for row in rows:
        payload = row['payload']
        selected = {key: copy.deepcopy(payload.get(key, [])) for key in ('system', 'tools', 'messages')}
        for message in selected['messages']:
            if isinstance(message['content'], str):
                message['content'] = [dict(type='text', text=message['content'])]
            for block in message['content']:
                if block['type'] == 'tool_result' and block.get('is_error') is None:
                    block['is_error'] = False
        normalized.append(dict(id=row['id'], betas=sorted(row['betas']), **selected))
    return normalized

with tempfile.TemporaryDirectory(prefix='pisharp-anthropic-differential-') as directory:
    work = pathlib.Path(directory)
    (work / 'Program.cs').write_text((probes / 'anthropic-inline-probe.cs').read_text())
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /><PackageReference Include="xunit" Version="2.9.3" /><Using Include="Xunit" />
<Compile Include="{repo}/tests/PiSharp.Tests/AnthropicInlineToolTests.cs" Link="AnthropicInlineToolTests.cs" /></ItemGroup></Project>''')
    environment = dict(os.environ)
    environment['NUGET_HTTP_CACHE_PATH'] = '/tmp/pisharp-nuget-http'
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-p:UseSharedCompilation=false', '-m:1', '--nologo'], env=environment, check=True, stdout=subprocess.DEVNULL, timeout=120)
    environment['PISHARP_COMPACTION_OUTPUT'] = str(work / 'pisharp-compaction.json')
    encoded = json.dumps(scenarios)
    pisharp_raw = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'), encoded], env=environment, timeout=60))
    oracle = pi / 'packages/ai/test/pisharp-anthropic-inline-differential.test.ts'
    if oracle.exists():
        raise RuntimeError(f'Refusing to overwrite {oracle}')
    shutil.copyfile(probes / 'anthropic-inline-probe.pi.test.ts', oracle)
    try:
        environment['PISHARP_ANTHROPIC_SCENARIOS'] = encoded
        environment['PISHARP_ANTHROPIC_OUTPUT'] = str(work / 'pi.json')
        environment['PISHARP_COMPACTION_OUTPUT'] = str(work / 'pi-compaction.json')
        subprocess.run(['node', str(pi / 'node_modules/vitest/vitest.mjs'), 'run', '--config', str(pi / 'packages/coding-agent/vitest.config.ts'), str(oracle)], cwd=pi / 'packages/ai', env=environment, check=True, timeout=60)
    finally:
        oracle.unlink()
    pi_raw = json.loads((work / 'pi.json').read_text())
    pi_results, pisharp_results = normalize(pi_raw), normalize(pisharp_raw)
    pi_compaction = json.loads((work / 'pi-compaction.json').read_text())
    pisharp_compaction = json.loads((work / 'pisharp-compaction.json').read_text())
    evidence = dict(piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=pi, text=True).strip(),
                    normalization='Compare full system/tools/messages and beta sets; select transcript-related body fields. Canonicalize a string content as one text block and omitted/null tool_result.is_error as false; preserve all text, ordering, schemas, cache controls and tool blocks.',
                    scenarios=scenarios, pi=pi_results, pisharp=pisharp_results,
                    compaction=dict(pi=pi_compaction, pisharp=pisharp_compaction),
                    match=pi_results == pisharp_results and pi_compaction == pisharp_compaction)
    pathlib.Path(args.output).write_text(json.dumps(evidence, indent=2) + '\n')
    assert evidence['match'], 'Anthropic transcript requests differ; inspect ' + args.output
    print(f'Matched {len(scenarios)} Anthropic transcript scenarios.')
