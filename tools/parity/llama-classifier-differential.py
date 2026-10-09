#!/usr/bin/env python3
"""Compare current Pi and PiSharp native llama.cpp classifier behavior."""

import argparse
import http.server
import json
import math
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
work = pathlib.Path(tempfile.mkdtemp(prefix="pisharp-llama-classifier-"))

csharp = r'''using System.Text.Json;
using PiSharp.Runtime.Classifiers;

var questions = new Dictionary<string, ClassifierQuestion>
{
    ["safe"] = new ClassifierBoolQuestion("safe?", new Dictionary<string, string> { ["true"] = "safe", ["false"] = "unsafe" }),
    ["choice"] = new ClassifierChoiceQuestion("choose", new Dictionary<string, string> { ["a"] = "first", ["b"] = "second" }),
    ["score"] = new ClassifierScoreQuestion("score", ["low", "high"])
};
var context = new ClassifierContext(JsonSerializer.SerializeToElement(new { text = "fixture" }), questions);
var imageContext = context with { Images = [new ClassifierImage("image", "AQID", "image/png")] };
using var http = new HttpClient();
var client = new LlamaClassifierClient(http);
var baseline = await client.ClassifyAsync(new("fixture", "model", "llama-cpp-classify", new Uri(args[0] + "/native/v1")), context, null);
var image = await client.ClassifyAsync(new("fixture", "model", "llama-cpp-classify", new Uri(args[0] + "/image/v1")), imageContext, null);
Console.WriteLine(JsonSerializer.Serialize(new { baseline, image }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
'''
(work / "Program.cs").write_text(csharp)
(work / "probe.csproj").write_text(
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
    '<OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
    '</PropertyGroup><ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" />'
    '</ItemGroup></Project>\n'.replace("{repo}", str(repo))
)
(work / "pi.mjs").write_text(
    "import { classify } from '{pi}/packages/ai/src/api/llama-cpp-classify.ts';\n"
    "const context={state:{text:'fixture'},questions:{safe:{type:'bool',instructions:'safe?',criteria:{true:'safe',false:'unsafe'}},"
    "choice:{type:'choice',instructions:'choose',criteria:{a:'first',b:'second'}},"
    "score:{type:'score',instructions:'score',criteria:['low','high']}}};\n"
    "const imageContext={...context,images:[{type:'image',data:'AQID',mimeType:'image/png'}]};\n"
    "const model=suffix=>({provider:'fixture',id:'model',api:'llama-cpp-classify',baseUrl:process.argv[2]+'/'+suffix+'/v1'});\n"
    "const baseline=await classify(model('native'),context);\n"
    "const image=await classify(model('image'),imageContext);\n"
    "console.log(JSON.stringify({baseline,image}));\n"
    .replace("{pi}", pi_repo.as_posix())
)

trace = []


class Server(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        if self.headers.get("Transfer-Encoding") == "chunked":
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
            raw = self.rfile.read(int(self.headers["Content-Length"]))
        body = json.loads(raw)
        trace.append({"method": self.command, "path": self.path, "authorization": self.headers.get("Authorization")})
        assert body["model"] == "model"
        if self.path.endswith("/tokenize"):
            text = body["content"]
            token = {"Yes": 2, "No": 3, "A": 4, "B": 5, "0": 6, "1": 7}
            response = {"tokens": [1] + ([token[text[1:]]] if text.startswith("\n") and len(text) > 1 else [])}
        elif self.path.endswith("/apply-template"):
            assert body["chat_template_kwargs"] == {"enable_thinking": False}
            response = {"prompt": body["messages"][1]["content"] + "<think>"}
        else:
            assert self.path.endswith("/completion") and body["n_predict"] == 1 and body["post_sampling_probs"] is False
            assert body["prompt"].endswith("<think></think>")
            probabilities = [{"id": i, "logprob": math.log(0.75 if i % 2 == 0 else 0.25)} for i in range(2, 8)]
            response = {"completion_probabilities": [{"top_logprobs": probabilities if body["n_probs"] > 256 else probabilities[::2]}]}
        data = json.dumps(response).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


environment = dict(os.environ)
environment.setdefault("NUGET_HTTP_CACHE_PATH", "/tmp/pisharp-nuget-http")
subprocess.run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false", "-m:1"],
               env=environment, check=True, stdout=subprocess.DEVNULL)
server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Server)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{server.server_port}"

pi = json.loads(subprocess.check_output(["node", str(work / "pi.mjs"), url], cwd=pi_repo))
pi_trace = list(trace)
trace.clear()
pisharp = json.loads(subprocess.check_output(["dotnet", str(work / "bin/Debug/net10.0/probe.dll"), url], cwd=repo, env=environment))
pisharp_trace = list(trace)
server.shutdown()


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


def equal(left, right):
    if isinstance(left, dict) and isinstance(right, dict):
        return left.keys() == right.keys() and all(equal(left[key], right[key]) for key in left)
    if isinstance(left, list) and isinstance(right, list):
        return len(left) == len(right) and all(equal(a, b) for a, b in zip(left, right))
    if isinstance(left, float) and isinstance(right, (int, float)):
        return math.isclose(left, right, abs_tol=1e-12)
    return left == right


pi_result = strip_runtime_fields(pi)
pisharp_result = strip_runtime_fields(pisharp)
if not equal(pi_result, pisharp_result):
    raise AssertionError(f"Native classifier results differ:\nPi: {pi_result}\nPiSharp: {pisharp_result}")
if pi_trace != pisharp_trace:
    raise AssertionError(f"Native classifier HTTP traces differ:\nPi: {pi_trace}\nPiSharp: {pisharp_trace}")
if pi_result["image"]["errorMessage"] != "llama.cpp classification does not support image input":
    raise AssertionError(f"Pi returned an unexpected image error: {pi_result['image']}")
for label, requests in (("Pi", pi_trace), ("PiSharp", pisharp_trace)):
    if any(request["path"].startswith("/image/") for request in requests):
        raise AssertionError(f"{label} sent an HTTP request for rejected classifier images: {requests}")

report = {
    "match": True,
    "piSha": subprocess.check_output(["git", "-C", str(pi_repo), "rev-parse", "HEAD"], text=True).strip(),
    "piSharpSha": subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip(),
    "baseline": {"result": pi_result["baseline"], "requests": {"pi": pi_trace, "pisharp": pisharp_trace}},
    "imageInput": {
        "result": pi_result["image"],
        "requestsSent": {"pi": 0, "pisharp": 0},
        "baselineRequestCounts": {"pi": len(pi_trace), "pisharp": len(pisharp_trace)},
    },
}
pathlib.Path(args.output).write_text(json.dumps(report, indent=2) + "\n")
