#!/usr/bin/env python3
"""Compare Linux clipboard image selection and fallback against pinned Pi."""
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
    oracle = pi / "packages/coding-agent/test/pisharp-clipboard-probe.test.ts"
    path_oracle = pi / "packages/coding-agent/test/pisharp-clipboard-path-probe.test.ts"
    if oracle.exists() or path_oracle.exists():
        raise RuntimeError(f"Refusing to overwrite pinned Pi probes: {oracle}, {path_oracle}")

    with tempfile.TemporaryDirectory(prefix="pisharp-clipboard-differential-") as directory:
        work = pathlib.Path(directory)
        (work / "Program.cs").write_text(
            (probe_dir / "multimodal-clipboard-probe.cs").read_text()
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
        run(["dotnet", str(work / "bin/Debug/net10.0/PiSharp.Tests.dll"), str(local_output)],
            cwd=repo, env=environment, timeout=90)
        pisharp = json.loads(local_output.read_text())

        oracle.write_text((probe_dir / "multimodal-clipboard-probe.pi.test.ts").read_text())
        path_oracle.write_text((probe_dir / "multimodal-clipboard-path-probe.pi.test.ts").read_text())
        try:
            pi_captures = []
            path_captures = []
            for index in range(2):
                capture_path = work / f"pi-{index}.json"
                path_capture_path = work / f"pi-paths-{index}.json"
                pi_environment = dict(environment)
                pi_environment["PISHARP_CLIPBOARD_OUTPUT"] = str(capture_path)
                pi_environment["PISHARP_CLIPBOARD_PATH_OUTPUT"] = str(path_capture_path)
                pi_environment["PI_CODING_AGENT_DIR"] = str(work / f"pi-profile-{index}")
                run(["node", str(pi / "node_modules/vitest/vitest.mjs"), "run", "--config",
                     str(pi / "packages/coding-agent/vitest.config.ts"), str(oracle), str(path_oracle), "--reporter=dot"],
                    cwd=pi, env=pi_environment, timeout=180)
                pi_captures.append(json.loads(capture_path.read_text()))
                path_captures.append(json.loads(path_capture_path.read_text()))
        finally:
            oracle.unlink(missing_ok=True)
            path_oracle.unlink(missing_ok=True)

    stable = pi_captures[0] == pi_captures[1]
    paths_stable = path_captures[0] == path_captures[1]
    pi_result = pi_captures[0]
    pi_paths = path_captures[0]
    comparisons = {
        scenario["id"]: scenario == pisharp["acquisitions"][index]
        for index, scenario in enumerate(pi_result)
    }
    comparisons["clipboard-file-path-precedence"] = pi_paths == pisharp["filePathPrecedence"]
    report = {
        "piSha": pin,
        "scope": "Linux clipboard image acquisition plus native file-path precedence over image/text fallbacks",
        "samePiCalibration": {"runs": 2, "stable": stable},
        "comparisons": comparisons,
        "pinnedPi": pi_result,
        "samePiFilePathCalibration": {"runs": 2, "stable": paths_stable},
        "pinnedPiFilePathPrecedence": pi_paths,
        "pisharp": pisharp["acquisitions"],
        "pisharpFilePathPrecedence": pisharp["filePathPrecedence"],
        "passed": sum(comparisons.values()) if stable else 0,
        "total": len(comparisons),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n")
    if not stable or not paths_stable or report["passed"] != report["total"]:
        raise SystemExit(f"Clipboard differential failed; report: {args.output}")
    print(f"Matched {report['passed']}/{report['total']} clipboard comparisons.")


if __name__ == "__main__":
    main()
