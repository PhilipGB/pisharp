#!/usr/bin/env python3
"""Compare silent provider completion after cancellation against Pi's real OpenAI stream."""
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
with tempfile.TemporaryDirectory(prefix='pisharp-provider-silent-abort-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text('''
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
var results = new List<object>();
foreach (var streaming in new[] { false, true })
{
    using var cancellation = new CancellationTokenSource();
    using var inner = new SilentAbortClient(cancellation);
    using var client = new ProviderRequestTimeoutChatClient(inner, 2000, 2000);
    var canceled = false;
    var completed = false;
    try
    {
        if (streaming) await foreach (var _ in client.GetStreamingResponseAsync([], cancellationToken: cancellation.Token)) { }
        else _ = await client.GetResponseAsync([], cancellationToken: cancellation.Token);
        completed = true;
    }
    catch (OperationCanceledException error) { canceled = error.CancellationToken == cancellation.Token; }
    results.Add(new { streaming, requests = inner.Requests, canceled, completed });
}
Console.WriteLine(JsonSerializer.Serialize(results));
sealed class SilentAbortClient(CancellationTokenSource cancellation) : IChatClient
{
    public int Requests { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests++;
        cancellation.Cancel();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "")));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests++;
        await Task.Yield();
        cancellation.Cancel();
        yield break;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
''')
    (work / 'probe.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    env = dict(os.environ, NUGET_HTTP_CACHE_PATH='/tmp/pisharp-nuget-http')
    build = subprocess.run(['dotnet', 'build', str(work / 'probe.csproj'), '--warnaserror', '-m:1', '-p:UseSharedCompilation=false'],
                           env=env, capture_output=True, text=True, timeout=120)
    if build.returncode:
        raise RuntimeError(build.stdout + build.stderr)
    actual = json.loads(subprocess.check_output(['dotnet', str(work / 'bin/Debug/net10.0/PiSharp.Tests.dll')], env=env, timeout=20))
    oracle = work / 'oracle.mjs'
    oracle.write_text('''import { stream } from SOURCE;
const model = { id: 'fixture', name: 'fixture', api: 'openai-completions', provider: 'fixture', baseUrl: 'http://fixture.invalid/v1', reasoning: false, input: ['text'], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 4096, maxTokens: 128 };
const results = [];
for (const streaming of [false, true]) {
 const controller = new AbortController(); let requests = 0;
 const events = stream(model, { messages: [{ role: 'user', content: 'hello', timestamp: 1 }] }, {
  apiKey: 'fixture', signal: controller.signal, maxRetries: 0,
  fetch: async () => { requests++; return new Response('data: [DONE]\\n\\n', { headers: { 'Content-Type': 'text/event-stream' } }); },
  onResponse: () => controller.abort(),
 });
 const trace = []; for await (const event of events) trace.push(event.type);
 const result = await events.result();
 results.push({ streaming, requests, canceled: result.stopReason === 'aborted', completed: trace.includes('done') });
}
console.log(JSON.stringify(results));
'''.replace('SOURCE', json.dumps((args.pi.resolve() / 'packages/ai/src/api/openai-completions.ts').as_uri())))
    expected = json.loads(subprocess.check_output(['node', str(oracle)], env=env, timeout=20))
    assert actual == expected and all(item['canceled'] for item in actual), json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, pi=expected, pisharp=actual, match=True,
        comparison='Semantic caller cancellation after silent inner completion: one provider request, cancellation reported and no successful completion. Pi uses its real OpenAI SDK stream for both native stream consumption and final-result consumption; .NET exercises both IChatClient call modes.',
        limitations='Pi fetch returns an empty SSE stream and onResponse aborts the caller before completion. The .NET native client cancels the caller and returns silently. Request/idle deadline guards are separately covered by deterministic native tests and a real saved-settings CLI loopback; this comparison does not establish timeout error text, all SDK adapters, or bounded settlement for an indefinitely uncooperative inner client.'), indent=2) + '\n')
    print('Matched two silent provider completion/cancellation cases.')
