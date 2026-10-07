#!/usr/bin/env python3
"""Compare llama.cpp context-window precedence and persisted reload with current Pi."""

import argparse
import http.server
import json
import os
import pathlib
import subprocess
import tempfile
import threading


parser = argparse.ArgumentParser()
parser.add_argument("--pi", required=True, help="Current Pi checkout")
parser.add_argument("--output", required=True)
args = parser.parse_args()

repo = pathlib.Path(__file__).resolve().parents[2]
pi_repo = pathlib.Path(args.pi).resolve()
work = pathlib.Path(tempfile.mkdtemp(prefix="pisharp-llama-router-context-"))
agent_dir = work / "agent"
agent_dir.mkdir()
pi_cache = work / "pi-models.json"

csharp = r'''using System.Text.Json;
using PiSharp.Cli;

var root = args[1];
Directory.CreateDirectory(root);
var environment = new Dictionary<string, string?>
{
    ["LLAMA_BASE_URL"] = args[0],
    ["LLAMA_API_KEY"] = "local"
};
using var http = new HttpClient();
var providers = await ProviderModelRuntime.CreateAsync(root, false,
    name => environment.GetValueOrDefault(name), http, offline: args[2] == "offline");
var chat = await providers.ListModelsAsync("llama.cpp");
var classifiers = providers.GetProvider("llama.cpp").Classifiers ?? [];
var output = new
{
    chatModels = chat.Select(model => new
    {
        id = model.Id,
        contextWindow = model.ContextLength,
        maxTokens = model.MaxOutputTokens
    }),
    classifiers = classifiers.Select(model => new
    {
        id = model.Id,
        api = model.Api,
        contextWindow = model.ContextWindow
    })
};
Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
'''
(work / "Program.cs").write_text(csharp)
(work / "probe.csproj").write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
    '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
    '</PropertyGroup><ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" />'
    '</ItemGroup></Project>\n'.replace("{repo}", str(repo))
)
(work / "pi.mjs").write_text(
    "import fs from 'node:fs';\n"
    "import { createLlamaProvider } from '{pi}/packages/coding-agent/src/extensions/llama/provider.ts';\n"
    "const [url,cachePath,phase]=process.argv.slice(2);\n"
    "const stored=fs.existsSync(cachePath)?JSON.parse(fs.readFileSync(cachePath,'utf8')):undefined;\n"
    "const controller=createLlamaProvider();\n"
    "await controller.provider.refreshModels?.({credential:{type:'api_key',key:'local',env:{LLAMA_BASE_URL:url}},"
    "stored,publish:async publication=>{if(publication.persist!==undefined)fs.writeFileSync(cachePath,"
    "JSON.stringify(publication.persist));publication.update?.();return true;},allowNetwork:phase!=='offline',"
    "signal:new AbortController().signal});\n"
    "const chat=controller.provider.getModels().map(model=>({id:model.id,contextWindow:model.contextWindow,"
    "maxTokens:model.maxTokens}));\n"
    "const classifiers=(controller.provider.getAllModels?.()??[]).filter(model=>model.type==='classifier')"
    ".map(model=>({id:model.id,api:model.api,contextWindow:model.contextWindow}));\n"
    "console.log(JSON.stringify({chatModels:chat,classifiers}));\n"
    .replace("{pi}", pi_repo.as_posix())
)

environment = dict(os.environ)
environment.setdefault("NUGET_HTTP_CACHE_PATH", "/tmp/pisharp-nuget-http")
for command in (
    ["npm", "run", "build", "--prefix", str(pi_repo / "packages/telemetry")],
    ["npm", "run", "build:offline", "--prefix", str(pi_repo / "packages/ai")],
):
    subprocess.run(command, cwd=pi_repo, env=environment, check=True, stdout=subprocess.DEVNULL)
subprocess.run(
    ["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false", "-m:1"],
    env=environment,
    check=True,
    stdout=subprocess.DEVNULL,
)

stage = 0
trace = []


def model_catalog(current_stage):
    if current_stage == 0:
        rows = [
            {"id": "runtime", "status": {"value": "loaded", "args": ["llama-server", "--ctx-size", "8192"]},
             "source": "local", "architecture": {"output_modalities": ["text"]},
             "meta": {"n_ctx": 32768, "n_ctx_train": 65536}},
            {"id": "configured", "status": {"value": "unloaded", "args": ["llama-server", "-c", "4096"]},
             "source": "preset", "architecture": {"output_modalities": ["text"]},
             "meta": {"n_ctx_train": 32768}},
            {"id": "training", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["text"]}, "meta": {"n_ctx_train": 16384}},
            {"id": "fallback", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["text"]}},
            {"id": "decision", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["decisions"]}, "meta": {"n_ctx": 8192}},
        ]
    else:
        rows = [
            {"id": "runtime", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["text"]}, "meta": {"n_ctx_train": 65536}},
            {"id": "configured", "status": {"value": "unloaded", "args": ["llama-server", "--ctx-size", "2048"]},
             "source": "preset", "architecture": {"output_modalities": ["text"]},
             "meta": {"n_ctx_train": 32768}},
            {"id": "training", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["text"]}, "meta": {"n_ctx_train": 16384}},
            {"id": "fallback", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["text"]}},
            {"id": "decision", "status": {"value": "sleeping"},
             "architecture": {"output_modalities": ["decisions"]}, "meta": {"n_ctx_train": 16384}},
        ]
    return {"data": rows}


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def _send(self, value, status=200):
        body = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _capture(self):
        trace.append({"method": self.command, "path": self.path,
                      "authorization": self.headers.get("Authorization")})

    def do_GET(self):
        self._capture()
        if self.path == "/models":
            return self._send(model_catalog(stage))
        if self.path == "/props":
            return self._send({"models_autoload": True})
        if self.path == "/props?model=runtime&autoload=false":
            return self._send({"chat_template": "fixture"})
        return self._send({"error": "not found"}, 404)


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{server.server_port}"


def run_pi(phase):
    return json.loads(subprocess.check_output(
        ["node", str(work / "pi.mjs"), url, str(pi_cache), phase], cwd=pi_repo))


def run_pisharp(phase):
    return json.loads(subprocess.check_output(
        ["dotnet", str(work / "bin/Debug/net10.0/probe.dll"), url, str(agent_dir), phase],
        cwd=repo, env=environment))


def compare_phase(name, pi_result, pisharp_result):
    if pi_result != pisharp_result:
        raise AssertionError(f"{name} context projections differ:\nPi: {pi_result}\nPiSharp: {pisharp_result}")
    return pi_result


expected_context = {
    "initial": {"runtime": 32768, "configured": 4096, "training": 16384, "fallback": 128000, "decision": 8192},
    "refresh": {"runtime": 32768, "configured": 2048, "training": 16384, "fallback": 128000, "decision": 8192},
}


def context_index(snapshot):
    return {
        **{model["id"]: model["contextWindow"] for model in snapshot["chatModels"]},
        **{model["id"]: model["contextWindow"] for model in snapshot["classifiers"]},
    }


snapshots = {}
traces = {}
for current_stage, phase in ((0, "initial"), (1, "refresh")):
    stage = current_stage
    trace.clear()
    pi_result = run_pi(phase)
    pi_trace = list(trace)
    trace.clear()
    pisharp_result = run_pisharp(phase)
    pisharp_trace = list(trace)
    if pi_trace != pisharp_trace:
        raise AssertionError(f"{phase} router HTTP traces differ:\nPi: {pi_trace}\nPiSharp: {pisharp_trace}")
    snapshots[phase] = compare_phase(phase, pi_result, pisharp_result)
    actual = context_index(snapshots[phase])
    if actual != expected_context[phase]:
        raise AssertionError(f"{phase} context precedence differs: expected {expected_context[phase]}, got {actual}")
    traces[phase] = pi_trace

trace.clear()
offline_pi = run_pi("offline")
pi_offline_trace = list(trace)
trace.clear()
offline_pisharp = run_pisharp("offline")
pisharp_offline_trace = list(trace)
if pi_offline_trace or pisharp_offline_trace:
    raise AssertionError("Offline reload unexpectedly contacted the router")
snapshots["offlineReload"] = compare_phase("offline reload", offline_pi, offline_pisharp)
if context_index(snapshots["offlineReload"]) != expected_context["refresh"]:
    raise AssertionError("Offline reload did not preserve the refreshed context windows")
server.shutdown()

report = {
    "match": True,
    "piSha": subprocess.check_output(["git", "-C", str(pi_repo), "rev-parse", "HEAD"], text=True).strip(),
    "piSharpSha": subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip(),
    "expectedContextWindows": expected_context,
    "snapshots": snapshots,
    "requests": traces,
    "offlineReloadRequests": {"pi": pi_offline_trace, "pisharp": pisharp_offline_trace},
}
pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
