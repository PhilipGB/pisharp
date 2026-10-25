#!/usr/bin/env python3
"""Compare provider requests built from an image session after it is reopened."""
import argparse
import base64
import json
import os
import pathlib
import struct
import subprocess
import tempfile
import zlib

from pi_reference import temporary_empty_model_catalogs, verify_pi_checkout


def png(width=3, height=2, rgba=(30, 90, 160, 255)):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    raw = b"".join(b"\x00" + bytes(rgba) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def user_turns(api, payload):
    messages = payload["input"] if api == "openai-responses" else payload["messages"]
    turns = []
    for message in messages:
        if message.get("role") != "user":
            continue
        content = message.get("content", "")
        if isinstance(content, str):
            turns.append([{"type": "text", "text": content}])
            continue
        parts = []
        for part in content:
            kind = part.get("type")
            if kind in {"input_text", "text"}:
                parts.append({"type": "text", "text": part["text"]})
            elif kind == "input_image":
                mime, data = part["image_url"].split(":", 1)[1].split(";base64,", 1)
                parts.append({"type": "image", "mimeType": mime, "data": data})
            elif kind == "image_url":
                data_url = part["image_url"]["url"]
                mime, data = data_url.split(":", 1)[1].split(";base64,", 1)
                parts.append({"type": "image", "mimeType": mime, "data": data})
            elif kind == "image":
                source = part["source"]
                parts.append({"type": "image", "mimeType": source["media_type"], "data": source["data"]})
            else:
                parts.append({"type": kind})
        turns.append(parts)
    return turns


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    pi = args.pi.resolve()
    pin = verify_pi_checkout(pi)
    repo = pathlib.Path(__file__).resolve().parents[2]
    probe_dir = pathlib.Path(__file__).resolve().parent
    inactive_image = base64.b64encode(png(rgba=(180, 40, 70, 255))).decode("ascii")
    active_image = base64.b64encode(png()).decode("ascii")

    with tempfile.TemporaryDirectory(prefix="pisharp-session-wire-") as directory:
        work = pathlib.Path(directory)
        (work / "Program.cs").write_text((probe_dir / "multimodal-session-wire-probe.cs").read_text())
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.SessionWireProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.1" /></ItemGroup>
</Project>""")
        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        environment["NO_PROXY"] = "127.0.0.1,localhost"
        environment["no_proxy"] = "127.0.0.1,localhost"
        for name in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"):
            environment.pop(name, None)
        subprocess.run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
                        "-m:1", "--nologo", "--verbosity", "quiet"], env=environment, check=True,
                       timeout=240)
        pisharp = json.loads(subprocess.check_output([
            "dotnet", str(work / "bin/Debug/net10.0/PiSharp.SessionWireProbe.dll"), inactive_image, active_image
        ], env=environment, text=True, timeout=120))

        oracle = pi / "packages/coding-agent/test/pisharp-session-wire-differential.test.ts"
        if oracle.exists():
            raise RuntimeError(f"Refusing to overwrite {oracle}")
        oracle.write_text((probe_dir / "multimodal-session-wire-probe.pi.test.ts").read_text())
        try:
            pi_captures = []
            with temporary_empty_model_catalogs(pi):
                for index in range(2):
                    capture_path = work / f"pi-{index}.json"
                    pi_environment = dict(environment)
                    pi_environment["PISHARP_SESSION_WIRE_OUTPUT"] = str(capture_path)
                    pi_environment["PISHARP_SESSION_WIRE_INACTIVE_IMAGE"] = inactive_image
                    pi_environment["PISHARP_SESSION_WIRE_ACTIVE_IMAGE"] = active_image
                    pi_environment["PI_CODING_AGENT_DIR"] = str(work / f"pi-profile-{index}")
                    subprocess.run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                                    str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), "--reporter=dot"],
                                   cwd=pi, env=pi_environment, check=True, timeout=240)
                    pi_captures.append(json.loads(capture_path.read_text()))
        finally:
            oracle.unlink(missing_ok=True)

    pinned = {entry["api"]: entry for entry in pi_captures[0]}
    pinned_repeat = {entry["api"]: entry for entry in pi_captures[1]}
    local = {entry["api"]: entry for entry in pisharp}
    cases = []
    stable = True
    for api in ("openai-responses", "openai-completions", "anthropic-messages"):
        pinned_entry, sharp_entry = pinned[api], local[api]
        pinned_turns = user_turns(api, pinned_entry["payload"])
        repeat_entry = pinned_repeat[api]
        same_pi_stable = (pinned_entry["path"] == repeat_entry["path"] and
                          pinned_turns == user_turns(api, repeat_entry["payload"]))
        stable = stable and same_pi_stable
        sharp_inactive_branch = user_turns(api, sharp_entry["captures"][1]["payload"])
        sharp_active_branch = user_turns(api, sharp_entry["captures"][2]["payload"])
        sharp_continuation = sharp_entry["captures"][3]
        sharp_turns = user_turns(api, sharp_continuation["payload"])
        images = [part for turn in pinned_turns for part in turn if part["type"] == "image"]
        inactive_sent = any(part["type"] == "image" and part["data"] == inactive_image
                            for turn in sharp_inactive_branch for part in turn)
        active_sent = any(part["type"] == "image" and part["data"] == active_image
                          for turn in sharp_active_branch for part in turn)
        cases.append({
            "api": api,
            "model": pinned_entry["model"],
            "pathMatch": pinned_entry["path"] == sharp_continuation["path"],
            "orderedUserTurnsMatch": pinned_turns == sharp_turns,
            "expectedTurnShape": len(pinned_turns) == 3 and
                [part.get("text") for part in pinned_turns[0] if part["type"] == "text"] == ["base"] and
                [part.get("text") for part in pinned_turns[1] if part["type"] == "text"] == ["inspect λ"] and
                [part.get("text") for part in pinned_turns[2] if part["type"] == "text"] == ["continue"] and
                len(images) == 1 and images[0]["data"] == active_image and
                not any(part["type"] == "image" for part in pinned_turns[2]),
            "inactiveBranchImageAbsent": not any(part["type"] == "image" and part["data"] == inactive_image
                                                  for turn in pinned_turns for part in turn),
            "pisharpInactiveBranchImageWasSent": inactive_sent,
            "pisharpSelectedBranchImageWasSent": active_sent,
            "pinnedUserTurns": pinned_turns,
            "pisharpUserTurns": sharp_turns,
            "initialPisharpPath": sharp_entry["captures"][0]["path"],
            "samePiStable": same_pi_stable,
        })
    report = {
        "piSha": pin,
        "scope": "provider request serialization after reopening a branched image session; active branch retained and inactive branch image excluded",
        "samePiCalibration": {"runs": 2, "stable": stable},
        "protocolsPassed": sum(case["pathMatch"] and case["orderedUserTurnsMatch"] and
                                case["expectedTurnShape"] and case["inactiveBranchImageAbsent"] and
                                case["pisharpInactiveBranchImageWasSent"] and
                                case["pisharpSelectedBranchImageWasSent"] and
                                case["samePiStable"] for case in cases),
        "protocolsTotal": len(cases),
        "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    if not stable or report["protocolsPassed"] != report["protocolsTotal"]:
        raise SystemExit(f"Session provider-wire differential failed; report: {args.output}")
    print(f"Matched post-restart image continuation requests for {report['protocolsPassed']}/{report['protocolsTotal']} provider protocols.")


if __name__ == "__main__":
    main()
