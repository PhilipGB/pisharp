#!/usr/bin/env python3
"""Compare positional truncated-image recovery at the provider-visible boundary."""
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


def normalize_paths(value, root):
    if isinstance(value, str):
        return value.replace(str(root), "<fixture-root>")
    if isinstance(value, list):
        return [normalize_paths(item, root) for item in value]
    if isinstance(value, dict):
        return {key: normalize_paths(item, root) for key, item in value.items()}
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    pi = args.pi.resolve()
    pin = verify_pi_checkout(pi)
    repo = pathlib.Path(__file__).resolve().parents[2]
    probe_dir = pathlib.Path(__file__).resolve().parent
    broken = b"\x89PNG\r\n\x1a\n" + struct.pack(">I", 13) + b"IHDR"
    valid = png()
    scenario = {"id": "truncated-before-valid", "prompt": "inspect", "files": ["broken.png", "valid.png"]}

    with tempfile.TemporaryDirectory(prefix="pisharp-file-recovery-") as directory:
        work = pathlib.Path(directory)
        root = work / "fixtures λ"
        root.mkdir()
        (root / "broken.png").write_bytes(broken)
        (root / "valid.png").write_bytes(valid)
        (work / "Program.cs").write_text((probe_dir / "multimodal-file-recovery-probe.cs").read_text())
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.FileRecoveryProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.1" /></ItemGroup>
</Project>""")
        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        subprocess.run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
                        "-m:1", "--nologo", "--verbosity", "quiet"], env=environment, check=True,
                       timeout=240)
        pisharp = json.loads(subprocess.check_output([
            "dotnet", str(work / "bin/Debug/net10.0/PiSharp.FileRecoveryProbe.dll"),
            str(root), base64.b64encode(broken).decode("ascii"), base64.b64encode(valid).decode("ascii")
        ], env=environment, text=True, timeout=90))

        oracle = pi / "packages/coding-agent/test/pisharp-file-recovery-differential.test.ts"
        if oracle.exists():
            raise RuntimeError(f"Refusing to overwrite {oracle}")
        oracle.write_text((probe_dir / "multimodal-file-recovery-probe.pi.test.ts").read_text())
        try:
            pi_captures = []
            with temporary_empty_model_catalogs(pi):
                for index in range(2):
                    capture_path = work / f"pi-{index}.json"
                    pi_environment = dict(environment)
                    pi_environment["PISHARP_FILE_RECOVERY_ROOT"] = str(root)
                    pi_environment["PISHARP_FILE_RECOVERY_SCENARIOS"] = json.dumps([scenario], ensure_ascii=False)
                    pi_environment["PISHARP_FILE_RECOVERY_OUTPUT"] = str(capture_path)
                    pi_environment["PI_CODING_AGENT_DIR"] = str(work / f"pi-profile-{index}")
                    subprocess.run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                                    str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), "--reporter=dot"],
                                   cwd=pi, env=pi_environment, check=True, timeout=240)
                    pi_captures.append(json.loads(capture_path.read_text()))
        finally:
            oracle.unlink(missing_ok=True)

    normalized_pi = [normalize_paths(capture, root) for capture in pi_captures]
    normalized_local = normalize_paths(pisharp, root)
    stable = normalized_pi[0] == normalized_pi[1]
    pinned = normalized_pi[0]["first"][0]["providerParts"]
    local = normalized_local
    text_parts = [part["text"] for part in pinned if part["type"] == "text"]
    images = [part for part in pinned if part["type"] == "image"]
    matches = pinned == local
    expected = (len(images) == 1 and images[0]["data"] == base64.b64encode(valid).decode("ascii") and
                any("[Image omitted: could not be resized below the inline image size limit.]" in text
                    for text in text_parts) and
                any("broken.png" in text and "valid.png" in text for text in text_parts))
    report = {
        "piSha": pin,
        "scope": "Deferred selected-model normalization of a truncated positional PNG followed by a valid PNG, compared at provider-visible prompt content",
        "samePiCalibration": {"runs": 2, "stable": stable},
        "providerPartsMatch": matches,
        "expectedRecovery": expected,
        "pinnedProviderParts": pinned,
        "pisharpProviderParts": local,
        "passed": int(stable and matches and expected),
        "total": 1,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    if not stable or not matches or not expected:
        raise SystemExit(f"Positional image recovery differential failed; report: {args.output}")
    print("Matched truncated-image recovery and valid-image continuation at provider context (1/1).")


if __name__ == "__main__":
    main()
