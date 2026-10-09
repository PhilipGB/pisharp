#!/usr/bin/env python3
"""Compare current Pi and PiSharp router discovery and native llama classifiers."""

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
work = pathlib.Path(tempfile.mkdtemp(prefix="pisharp-llama-router-classifier-"))
agent_dir = work / "agent"
agent_dir.mkdir()

csharp = r'''using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Runtime.Classifiers;

var root = args[1];
Directory.CreateDirectory(root);
var environment = new Dictionary<string, string?>
{
    ["LLAMA_BASE_URL"] = args[0],
    ["LLAMA_API_KEY"] = "local"
};
using var http = new HttpClient();
var providers = await ProviderModelRuntime.CreateAsync(root, false,
    name => environment.GetValueOrDefault(name), http);
var chat = await providers.ListModelsAsync("llama.cpp");
var classifiers = providers.GetProvider("llama.cpp").Classifiers ?? [];
var selected = classifiers.Single(model => model.Id == "decision-model");
var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { message = "fixture" }),
    new Dictionary<string, ClassifierQuestion>
    {
        ["safe"] = new ClassifierBoolQuestion("Is the state safe?", new Dictionary<string, string>
        {
            ["true"] = "safe",
            ["false"] = "unsafe"
        })
    });
var result = await new ProviderClassifierRuntime(providers, http)
    .ClassifyAsync("llama.cpp", selected.Id, context);
var imageContext = context with { Images = [new ClassifierImage("image", "AQID", "image/png")] };
var imageResult = await new ProviderClassifierRuntime(providers, http)
    .ClassifyAsync("llama.cpp", selected.Id, imageContext);
using var canceled = new CancellationTokenSource();
canceled.Cancel();
var canceledImageResult = await new SystemOneClassifierClient(http)
    .ClassifyAsync(selected, imageContext, "local", canceled.Token);
var output = new
{
    chatModels = chat.Select(model => new
    {
        id = model.Id,
        api = model.Api,
        baseUrl = model.BaseUrl,
        contextWindow = model.ContextLength,
        maxTokens = model.MaxOutputTokens,
        reasoning = model.Reasoning,
        input = model.Input,
        compatibility = model.Compatibility
    }),
    classifiers = classifiers.Select(model => new
    {
        id = model.Id,
        api = model.Api,
        baseUrl = model.BaseUrl.AbsoluteUri.TrimEnd('/'),
        contextWindow = model.ContextWindow
    }),
    result,
    imageResult,
    canceledImageResult
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
    "import { createLlamaProvider } from '{pi}/packages/coding-agent/src/extensions/llama/provider.ts';\n"
    "const url=process.argv[2];\n"
    "const controller=createLlamaProvider();\n"
    "await controller.provider.refreshModels?.({credential:{type:'api_key',key:'local',env:{LLAMA_BASE_URL:url}},"
    "stored:undefined,publish:async publication=>{publication.update?.();return true;},allowNetwork:true,"
    "signal:new AbortController().signal});\n"
    "const context={state:{message:'fixture'},questions:{safe:{type:'bool',instructions:'Is the state safe?',"
    "criteria:{true:'safe',false:'unsafe'}}}};\n"
    "const all=controller.provider.getAllModels?.()??[];\n"
    "const selected=all.find(model=>model.type==='classifier'&&model.id==='decision-model');\n"
    "if(!selected)throw new Error('Pi did not expose the decision model as a classifier');\n"
    "const result=await controller.provider.classify?.(selected,context,{apiKey:'local'});\n"
    "const imageContext={...context,images:[{type:'image',data:'AQID',mimeType:'image/png'}]};\n"
    "const imageResult=await controller.provider.classify?.(selected,imageContext,{apiKey:'local'});\n"
    "const canceledImageResult=await controller.provider.classify?.(selected,imageContext,{apiKey:'local',signal:AbortSignal.abort()});\n"
    "const project=model=>({id:model.id,api:model.api,baseUrl:model.baseUrl,contextWindow:model.contextWindow,"
    "maxTokens:model.maxTokens,reasoning:model.reasoning,input:model.input,compatibility:model.compat});\n"
    "console.log(JSON.stringify({chatModels:controller.provider.getModels().map(project),"
    "classifiers:all.filter(model=>model.type==='classifier').map(model=>({id:model.id,api:model.api,"
    "baseUrl:model.baseUrl.replace(/\\/$/u,''),contextWindow:model.contextWindow})),result,imageResult,canceledImageResult}));\n"
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

catalog = {
    "data": [
        {
            "id": "chat-model",
            "status": {"value": "loaded"},
            "architecture": {"input_modalities": ["text", "image"], "output_modalities": ["text"]},
            "meta": {"n_ctx": 32768, "n_ctx_train": 131072},
        },
        {
            "id": "decision-model",
            "status": {"value": "sleeping"},
            "architecture": {"input_modalities": ["text"], "output_modalities": ["decisions"]},
            "meta": {"n_ctx": 8192, "n_ctx_train": 16384},
        },
        {
            "id": "sleeping-chat",
            "status": {"value": "sleeping"},
            "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]},
            "meta": {"n_ctx_train": 16384},
        },
        {
            "id": "autoload-chat",
            "status": {"value": "unloaded", "args": ["llama-server", "-c", "4096"]},
            "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]},
            "source": "preset",
            "meta": {"n_ctx_train": 8192},
        },
        {
            "id": "autoload-decision",
            "status": {"value": "unloaded"},
            "architecture": {"input_modalities": ["text"], "output_modalities": ["decisions"]},
            "source": "preset",
            "meta": {"n_ctx_train": 8192},
        },
        {"id": "failed-preset", "status": {"value": "unloaded", "failed": True}, "source": "preset"},
        {"id": "filesystem-model", "status": {"value": "unloaded"}, "source": "filesystem"},
    ]
}
trace = []
router_autoload = True


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

    def _capture(self, body=None):
        trace.append(
            {
                "method": self.command,
                "path": self.path,
                "authorization": self.headers.get("Authorization"),
                "body": body,
            }
        )

    def do_GET(self):
        self._capture()
        if self.path == "/models":
            return self._send(catalog)
        if self.path == "/props":
            return self._send({"models_autoload": router_autoload})
        if self.path == "/props?model=chat-model&autoload=false":
            return self._send({"chat_template": "{% if enable_thinking %}think{% endif %}"})
        return self._send({"error": "not found"}, 404)

    def do_POST(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            chunks = []
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            raw = b"".join(chunks)
        else:
            raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        body = json.loads(raw) if raw else {}
        self._capture(body)
        if self.path != "/v1/systemone":
            return self._send({"error": "not found"}, 404)
        return self._send(
            {
                "model": "decision-model",
                "answers": {"safe": {"type": "noul", "noul": 0.9}},
                "usage": {"input_tokens": 42, "output_tokens": 0},
            }
        )


server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{server.server_port}"


def strip_runtime_fields(value):
    if isinstance(value, dict):
        return {
            key: strip_runtime_fields(item)
            for key, item in value.items()
            if key not in {"timestamp", "usage"} and not (key == "errorMessage" and item is None)
        }
    if isinstance(value, list):
        return [strip_runtime_fields(item) for item in value]
    return value


def usage_summary(result):
    usage = result["result"].get("usage")
    if usage is None:
        return None
    if "input" in usage:
        return {
            "input": usage.get("input", 0),
            "output": usage.get("output", 0),
            "total": usage.get("totalTokens", 0),
            "cost": usage.get("cost", {}).get("total", 0),
        }
    return {
        "input": usage.get("inputTokens", 0),
        "output": usage.get("outputTokens", 0),
        "total": usage.get("totalTokens", 0),
        "cost": usage.get("cost", 0),
    }


def equal(left, right):
    if isinstance(left, dict) and isinstance(right, dict):
        return left.keys() == right.keys() and all(equal(left[key], right[key]) for key in left)
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(equal(a, b) for a, b in zip(left, right))
    if isinstance(left, float) and isinstance(right, (int, float)):
        return abs(left - right) <= 1e-12
    return left == right


scenario_results = []
primary = None
for router_autoload in (True, False):
    trace.clear()
    pi = json.loads(subprocess.check_output(["node", str(work / "pi.mjs"), url], cwd=pi_repo))
    pi_trace = list(trace)
    trace.clear()
    pisharp_root = agent_dir / ("autoload-on" if router_autoload else "autoload-off")
    pisharp_root.mkdir()
    pisharp = json.loads(
        subprocess.check_output(
            ["dotnet", str(work / "bin/Debug/net10.0/probe.dll"), url, str(pisharp_root)],
            cwd=repo,
            env=environment,
        )
    )
    pisharp_trace = list(trace)

    pi_result = strip_runtime_fields(pi)
    pisharp_result = strip_runtime_fields(pisharp)
    pi_usage = usage_summary(pi)
    pisharp_usage = usage_summary(pisharp)
    if not equal(pi_usage, pisharp_usage):
        raise AssertionError(f"Router classifier usage differs with autoload={router_autoload}:\nPi: {pi_usage}\nPiSharp: {pisharp_usage}")
    if not equal(pi_result, pisharp_result):
        raise AssertionError(f"Router classifier results differ with autoload={router_autoload}:\nPi: {pi_result}\nPiSharp: {pisharp_result}")
    if not equal(pi_trace, pisharp_trace):
        raise AssertionError(f"Router classifier HTTP traces differ with autoload={router_autoload}:\nPi: {pi_trace}\nPiSharp: {pisharp_trace}")
    expected_image_error = "System One API does not support image input"
    if pi_result["imageResult"].get("errorMessage") != expected_image_error:
        raise AssertionError(f"Unexpected router decision image result with autoload={router_autoload}: {pi_result['imageResult']}")
    if pi_result["canceledImageResult"].get("errorMessage") != expected_image_error:
        raise AssertionError(f"Unexpected canceled router decision image result with autoload={router_autoload}: {pi_result['canceledImageResult']}")
    if sum(request["path"] == "/v1/systemone" for request in pi_trace) != 1:
        raise AssertionError(f"Image classification sent a router request with autoload={router_autoload}: {pi_trace}")

    chat_ids = {model["id"] for model in pi_result["chatModels"]}
    classifier_ids = {model["id"] for model in pi_result["classifiers"]}
    expected_chat = {"chat-model", "sleeping-chat", "autoload-chat"} if router_autoload else {"chat-model", "sleeping-chat"}
    expected_classifiers = (
        {"chat-model", "decision-model", "sleeping-chat", "autoload-chat", "autoload-decision"}
        if router_autoload else {"chat-model", "decision-model", "sleeping-chat"}
    )
    if chat_ids != expected_chat:
        raise AssertionError(f"Unexpected chat eligibility with router autoload={router_autoload}: {chat_ids}")
    if classifier_ids != expected_classifiers:
        raise AssertionError(f"Unexpected classifier eligibility with router autoload={router_autoload}: {classifier_ids}")

    scenario_results.append({
        "modelsAutoload": router_autoload,
        "projection": {"chatModels": pi_result["chatModels"], "classifiers": pi_result["classifiers"]},
        "requests": {"pi": pi_trace, "pisharp": pisharp_trace},
    })
    if router_autoload:
        primary = (pi_result, pi_usage, pi_trace, pisharp_trace)

server.shutdown()


pi_result, pi_usage, pi_trace, pisharp_trace = primary

report = {
    "match": True,
    "piSha": subprocess.check_output(["git", "-C", str(pi_repo), "rev-parse", "HEAD"], text=True).strip(),
    "piSharpSha": subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip(),
    "projection": {"chatModels": pi_result["chatModels"], "classifiers": pi_result["classifiers"]},
    "result": pi_result["result"],
    "imageInput": {
        "result": pi_result["imageResult"],
        "alreadyCanceledResult": pi_result["canceledImageResult"],
        "requestsSent": 0,
    },
    "usage": pi_usage,
    "requests": {"pi": pi_trace, "pisharp": pisharp_trace},
    "autoloadScenarios": scenario_results,
}
pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
