#!/usr/bin/env python3
"""Compare image history and provider continuation after a saved-session reopen."""
import argparse
import json
import os
import pathlib
import subprocess
import tempfile

from pi_reference import verify_pi_checkout


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
    oracle = pi / "packages/coding-agent/test/pisharp-session-continuation-probe.test.ts"
    if oracle.exists():
        raise RuntimeError(f"Refusing to overwrite {oracle}")

    with tempfile.TemporaryDirectory(prefix="pisharp-session-continuation-") as directory:
        work = pathlib.Path(directory)
        (work / "Program.cs").write_text(
            (probe_dir / "multimodal-session-continuation-probe.cs").read_text()
        )
        (work / "probe.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.SessionContinuationProbe</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Runtime/PiSharp.Runtime.csproj" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" Version="4.151.1" /></ItemGroup>
</Project>""")

        environment = dict(os.environ)
        environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
        run(["dotnet", "build", str(work / "probe.csproj"), "-p:UseSharedCompilation=false",
             "-m:1", "--nologo", "--verbosity", "quiet"], cwd=repo, env=environment, timeout=240)
        local_output = work / "pisharp.json"
        local_root = work / "pisharp-session"
        run(["dotnet", str(work / "bin/Debug/net10.0/PiSharp.SessionContinuationProbe.dll"),
             str(local_root), str(local_output)], cwd=repo, env=environment, timeout=90)
        pisharp = json.loads(local_output.read_text())

        oracle.write_text((probe_dir / "multimodal-session-continuation-probe.pi.test.ts").read_text())
        try:
            pi_captures = []
            for index in range(2):
                capture_path = work / f"pi-{index}.json"
                pi_environment = dict(environment)
                pi_environment["PISHARP_SESSION_OUTPUT"] = str(capture_path)
                pi_environment["PI_CODING_AGENT_DIR"] = str(work / f"pi-profile-{index}")
                run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                     str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), "--reporter=dot"],
                    cwd=pi, env=pi_environment, timeout=180)
                pi_captures.append(json.loads(capture_path.read_text()))
        finally:
            oracle.unlink(missing_ok=True)

    same_pi_stable = pi_captures[0] == pi_captures[1]
    pi_result = pi_captures[0]
    fields = ["initialProviderParts", "restoredUser", "continuationProviderParts"]
    comparisons = {field: pi_result[field] == pisharp[field] for field in fields}
    report = {
        "piSha": pin,
        "scope": "image-bearing user history after saved-session reopen and provider continuation",
        "samePiCalibration": {"runs": 2, "stable": same_pi_stable},
        "comparisons": comparisons,
        "pinnedPi": pi_result,
        "pisharp": pisharp,
        "passed": sum(comparisons.values()) if same_pi_stable else 0,
        "total": len(fields),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    if not same_pi_stable or report["passed"] != report["total"]:
        raise SystemExit(f"Session continuation differential failed; report: {args.output}")
    print(f"Matched {report['passed']}/{report['total']} image session-continuation comparisons.")


if __name__ == "__main__":
    main()
