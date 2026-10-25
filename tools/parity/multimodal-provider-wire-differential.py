#!/usr/bin/env python3
"""Compare image-bearing physical provider requests with pinned Pi."""
import argparse
import base64
import json
import os
import pathlib
import struct
import subprocess
import tempfile
import zlib

from pi_reference import verify_pi_checkout


def png(width=3, height=2, rgba=(30, 90, 160, 255)):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    raw = b"".join(b"\x00" + bytes(rgba) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" +
            chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)) +
            chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def user_parts(api, payload):
    if api == "openai-responses":
        messages = payload["input"]
    else:
        messages = payload["messages"]
    user = next(message for message in reversed(messages) if message.get("role") == "user")
    parts = user["content"]
    items = []
    for part in parts:
        kind = part.get("type")
        if kind in {"input_text", "text"}:
            items.append({"type": "text", "text": part["text"]})
        elif kind == "input_image":
            mime, data = part["image_url"].split(":", 1)[1].split(";base64,", 1)
            items.append({"type": "image", "mimeType": mime, "data": data})
        elif kind == "image_url":
            data_url = part["image_url"]["url"]
            mime, data = data_url.split(":", 1)[1].split(";base64,", 1)
            items.append({"type": "image", "mimeType": mime, "data": data})
        elif kind == "image":
            source = part["source"]
            items.append({"type": "image", "mimeType": source["media_type"], "data": source["data"]})
        else:
            items.append({"type": kind})
    return items


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    pi = args.pi.resolve()
    pin = verify_pi_checkout(pi)
    repo = pathlib.Path(__file__).resolve().parents[2]
    probe_dir = pathlib.Path(__file__).resolve().parent
    image = base64.b64encode(png()).decode("ascii")

    with tempfile.TemporaryDirectory(prefix="pisharp-provider-wire-") as directory:
        work = pathlib.Path(directory)
        (work / "Program.cs").write_text((probe_dir / "multimodal-provider-wire-probe.cs").read_text())
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.ProviderWireProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.1" /></ItemGroup>
</Project>""")
        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        environment["NO_PROXY"] = "127.0.0.1,localhost"
        environment["no_proxy"] = "127.0.0.1,localhost"
        for name in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"):
            environment.pop(name, None)
        subprocess.run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
                        "-m:1", "--nologo"], env=environment, check=True, stdout=subprocess.DEVNULL, timeout=240)
        pisharp = json.loads(subprocess.check_output([
            "dotnet", str(work / "bin/Debug/net10.0/PiSharp.ProviderWireProbe.dll"), image
        ], env=environment, text=True, timeout=90))

        oracle = pi / "packages/ai/test/pisharp-provider-wire-differential.test.ts"
        if oracle.exists():
            raise RuntimeError(f"Refusing to overwrite {oracle}")
        oracle.write_text((probe_dir / "multimodal-provider-wire-probe.pi.test.ts").read_text())
        try:
            environment["PISHARP_PROVIDER_IMAGE"] = image
            environment["PISHARP_PROVIDER_OUTPUT"] = str(work / "pi.json")
            subprocess.run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                            str(pi / "packages/ai/vitest.config.ts"), str(oracle), "--reporter=dot"],
                           cwd=pi / "packages/ai", env=environment, check=True, timeout=180)
        finally:
            oracle.unlink(missing_ok=True)
        pi_capture = json.loads((work / "pi.json").read_text())

    pinned = {entry["api"]: entry for entry in pi_capture}
    local = {entry["api"]: entry for entry in pisharp}
    rows = []
    for api in ("openai-responses", "openai-completions", "anthropic-messages"):
        pi_entry, sharp_entry = pinned[api], local[api]
        pi_parts = user_parts(api, pi_entry["first"]["payload"])
        sharp_parts = user_parts(api, sharp_entry["payload"])
        path_match = pi_entry["first"]["path"] == sharp_entry["path"]
        rows.append({"api": api, "model": pi_entry["model"], "pathMatch": path_match,
                     "orderedUserPartsMatch": pi_parts == sharp_parts, "pinnedParts": pi_parts,
                     "pisharpParts": sharp_parts,
                     "samePiStable": pi_entry["first"] == pi_entry["second"]})
    report = {"piSha": pin, "scope": "physical image-bearing request serialization at pinned Pi provider streams and PiSharp MAF ChatClient HTTP boundaries",
              "protocolsPassed": sum(row["pathMatch"] and row["orderedUserPartsMatch"] and row["samePiStable"] for row in rows),
              "protocolsTotal": len(rows), "cases": rows}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    if report["protocolsPassed"] != report["protocolsTotal"]:
        raise SystemExit(f"Provider-wire differential failed; report: {args.output}")
    print(f"Matched image-bearing physical requests for {report['protocolsPassed']}/{report['protocolsTotal']} provider protocols.")


if __name__ == "__main__":
    main()
