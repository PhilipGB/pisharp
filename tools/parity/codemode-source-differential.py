#!/usr/bin/env python3
"""Compare Codemode directive placement against the configured real Pi parser."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
sources = [
    '// count\n// @options: {"timeout_ms":1000}\ntext("executed")',
    '\n  // @options: {"timeout_ms":1000}\nwhile (true) {}',
    'text(1)\r\n// @options: {"timeout_ms":1}',
    '// @options: {"timeout_ms":1000}\ntext(1)\n// @options: {"timeout_ms":2}',
    '// @optionsx {}\ntext(1)',
    'text("// @options: {}")',
    '\t// @options: {}\r\ntext(1)',
    '// comment\rtext(1)',
]
with tempfile.TemporaryDirectory(prefix='pisharp-codemode-source-differential-') as directory:
    work = Path(directory)
    input_path = work / 'sources.json'
    input_path.write_text(json.dumps(sources))
    (work / 'Program.cs').write_text('''
using System.Text.Json;
using PiSharp.Runtime.Codemode;
var sources = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[0]))!;
Console.WriteLine(JsonSerializer.Serialize(sources.Select(source =>
{
    try { return new { code = (string?)CodemodeSource.Parse(source).Code, error = (string?)null }; }
    catch (ArgumentException error) { return new { code = (string?)null, error = error.Message }; }
})));
''')
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '-m:1', '-p:UseSharedCompilation=false'],
                   env=env, check=True, stdout=subprocess.DEVNULL, timeout=120)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll'),
                                                str(input_path)], env=env, timeout=20))
    oracle = work / 'oracle.mjs'
    oracle.write_text('''import { readFileSync } from 'node:fs';
import { parseCodemodeSource } from SOURCE;
const sources = JSON.parse(readFileSync(process.argv[2], 'utf8'));
console.log(JSON.stringify(sources.map(source => {
  try { return { code: parseCodemodeSource(source).code, error: null }; }
  catch (error) { return { code: null, error: error.message }; }
})));
'''.replace('SOURCE', json.dumps((args.pi.resolve() / 'packages/codemode/src/source.ts').as_uri())))
    expected = json.loads(subprocess.check_output(['node', str(oracle), str(input_path)], env=env, timeout=20))
    assert actual == expected, json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, sources=sources, pi=expected, pisharp=actual,
        match=True, comparison='Exact rejection messages and preserved source text for misplaced directives and valid controls. Other options validation/default/deadline semantics are outside this probe.'), indent=2) + '\n')
    print(f'Matched {len(sources)} Codemode placement/source cases.')
