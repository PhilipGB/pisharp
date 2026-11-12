#!/usr/bin/env python3
"""Compare pinned Pi and PiSharp on the branchSummary.skipPrompt settings boundary.

Both products consume tools/parity/branch_summary_skip_prompt_cases.json. The pinned
side loads the cases through the real SettingsManager storage path; the PiSharp side
loads the same JSON through UserSettings and overlays the trusted project scope.
Normalization keeps only the observable decision: whether the interactive tree flow
would show "Summarize branch?", the merged skipPrompt value and the reserve.
"""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import tempfile

from pi_reference import verify_pi_checkout

from terminal.thread_limits import apply_test_thread_limit
apply_test_thread_limit()

PINNED_PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--pi", required=True)
parser.add_argument("--output", required=True)
parser.add_argument("--cases", default=None)
args = parser.parse_args()
verify_pi_checkout(args.pi)
pi = pathlib.Path(args.pi).resolve()
repo = pathlib.Path(__file__).resolve().parents[2]
tools = repo / "tools/parity"
cases = pathlib.Path(args.cases) if args.cases else tools / "branch_summary_skip_prompt_cases.json"

with tempfile.TemporaryDirectory(prefix="pisharp-skip-prompt-") as directory:
    work = pathlib.Path(directory)
    subprocess.run(
        ["node", "--import", str(pi / "packages/coding-agent/src/experimental/source-resolver.ts"),
         str(tools / "branch_summary_skip_prompt_oracle.mjs"), str(pi), str(cases),
         str(work / "pi.json")],
        check=True, timeout=180)

    (work / "probe/Program.cs").parent.mkdir(parents=True)
    shutil.copyfile(tools / "branch_summary_skip_prompt_probe.cs", work / "probe/Program.cs")
    (work / "probe/probe.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>PiSharp.Tests</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="{repo}/src/PiSharp.Cli/PiSharp.Cli.csproj" /></ItemGroup></Project>''')
    environment = dict(os.environ)
    environment["NUGET_HTTP_CACHE_PATH"] = "/tmp/pisharp-nuget-http"
    subprocess.run(["dotnet", "build", str(work / "probe/probe.csproj"), "-p:UseSharedCompilation=false",
                    "-m:1", "--nologo"], env=environment, check=True,
                   stdout=subprocess.DEVNULL, timeout=300)
    pisharp = json.loads(subprocess.check_output(
        ["dotnet", str(work / "probe/bin/Debug/net10.0/PiSharp.Tests.dll"), cases.read_text()],
        env=environment, timeout=120))

    pi_results = json.loads((work / "pi.json").read_text())

def canonical(raw_text):
    """Both sides serialize the stored token differently; compare the parsed JSON value."""
    if raw_text in ("<absent>", "<undefined>"):
        return raw_text
    return json.dumps(json.loads(raw_text), separators=(",", ":"))


def normalize_pi(case):
    return {
        "id": case["id"],
        "loaded": True,
        "prompts": case["prompts"],
        "skipPrompt": canonical(case["skipPrompt"]),
        "parentPresent": case["hasBranchSummaryKey"],
        "reserve": int(case["reserveTokens"]),
    }


def normalize_pisharp(case):
    stored = case["skipPrompt"]
    # The probe keeps the stored token verbatim, so both sides compare the same JSON text.
    raw = "<absent>" if stored is None else canonical(stored["raw"])
    return {
        "id": case["id"],
        "loaded": case["loaded"],
        "error": case["error"],
        "prompts": case["prompts"],
        "skipPrompt": raw,
        "parentPresent": case["parent"]["present"],
        "reserve": case["reserve"],
    }

pi_cases = [normalize_pi(case) for case in pi_results["cases"]]
pisharp_cases = [normalize_pisharp(case) for case in pisharp]

# Pinned Pi's JSON.stringify renders a present JSON null as "null" and an absent member as "<undefined>".
for case in pi_cases:
    if case["skipPrompt"] == "<undefined>":
        case["skipPrompt"] = "<absent>"

differences = []
for expected, actual in zip(pi_cases, pisharp_cases):
    for key in ("loaded", "prompts", "skipPrompt", "parentPresent", "reserve"):
        if expected[key] != actual[key]:
            differences.append({"id": expected["id"], "field": key,
                                "pi": expected[key], "pisharp": actual[key]})
    if actual.get("error") and expected["loaded"]:
        differences.append({"id": expected["id"], "field": "error", "pi": None,
                            "pisharp": actual["error"]})

document = {
    "schema": 1,
    "capability": "settings-branchsummary-skipprompt",
    "piSha": pi_results["piSha"],
    "pisharpSha": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip(),
    "cases": [{"id": case["id"], "pi": case, "pisharp": pisharp_case}
               for case, pisharp_case in zip(pi_cases, pisharp_cases)],
    "normalization": "Both sides record whether the tree flow prompts, the merged skipPrompt "
                     "value, whether the branchSummary key is present and the effective reserve.",
    "match": not differences,
    "differences": differences,
}
pathlib.Path(args.output).parent.mkdir(parents=True, exist_ok=True)
pathlib.Path(args.output).write_text(json.dumps(document, indent=2) + "\n")
print(f"{'matched' if not differences else 'DIFFERENT'}: {len(pi_cases)} cases, "
      f"{len(differences)} differences, pinned Pi {PINNED_PI_SHA}")
for difference in differences:
    print(f"  {difference['id']}.{difference['field']}: pi={difference['pi']!r} "
          f"pisharp={difference['pisharp']!r}")
raise SystemExit(0 if not differences else 1)
