#!/usr/bin/env python3
"""Compare cancellation racing a provider rejection against Pi's request retry boundary."""
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
with tempfile.TemporaryDirectory(prefix='pisharp-provider-cancellation-') as directory:
    work = Path(directory)
    (work / 'Program.cs').write_text('''
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
var results = new List<object>();
foreach (var streaming in new[] { false, true })
foreach (var maxRetries in new[] { 0, 1 })
{
    using var cancellation = new CancellationTokenSource();
    using var rejected = new RejectingClient(cancellation);
    using var client = new ProviderRetryChatClient(rejected, maxRetries, 0);
    var canceled = false;
    try
    {
        if (streaming) await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hello")], cancellationToken: cancellation.Token)) { }
        else _ = await client.GetResponseAsync([new(ChatRole.User, "hello")], cancellationToken: cancellation.Token);
    }
    catch (OperationCanceledException error) { canceled = error.CancellationToken == cancellation.Token; }
    results.Add(new { streaming, maxRetries, requests = rejected.Requests, canceled });
}
Console.WriteLine(JsonSerializer.Serialize(results));
sealed class RejectingClient(CancellationTokenSource cancellation) : IChatClient
{
    public int Requests { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests++;
        cancellation.Cancel();
        return Task.FromException<ChatResponse>(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests++;
        await Task.Yield();
        cancellation.Cancel();
        if (Requests > 0) throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
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
    oracle.write_text('''import { retryProviderRequest } from SOURCE;
const results = [];
for (const streaming of [false, true]) for (const maxRetries of [0, 1]) {
  const controller = new AbortController();
  let requests = 0;
  let canceled = false;
  try {
    await retryProviderRequest(async () => {
      requests++;
      controller.abort();
      throw Object.assign(new Error('rate limited'), { status: 429, headers: new Headers() });
    }, { maxRetries, signal: controller.signal });
  } catch (error) { canceled = error.name === 'AbortError' && controller.signal.aborted; }
  results.push({ streaming, maxRetries, requests, canceled });
}
console.log(JSON.stringify(results));
'''.replace('SOURCE', json.dumps((args.pi.resolve() / 'packages/ai/src/utils/provider-retry.ts').as_uri())))
    expected = json.loads(subprocess.check_output(['node', str(oracle)], env=env, timeout=20))
    assert actual == expected and all(item['canceled'] for item in actual), json.dumps(dict(pi=expected, pisharp=actual), indent=2)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(piSha=pi_sha, pi=expected, pisharp=actual, match=True,
        comparison='Semantic cancellation at the actual request-retry boundary, with one request and no replay, for native streaming/nonstreaming calls and enabled/disabled retries. Pi checks AbortError and its aborted signal; .NET checks OperationCanceledException carrying the exact caller token.',
        limitations='The injected native client rejects synchronously after canceling the caller; Pi request() does the same. This isolates retry-boundary ordering. Existing HTTP retry-delay cancellation controls cover real SDK/loopback calls. Later stream failures, all event envelopes and provider error formatting remain separately ledgered.'), indent=2) + '\n')
    print('Matched four provider rejection/cancellation retry-boundary cases.')
