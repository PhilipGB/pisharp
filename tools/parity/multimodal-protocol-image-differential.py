#!/usr/bin/env python3
"""Compare JSON-mode file and RPC image prompts against pinned Pi."""
import argparse
import base64
import json
import os
import pathlib
import subprocess
import tempfile

from pi_reference import temporary_empty_model_catalogs, verify_pi_checkout


PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg=="
)


def run(command, *, cwd, env, timeout):
    subprocess.run(command, cwd=cwd, env=env, check=True, timeout=timeout)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pi", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    pi = args.pi.resolve()
    pin = verify_pi_checkout(pi)
    repo = pathlib.Path(__file__).resolve().parents[2]
    probe_dir = pathlib.Path(__file__).resolve().parent
    oracle = pi / "packages/coding-agent/test/pisharp-protocol-image-probe.test.ts"
    if oracle.exists():
        raise RuntimeError(f"Refusing to overwrite {oracle}")

    with tempfile.TemporaryDirectory(prefix="pisharp-protocol-image-") as directory:
        work = pathlib.Path(directory)
        input_dir = work / "input"
        input_dir.mkdir()
        (input_dir / "pixel.png").write_bytes(PNG)
        (input_dir / "broken.png").write_bytes(PNG[:24])
        (work / "Program.cs").write_text(
            (probe_dir / "multimodal-protocol-image-probe.cs").read_text()
        )
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup>
</Project>""")

        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
             "-m:1", "--nologo", "--verbosity", "quiet"], cwd=repo, env=environment, timeout=300)
        local_output = work / "pisharp.json"
        run(["dotnet", str(work / "bin/Debug/net10.0/PiSharp.Tests.dll"),
             str(input_dir), str(local_output)], cwd=repo, env=environment, timeout=90)
        pisharp = json.loads(local_output.read_text())

        oracle.write_text((probe_dir / "multimodal-protocol-image-probe.pi.test.ts").read_text())
        try:
            pi_captures = []
            with temporary_empty_model_catalogs(pi):
                for index in range(2):
                    capture_path = work / f"pi-{index}.json"
                    pi_environment = dict(environment)
                    pi_environment["PISHARP_PROTOCOL_INPUT_DIR"] = str(input_dir)
                    pi_environment["PISHARP_PROTOCOL_OUTPUT"] = str(capture_path)
                    pi_environment["PI_CODING_AGENT_DIR"] = str(work / f"pi-profile-{index}")
                    run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                         str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), "--reporter=dot"],
                        cwd=pi, env=pi_environment, timeout=180)
                    pi_captures.append(json.loads(capture_path.read_text()))
        finally:
            oracle.unlink(missing_ok=True)

    stable = pi_captures[0] == pi_captures[1]
    pi_result = pi_captures[0]
    comparisons = {mode: pi_result[mode] == pisharp[mode] for mode in ("json", "rpc")}
    report = {
        "piSha": pin,
        "scope": "JSON-mode file input with a malformed image followed by valid PNG, plus an RPC image prompt; provider-visible text/order/MIME/data",
        "samePiCalibration": {"runs": 2, "stable": stable},
        "comparisons": comparisons,
        "pinnedPi": pi_result,
        "pisharp": pisharp,
        "passed": sum(comparisons.values()) if stable else 0,
        "total": len(comparisons),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    if not stable or report["passed"] != report["total"]:
        raise SystemExit(f"JSON/RPC image differential failed; report: {args.output}")
    print(f"Matched {report['passed']}/{report['total']} JSON/RPC image comparisons.")


if __name__ == "__main__":
    main()
