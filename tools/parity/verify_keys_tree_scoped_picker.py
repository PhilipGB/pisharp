#!/usr/bin/env python3
"""Verify the keys-tree-scoped-picker acceptance manifest and committed evidence."""
import argparse
import copy
import json
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
REQUIRED_CASES = [f"T{index:02d}" for index in range(1, 9)] + \
                 [f"S{index:02d}" for index in range(1, 8)]
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
CASE_IDS = ("100x30-dark-fullscreen", "100x30-dark-regular",
            "140x40-dark-fullscreen", "140x40-dark-regular")
FIXTURE_ID = "terminal-keys-tree-scoped-picker"
DEFAULT_ROWS = ["U2", "A3", "C7", "C8", "R10", "K11", "Y12", "Z13", "B4", "B5"]
ALL_ROWS = ["M1", "T1", "U2", "A3", "C7", "C8", "R10", "K11", "Y12", "Z13",
            "T15", "L16", "B4", "B5", "L6"]
NO_TOOLS_ROWS = ["U2", "A3", "C7", "C8", "K11", "Y12", "Z13", "B4", "B5"]
USER_ROWS = ["U2", "C7", "B4"]
LABELED_ROWS = ["A3", "C8"]
SUMMARY_OPTIONS = ["Summarize branch?", "\u2192 No summary", "Summarize",
                   "Summarize with custom prompt"]
SCOPED_INITIAL = ["fixture-model", "alpha", "beta"]
SCOPED_ALL = ["fixture-model", "alpha", "beta", "gamma"]


class VerificationError(RuntimeError):
    pass


def load_json(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise VerificationError(f"Cannot read {path}: {error}") from error


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def git_output(*arguments):
    return subprocess.check_output(["git", *arguments], cwd=ROOT, text=True).strip()


def implementation_sha():
    return git_output("log", "-1", "--format=%H", "--", "src", "tests")


def validate_manifest(manifest, implementation):
    require(manifest.get("schema") == 1, "Manifest schema must be 1.")
    require(manifest.get("capability") == "keys-tree-scoped-picker",
            "Manifest capability must be keys-tree-scoped-picker.")
    require(manifest.get("piSha") == PI_SHA,
            f"Manifest Pi SHA is {manifest.get('piSha')!r}, expected {PI_SHA}.")
    require(manifest.get("oracleScenarioCount") == len(REQUIRED_CASES),
            f"Manifest must claim {len(REQUIRED_CASES)} oracle scenarios.")
    recorded = manifest.get("implementationSha")
    require(len(recorded or "") == 40, "Manifest must record a 40-character implementation SHA.")
    require(recorded == implementation,
            f"Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.")
    cases = manifest.get("cases") or []
    ids = [case.get("id") for case in cases]
    require(len(ids) == len(set(ids)), "Manifest contains duplicate case ids.")
    require(ids == REQUIRED_CASES, f"Manifest cases must be {REQUIRED_CASES}, found {ids}.")
    for case in cases:
        require(case.get("required") is True, f"{case['id']} must be required.")
        require(case.get("status") == "pass",
                f"{case['id']} is {case.get('status')!r}; required cases may only be pass.")
        require(case.get("oracle") == [case["id"]],
                f"{case['id']} must reference its own oracle scenario.")
        require(case.get("tests"), f"{case['id']} has no deterministic test evidence.")
        require(case.get("ptyCheckpoints"), f"{case['id']} has no PTY checkpoint evidence.")
    comparisons = manifest.get("requiredComparisons") or []
    comparison_ids = {comparison.get("id") for comparison in comparisons}
    require({"paired-pty", "same-pi-calibration"} <= comparison_ids,
            "Manifest must require paired PTY and same-Pi calibration comparisons.")
    for comparison in comparisons:
        require(comparison.get("required") is True,
                f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get("status") == "match",
                f"Unresolved required comparison {comparison.get('id')}: {comparison.get('status')!r}.")
    for finding in manifest.get("findings") or []:
        require(finding.get("required") is False,
                f"Finding {finding.get('id')} expands the required contract.")
    tests = manifest.get("requiredTests") or {}
    require(len(tests.get("facts") or []) >= 11,
            "Manifest must list at least eleven required deterministic facts.")
    require(len(set(tests["facts"])) == len(tests["facts"]), "Duplicate required test names.")
    require(set(tests.get("ptyEvidence") or []) == set(CASE_IDS),
            "Manifest PTY evidence ids must name all four paired cases.")
    require((manifest.get("terminalFixtures") or []) ==
            ["tools/parity/terminal/fixtures/terminal-keys-tree-scoped-picker.json"],
            "Manifest must name the keys-tree-scoped-picker terminal fixture.")
    require(manifest.get("oracleFile") ==
            "docs/parity/fixtures/keys-tree-scoped-picker-pi-oracle-2026-11-05.json",
            "Manifest must name the pinned-Pi oracle file.")
    require(manifest.get("checkpointFile") ==
            "docs/parity/fixtures/keys-tree-scoped-picker-checkpoints-2026-11-05.json",
            "Manifest must name the checkpoint evidence file.")
    validate_gates(manifest.get("gates") or {}, implementation)


def validate_gates(gates, implementation):
    for name in ("format", "build", "fullSuite", "exactHeadCi", "ledger"):
        gate = gates.get(name)
        require(isinstance(gate, dict), f"Manifest gate {name!r} is missing.")
        require(gate.get("status") == "pass", f"Manifest gate {name!r} is {gate.get('status')!r}.")
        require(gate.get("evidence"), f"Manifest gate {name!r} records no evidence.")
    require(gates["format"]["evidence"].startswith("dotnet format"),
            "Format gate must record the format command.")
    require(gates["build"]["evidence"].startswith("dotnet build"),
            "Build gate must record the build command.")
    suite = gates["fullSuite"]
    require(suite.get("failed") == 0 and suite.get("skipped") == 0,
            "Full-suite gate records failures or skips.")
    require((suite.get("passed") or 0) >= 4000, "Full-suite gate record is incomplete.")
    ci = gates["exactHeadCi"]
    require(len(ci.get("headSha") or "") == 40, "Exact-head CI must record a commit SHA.")
    require(str(ci.get("run") or "").isdigit(), "Exact-head CI must record a workflow run id.")
    if ci["headSha"] != git_output("rev-parse", "HEAD"):
        changed = git_output("diff", "--name-only", f"{ci['headSha']}..HEAD").splitlines()
        require(changed and all(path.startswith("docs/") for path in changed),
                "Exact-head CI commit must be HEAD or differ only in documentation.")
    require(gates["ledger"].get("entry") == "keys-tree-scoped-picker",
            "Ledger gate must name the keys-tree-scoped-picker entry.")


def validate_oracle(oracle, manifest, pi_checkout=None):
    require(oracle.get("capability") == "keys-tree-scoped-picker",
            "Oracle capability must be keys-tree-scoped-picker.")
    require(oracle.get("piSha") == manifest.get("piSha"),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get("scenarios") or {}
    require(len(scenarios) == manifest.get("oracleScenarioCount"),
            f"Oracle has {len(scenarios)} scenarios, manifest claims {manifest.get('oracleScenarioCount')}.")
    require(set(scenarios) == set(REQUIRED_CASES),
            f"Oracle scenarios must be {REQUIRED_CASES}, found {sorted(scenarios)}.")
    for name, scenario in scenarios.items():
        require(scenario.get("source"), f"Oracle scenario {name!r} has no source references.")
        require(scenario.get("expected"), f"Oracle scenario {name!r} has no expected observation.")
        require(scenario.get("description"), f"Oracle scenario {name!r} has no description.")
    pty = oracle.get("ptyScenarios") or {}
    require(set(pty) == set(CASE_IDS), f"Oracle PTY scenarios must cover {CASE_IDS}.")
    for case_id, details in pty.items():
        columns, rows = case_id.split("-")[0].split("x")
        require(details.get("columns") == int(columns) and details.get("rows") == int(rows),
                f"Oracle PTY scenario {case_id} dimensions disagree with its id.")
        require(details.get("theme") == "dark" and details.get("mode") in ("fullscreen", "regular"),
                f"Oracle PTY scenario {case_id} theme/mode is incomplete.")
    validate_oracle_sources(oracle, pi_checkout)
    return scenarios


def validate_oracle_sources(oracle, pi_checkout=None):
    require(oracle.get("sources"), "Oracle has no source inventory.")
    if pi_checkout is None:
        return
    checkout = Path(pi_checkout)
    require(checkout.is_dir(), f"Pinned Pi checkout {checkout} is missing.")
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=checkout, text=True).strip()
    require(actual == PI_SHA, f"Pinned Pi checkout is {actual}, expected {PI_SHA}.")
    for source in oracle["sources"]:
        path = checkout / source["path"]
        require(path.is_file(), f"Oracle source does not exist in pinned Pi: {source['path']}")
        text = path.read_text(encoding="utf-8")
        for anchor in source.get("anchors") or []:
            require(anchor in text, f"Oracle anchor missing from {source['path']}: {anchor!r}")


def validate_terminal_fixture(manifest):
    fixture = load_json(ROOT / manifest["terminalFixtures"][0])
    require(fixture.get("id") == FIXTURE_ID, "Terminal fixture id is wrong.")
    require(fixture.get("persistSessions") is True or
            "--session" in json.dumps(fixture.get("arguments", {})),
            "Terminal fixture must persist isolated sessions or pass --session.")
    actions = fixture.get("actions") or []
    ids = [action.get("id") for action in actions]
    required = ("tree-open", "tree-default", "tree-fold", "tree-unfold",
                "tree-filter-no-tools", "tree-filter-user-only", "tree-filter-labeled",
                "tree-filter-all", "tree-search", "tree-search-escape",
                "tree-label-open", "tree-label-save", "tree-label-timestamp",
                "tree-copy", "tree-copy-empty", "tree-goto-branch",
                "tree-navigate-prompt", "tree-navigate-resume",
                "tree-navigate-no-summary", "tree-already", "tree-user-navigate",
                "scoped-open", "scoped-initial", "scoped-search-gamma",
                "scoped-toggle-gamma", "scoped-clear-search", "scoped-toggle-alpha-off",
                "scoped-clear-alpha", "scoped-reorder", "scoped-reorder-top",
                "scoped-provider-toggle", "scoped-clear-all", "scoped-enable-all",
                "scoped-save", "scoped-close", "restart", "scoped-restart-open",
                "scoped-restart-search", "quit")
    for expected in required:
        require(expected in ids, f"Terminal fixture is missing action {expected!r}.")
    require({"fullscreen", "regular"} <= set(fixture.get("modes") or []),
            "Terminal fixture must exercise fullscreen and regular modes.")
    require(len(fixture.get("dimensions") or []) >= 2,
            "Terminal fixture must exercise at least two dimensions.")
    require(len(fixture.get("assertionFrames") or []) >= 32,
            "Terminal fixture must name at least 32 checkpoints.")
    settings = fixture.get("settings") or {}
    require(settings.get("enabledModels") == ["fixture/fixture-model", "fixture/alpha", "fixture/beta"],
            "Terminal fixture must start from the configured three-model scope.")
    clipboard = [action for action in actions if action.get("expectClipboardTexts")]
    require(clipboard and clipboard[0]["expectClipboardTexts"] == ["second answer"],
            "Terminal fixture must observe the tree clipboard write.")


def validate_checkpoints(checkpoints, manifest):
    require(checkpoints.get("capability") == "keys-tree-scoped-picker",
            "Checkpoint capability must be keys-tree-scoped-picker.")
    require(checkpoints.get("piSha") == manifest.get("piSha"),
            f"Checkpoint pin is {checkpoints.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    cases = checkpoints.get("cases") or {}
    require(set(cases) == set(CASE_IDS),
            f"Checkpoint cases must be {list(CASE_IDS)}, found {sorted(cases)}.")
    dimensions = {case_id.split("-")[0] for case_id in cases}
    modes = {case_id.split("-")[-1] for case_id in cases}
    require(len(dimensions) >= 2, f"Paired evidence only used dimensions {sorted(dimensions)}.")
    require({"fullscreen", "regular"} <= modes, f"Paired evidence modes are {sorted(modes)}.")
    index = {}
    by_case = {}
    for case_id, case in cases.items():
        require(case.get("dimensions") and case.get("theme") == "dark" and case.get("mode"),
                f"{case_id} checkpoint metadata is incomplete.")
        paired = case.get("paired") or {}
        calibration = case.get("calibration") or {}
        require(not paired.get("scenarioErrors"),
                f"{case_id} paired scenario errors: {paired.get('scenarioErrors')}")
        require(not calibration.get("scenarioErrors"),
                f"{case_id} calibration scenario errors: {calibration.get('scenarioErrors')}")
        require(calibration.get("semanticDifferences") == 0,
                f"{case_id} calibration has {calibration.get('semanticDifferences')} semantic differences.")
        require(calibration.get("httpMatch") is True, f"{case_id} calibration HTTP traces differ.")
        require(paired.get("semanticDifferences") == 0,
                f"{case_id} paired semantic differences: {paired.get('semanticFirstDifferences')}")
        require(paired.get("httpMatch") is True, f"{case_id} provider HTTP traces differ.")
        pi = case["pi"]["checkpoints"]
        pisharp = case["pisharp"]["checkpoints"]
        require(set(pi) == set(pisharp), f"{case_id} products captured different checkpoint ids.")
        require(len(pi) >= 70, f"{case_id} captured only {len(pi)} checkpoints.")
        for checkpoint_id in pi:
            require(pi[checkpoint_id] == pisharp[checkpoint_id],
                    f"{case_id}/{checkpoint_id} semantic checkpoint differs.")
            index[f"{FIXTURE_ID}/{case_id}/{checkpoint_id}"] = pi[checkpoint_id]
            by_case.setdefault(f"{FIXTURE_ID}/{case_id}", []).append(
                f"{FIXTURE_ID}/{case_id}/{checkpoint_id}")
        require(case["pi"]["requests"] == [] and case["pisharp"]["requests"] == [],
                f"{case_id} unexpectedly issued provider requests.")
        require(case["pi"]["settings"] == case["pisharp"]["settings"] ==
                {"shellCommandPrefix": "preserved",
                 "enabledModels": ["fixture/beta", "fixture/alpha"]},
                f"{case_id} persisted enabledModels differ.")
        require(case["pi"]["labels"] == case["pisharp"]["labels"],
                f"{case_id} persisted label trails differ.")
        require(case["pi"]["labels"] == [
            {"targetId": "a0000003", "label": "first label"},
            {"targetId": "c0000008", "label": "second answer label"},
            {"targetId": "c0000008", "label": "second answer label updated"},
        ], f"{case_id} label persistence trail differs.")
        require(case["pi"]["clipboard"].get("tree-copy") == ["second answer"],
                f"{case_id} pinned-Pi tree clipboard text differs.")
        require(case["pisharp"]["clipboard"].get("tree-copy") == ["second answer"],
                f"{case_id} PiSharp tree clipboard text differs.")
        require(not case["pisharp"]["clipboard"].get("tree-copy-empty"),
                f"{case_id} copy of a settings entry wrote clipboard text.")
        require(not case["pi"]["clipboard"].get("tree-copy-empty"),
                f"{case_id} pinned-Pi copy of a settings entry wrote clipboard text.")
        require(case["pisharp"]["sessionFiles"],
                f"{case_id} PiSharp did not persist a native session document.")
        validate_case_facts(case_id, pi)
    return index, by_case, dimensions, modes


def validate_case_facts(case_id, checkpoints):
    def tree(checkpoint):
        value = checkpoints[checkpoint]["tree"]
        require(value is not None, f"{case_id}/{checkpoint} did not capture the tree.")
        return value

    def scoped(checkpoint):
        value = checkpoints[checkpoint]["scoped"]
        require(value is not None, f"{case_id}/{checkpoint} did not capture the scoped selector.")
        return value

    default = tree("tree-default")
    require(default["rows"] == DEFAULT_ROWS and default["selected"] == "Z13" and
            default["count"] == "8/10", f"{case_id} default tree state differs: {default}")
    require(tree("tree-page-up")["selected"] == "U2" and
            tree("tree-page-up")["count"] == "1/10", f"{case_id} page-up selection differs.")
    require(tree("tree-page-down")["selected"] == "B5" and
            tree("tree-page-down")["count"] == "10/10", f"{case_id} page-down selection differs.")
    folded = tree("tree-fold")
    require(folded["folded"] == ["C7"] and folded["rows"] == ["U2", "A3", "C7", "B4", "B5"] and
            folded["count"] == "3/5", f"{case_id} fold state differs: {folded}")
    require(tree("tree-unfold")["folded"] == [] and
            tree("tree-unfold")["count"] == "3/10", f"{case_id} unfold state differs.")
    require(tree("tree-filter-no-tools")["rows"] == NO_TOOLS_ROWS and
            tree("tree-filter-no-tools")["tags"] == ["no-tools"],
            f"{case_id} no-tools filter differs.")
    require(tree("tree-filter-user-only")["rows"] == USER_ROWS and
            tree("tree-filter-user-only")["tags"] == ["user"],
            f"{case_id} user-only filter differs.")
    require(tree("tree-filter-labeled")["rows"] == LABELED_ROWS and
            tree("tree-filter-labeled")["tags"] == ["labeled"],
            f"{case_id} labeled-only filter differs.")
    require(tree("tree-filter-all")["rows"] == ALL_ROWS and
            tree("tree-filter-all")["tags"] == ["all"],
            f"{case_id} all filter differs.")
    require(tree("tree-search")["search"] == "second" and
            tree("tree-search")["rows"] == ["C7", "C8"],
            f"{case_id} search result differs.")
    require(tree("tree-search-escape")["search"] == "" and
            tree("tree-search-escape")["count"] == "4/10",
            f"{case_id} escape-with-search did not clear the query.")
    require("second answer label updated" in tree("tree-label-save")["renderedLabels"],
            f"{case_id} saved label is not rendered.")
    require(tree("tree-label-timestamp")["tags"] == ["+label time"] and
            tree("tree-label-timestamp-off")["tags"] == [],
            f"{case_id} label timestamp toggle differs.")
    require(tree("tree-goto-branch")["selected"] == "B5" and
            tree("tree-goto-branch")["count"] == "10/10",
            f"{case_id} branch target selection differs.")
    require(checkpoints["tree-navigate-prompt"]["editor"] == SUMMARY_OPTIONS,
            f"{case_id} branch summary prompt differs.")
    require(checkpoints["tree-navigate-resume"]["tree"]["selected"] == "B5",
            f"{case_id} escape from summary did not resume the tree at its selection.")
    require(checkpoints["tree-navigate-no-summary"]["tree"] is None and
            checkpoints["tree-navigate-no-summary"]["editor"] == [],
            f"{case_id} branch navigation did not close the tree.")
    require(checkpoints["tree-user-navigate"]["editor"] == ["first prompt"],
            f"{case_id} user-message navigation did not restore the prompt draft.")

    initial = scoped("scoped-initial")
    require(initial["rows"] == SCOPED_ALL and initial["enabled"] == SCOPED_INITIAL and
            initial["count"] == "3/4" and initial["unsaved"] is False,
            f"{case_id} scoped initial state differs: {initial}")
    require(scoped("scoped-search-gamma")["rows"] == ["gamma"] and
            scoped("scoped-search-gamma")["selected"] == "gamma",
            f"{case_id} scoped search differs.")
    require(scoped("scoped-toggle-gamma")["count"] == "all enabled" and
            scoped("scoped-toggle-gamma")["unsaved"] is True,
            f"{case_id} toggling the last disabled model did not normalize to all enabled.")
    require(scoped("scoped-clear-search")["rows"] == SCOPED_ALL and
            scoped("scoped-clear-search")["enabled"] == SCOPED_ALL,
            f"{case_id} Ctrl+C did not clear the scoped search.")
    require(scoped("scoped-clear-alpha")["rows"] == ["fixture-model", "beta", "gamma", "alpha"] and
            scoped("scoped-clear-alpha")["enabled"] == ["fixture-model", "beta", "gamma"],
            f"{case_id} disabling a model did not preserve explicit order.")
    require(scoped("scoped-reorder")["enabled"] == ["fixture-model", "gamma", "beta"] and
            scoped("scoped-reorder")["selected"] == "gamma",
            f"{case_id} reorder up did not move the selected model.")
    require(scoped("scoped-reorder-top")["enabled"] == ["gamma", "fixture-model", "beta"] and
            scoped("scoped-reorder-top")["selected"] == "gamma",
            f"{case_id} second reorder up did not reach the top.")
    require(scoped("scoped-provider-toggle")["enabled"] == SCOPED_ALL and
            scoped("scoped-provider-toggle")["count"] == "all enabled",
            f"{case_id} provider toggle did not enable the provider.")
    require(scoped("scoped-clear-all")["enabled"] == [] and
            scoped("scoped-clear-all")["count"] == "0/4",
            f"{case_id} clear-all did not disable every model.")
    require(scoped("scoped-enable-all")["enabled"] == SCOPED_ALL,
            f"{case_id} enable-all did not restore every model.")
    require(scoped("scoped-save-toggle-gamma")["count"] == "3/4",
            f"{case_id} search-scoped toggle count differs.")
    require(scoped("scoped-save-clear")["enabled"] == SCOPED_INITIAL,
            f"{case_id} clearing the search lost the explicit scope.")
    require(scoped("scoped-save-clear2")["enabled"] == ["alpha", "beta"],
            f"{case_id} toggling a model off did not update the explicit list.")
    saved = scoped("scoped-save")
    require(saved["enabled"] == ["beta", "alpha"] and saved["count"] == "2/4" and
            saved["unsaved"] is False, f"{case_id} save state differs: {saved}")
    restarted = scoped("scoped-restart-open")
    require(restarted["enabled"] == ["beta", "alpha"] and restarted["selected"] == "beta" and
            restarted["count"] == "2/4", f"{case_id} saved scope did not survive restart.")
    require(scoped("scoped-restart-search")["rows"] == ["beta"],
            f"{case_id} restarted selector search differs.")
    require(checkpoints["quit"]["activeScreen"] == "normal",
            f"{case_id} did not restore the normal terminal screen.")


def validate_case_references(manifest, oracle, results, index, by_case):
    tests = manifest["requiredTests"]
    allowed_tests = set(tests["facts"])
    passing = {name for name, outcome in results if outcome == "Passed"}
    scenario_names = set(oracle["scenarios"])
    referenced_tests = set()
    for case in manifest["cases"]:
        for name in case.get("oracle") or []:
            require(name in scenario_names,
                    f"{case['id']} references a nonexistent oracle scenario: {name!r}.")
        for method in case.get("tests") or []:
            require(method in allowed_tests,
                    f"{case['id']} references a test outside requiredTests: {method!r}.")
            require(any(name == method or name.startswith(method + "(") for name in passing),
                    f"{case['id']} references a test that did not pass: {method!r}.")
            referenced_tests.add(method)
        for checkpoint in case.get("ptyCheckpoints") or []:
            require(resolve_checkpoint(checkpoint, index, by_case),
                    f"{case['id']} references a missing terminal checkpoint: {checkpoint!r}.")
    require(referenced_tests == allowed_tests,
            f"Required facts are not referenced by any case: {sorted(allowed_tests - referenced_tests)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in (case.get("oracle") or [])}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced by any case: {sorted(scenario_names - referenced_scenarios)}")
    referenced_evidence = set(tests.get("ptyEvidence") or [])
    require(referenced_evidence <= set(by_case),
            f"PTY evidence ids are not checkpoint cases: {sorted(referenced_evidence - set(by_case))}")
    for dimension in ("100x30", "140x40"):
        require(any(entry.startswith(f"{FIXTURE_ID}/{dimension}-") for entry in index),
                f"Evidence has no {dimension} checkpoint.")
    for mode in ("fullscreen", "regular"):
        require(any(entry.startswith(f"{FIXTURE_ID}/") and f"-{mode}/" in entry for entry in index),
                f"Evidence has no {mode} checkpoint.")


def validate_oracle_pty_scenarios(oracle, index, by_case):
    for case_id in oracle["ptyScenarios"]:
        prefix = f"{FIXTURE_ID}/{case_id}"
        require(by_case.get(prefix), f"Oracle PTY scenario {case_id} has no checkpoint references.")


def resolve_checkpoint(reference, index, by_case):
    if reference in index:
        return [reference]
    matches = by_case.get(reference, [])
    require(matches, f"Nonexistent terminal checkpoint reference: {reference!r}.")
    return sorted(matches)


def trx_results(path):
    try:
        tree = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise VerificationError(f"Cannot read TRX {path}: {error}") from error
    results = []
    for element in tree.getroot().iter(
            "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
        results.append((element.get("testName") or "", element.get("outcome") or ""))
    require(results, f"TRX {path} contains no test results.")
    return results


def validate_trx(results, manifest):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    for method in manifest["requiredTests"]["facts"]:
        matching = [(name, outcome) for name, outcome in results
                    if name == method or name.startswith(method + "(")]
        require(matching, f"Required test did not run: {method}")
        for name, outcome in matching:
            require(outcome not in skipped, f"Required test was skipped: {name} ({outcome}).")
            require(outcome == "Passed", f"Required test failed: {name} ({outcome}).")


def validate_full_trx(results):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    failures = [name for name, outcome in results if outcome != "Passed" and outcome not in skipped]
    skips = [name for name, outcome in results if outcome in skipped]
    require(not failures, f"Full suite has failing tests: {failures[:5]}")
    require(not skips, f"Full suite has skipped tests: {skips[:5]}")


def validate_ledger(ledger, manifest):
    entry = ledger.get("keys-tree-scoped-picker")
    require(isinstance(entry, dict), "Execution ledger has no keys-tree-scoped-picker entry.")
    require(entry.get("status") in ("verified", "complete"),
            f"Ledger entry status is {entry.get('status')!r}.")
    require(entry.get("implementation"), "Ledger entry has no implementation references.")
    evidence = entry.get("evidence") or []
    for required in (manifest["oracleFile"], manifest["checkpointFile"],
                     "docs/parity/fixtures/keys-tree-scoped-picker-acceptance-2026-11-05.json"):
        require(any(required in value for value in evidence),
                f"Ledger entry does not reference {required}.")


def run(command, env=None, timeout=3600):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-4000:] + result.stderr[-4000:]).strip()
        raise VerificationError(f"Command failed: {' '.join(command)}\n{tail}")
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-tree-scoped-verify-")
    trx = Path(directory) / "tree-scoped.trx"
    environment = dict(os.environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~KeysTreeScopedPickerTests",
        "FullyQualifiedName~TerminalPtyTests.ConversationTreeSearchSelectsAndPersistsABranchThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.ScopedModelsSelectorAppliesSessionEditsAndPersistsReorderedScopeThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.ScopedModelsSelectorAddsModelsDiscoveredWhileOpenThroughLinuxPty",
        "FullyQualifiedName~AppControlPtyTests.DoubleEscapeHonoursTheConfiguredActionAndReturnsToTheEditor",
        "FullyQualifiedName~AppControlPtyTests.RegularModeDoubleEscapeAfterBashEntryOpensTheTreeSelectorThroughLinuxPty",
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=tree-scoped.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx


def run_quality():
    run(["dotnet", "format", "PiSharp.slnx", "--verify-no-changes", "--no-restore"])
    run(["dotnet", "build", "PiSharp.slnx", "--warnaserror", "--nologo"])


def self_test(manifest, oracle, checkpoints, results, ledger):
    def rejects(call, label):
        try:
            call()
        except VerificationError as error:
            print(f"negative control rejected: {label}: {error}")
            return
        raise VerificationError(f"Negative control accepted an invalid case: {label}")

    implementation = manifest["implementationSha"]
    validate_manifest(manifest, implementation)
    index, by_case, _, _ = validate_checkpoints(checkpoints, manifest)
    passing = [(name, "Passed") for name in manifest["requiredTests"]["facts"]]

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "S07"]
    rejects(lambda: validate_manifest(missing, implementation), "missing matrix case")

    duplicate = copy.deepcopy(manifest)
    duplicate["cases"].append(copy.deepcopy(duplicate["cases"][0]))
    rejects(lambda: validate_manifest(duplicate, implementation), "duplicate matrix case")

    failed = copy.deepcopy(manifest)
    failed["cases"][0]["status"] = "failed"
    rejects(lambda: validate_manifest(failed, implementation), "failed matrix case")

    skipped_case = copy.deepcopy(manifest)
    skipped_case["cases"][1]["status"] = "skip"
    rejects(lambda: validate_manifest(skipped_case, implementation), "skipped matrix case")

    stale = copy.deepcopy(manifest)
    stale["implementationSha"] = "0" * 40
    rejects(lambda: validate_manifest(stale, implementation), "stale implementation SHA")

    wrong_pi = copy.deepcopy(manifest)
    wrong_pi["piSha"] = "1" * 40
    rejects(lambda: validate_manifest(wrong_pi, implementation), "incorrect Pi SHA")

    unresolved = copy.deepcopy(manifest)
    unresolved["requiredComparisons"][0]["status"] = "differs"
    rejects(lambda: validate_manifest(unresolved, implementation), "unresolved required comparison")

    failed_gate = copy.deepcopy(manifest)
    failed_gate["gates"]["format"]["status"] = "failed"
    rejects(lambda: validate_manifest(failed_gate, implementation), "failed quality gate")

    skipped_suite = copy.deepcopy(manifest)
    skipped_suite["gates"]["fullSuite"]["skipped"] = 1
    rejects(lambda: validate_manifest(skipped_suite, implementation), "skipped full-suite test")

    bad_ci = copy.deepcopy(manifest)
    bad_ci["gates"]["exactHeadCi"]["headSha"] = "0" * 40
    rejects(lambda: validate_manifest(bad_ci, implementation), "wrong exact-head CI commit")

    bad_fixture = copy.deepcopy(manifest)
    bad_fixture["terminalFixtures"] = ["tools/parity/terminal/fixtures/missing.json"]
    rejects(lambda: validate_terminal_fixture(bad_fixture), "missing terminal fixture")

    bad_oracle = copy.deepcopy(oracle)
    bad_oracle["scenarios"]["T01"].pop("expected")
    rejects(lambda: validate_oracle(bad_oracle, manifest), "incomplete oracle scenario")

    bad_sources = copy.deepcopy(oracle)
    bad_sources["sources"] = []
    rejects(lambda: validate_oracle_sources(bad_sources), "missing oracle source inventory")

    bad_case_ref = copy.deepcopy(manifest)
    bad_case_ref["cases"][0]["ptyCheckpoints"] = [
        "terminal-keys-tree-scoped-picker/does-not-exist/startup"]
    rejects(lambda: validate_case_references(bad_case_ref, oracle, passing, index, by_case),
            "nonexistent checkpoint reference")

    bad_oracle_ref = copy.deepcopy(manifest)
    bad_oracle_ref["cases"][0]["oracle"] = ["nonexistent oracle scenario"]
    rejects(lambda: validate_case_references(bad_oracle_ref, oracle, passing, index, by_case),
            "nonexistent oracle reference")

    bad_test_ref = copy.deepcopy(manifest)
    bad_test_ref["cases"][0]["tests"] = ["PiSharp.Tests.KeysTreeScopedPickerTests.DoesNotExist"]
    rejects(lambda: validate_case_references(bad_test_ref, oracle, passing, index, by_case),
            "nonexistent test reference")

    def mutate_checkpoint(case_id, checkpoint, mutate, label):
        bad = copy.deepcopy(checkpoints)
        mutate(bad["cases"][case_id]["pisharp"]["checkpoints"][checkpoint])
        rejects(lambda: validate_checkpoints(bad, manifest), label)

    mutate_checkpoint(CASE_IDS[0], "tree-default",
                      lambda value: value["tree"].__setitem__("rows", ["U2"]),
                      "incorrect tree rows")
    mutate_checkpoint(CASE_IDS[0], "tree-fold",
                      lambda value: value["tree"].__setitem__("folded", []),
                      "lost folded branch")
    mutate_checkpoint(CASE_IDS[0], "tree-filter-all",
                      lambda value: value["tree"].__setitem__("tags", []),
                      "lost filter tag")
    mutate_checkpoint(CASE_IDS[0], "tree-label-save",
                      lambda value: value["tree"].__setitem__("renderedLabels", ["first label"]),
                      "lost saved label")
    mutate_checkpoint(CASE_IDS[0], "scoped-clear-alpha",
                      lambda value: value["scoped"].__setitem__("enabled", ["alpha"]),
                      "incorrect scoped enabled order")
    mutate_checkpoint(CASE_IDS[0], "scoped-save",
                      lambda value: value["scoped"].__setitem__("unsaved", True),
                      "saved scope still marked unsaved")
    mutate_checkpoint(CASE_IDS[0], "quit",
                      lambda value: value.__setitem__("activeScreen", "alternate"),
                      "terminal screen not restored")
    mutate_checkpoint(CASE_IDS[0], "tree-copy",
                      lambda value: value["clipboard"].__setitem__("tree-copy", []),
                      "lost tree clipboard write")

    bad_settings = copy.deepcopy(checkpoints)
    bad_settings["cases"][CASE_IDS[0]]["pisharp"]["settings"]["enabledModels"] = ["fixture/alpha"]
    rejects(lambda: validate_checkpoints(bad_settings, manifest), "wrong persisted enabledModels")

    bad_labels = copy.deepcopy(checkpoints)
    bad_labels["cases"][CASE_IDS[0]]["pisharp"]["labels"] = []
    rejects(lambda: validate_checkpoints(bad_labels, manifest), "lost label persistence")

    bad_paired = copy.deepcopy(checkpoints)
    bad_paired["cases"][CASE_IDS[0]]["paired"]["semanticDifferences"] = 1
    rejects(lambda: validate_checkpoints(bad_paired, manifest), "unresolved paired difference")

    bad_calibration = copy.deepcopy(checkpoints)
    bad_calibration["cases"][CASE_IDS[0]]["calibration"]["semanticDifferences"] = 1
    rejects(lambda: validate_checkpoints(bad_calibration, manifest), "failed same-Pi calibration")

    bad_calibration_error = copy.deepcopy(checkpoints)
    bad_calibration_error["cases"][CASE_IDS[0]]["calibration"]["scenarioErrors"] = {"pi": "timeout"}
    rejects(lambda: validate_checkpoints(bad_calibration_error, manifest),
            "calibration scenario error")

    bad_pty = copy.deepcopy(checkpoints)
    del bad_pty["cases"][CASE_IDS[0]]
    rejects(lambda: validate_checkpoints(bad_pty, manifest), "missing PTY evidence")

    bad_requests = copy.deepcopy(checkpoints)
    bad_requests["cases"][CASE_IDS[0]]["pisharp"]["requests"] = ["prompt"]
    rejects(lambda: validate_checkpoints(bad_requests, manifest), "unexpected provider request")

    failed_trx = [(name, "Failed" if name.startswith(manifest["requiredTests"]["facts"][0])
                   else outcome) for name, outcome in results]
    rejects(lambda: validate_trx(failed_trx, manifest), "failed focused test")

    skipped_trx = [(name, "NotExecuted" if name.startswith(manifest["requiredTests"]["facts"][1])
                    else outcome) for name, outcome in results]
    rejects(lambda: validate_trx(skipped_trx, manifest), "skipped focused test")

    failed_full = results + [("PiSharp.Tests.SomeTest", "Failed")]
    rejects(lambda: validate_full_trx(failed_full), "failed full suite")

    skipped_full = results + [("PiSharp.Tests.SomeTest", "Skipped")]
    rejects(lambda: validate_full_trx(skipped_full), "skipped full suite")

    bad_ledger = copy.deepcopy(ledger)
    bad_ledger["keys-tree-scoped-picker"]["status"] = "not_started"
    rejects(lambda: validate_ledger(bad_ledger, manifest), "ledger entry not completed")

    bad_ledger_evidence = copy.deepcopy(ledger)
    bad_ledger_evidence["keys-tree-scoped-picker"]["evidence"] = []
    rejects(lambda: validate_ledger(bad_ledger_evidence, manifest), "ledger evidence missing")

    print("negative controls passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-tree-scoped-picker-acceptance-2026-11-05.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-tree-scoped-picker-pi-oracle-2026-11-05.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-tree-scoped-picker-checkpoints-2026-11-05.json")
    parser.add_argument("--ledger", type=Path, default=ROOT / "docs/parity/execution-ledger.json")
    parser.add_argument("--implementation")
    parser.add_argument("--pi", type=Path)
    parser.add_argument("--trx", type=Path)
    parser.add_argument("--full-trx", type=Path)
    parser.add_argument("--skip-tests", action="store_true")
    parser.add_argument("--quality", action="store_true",
                        help="Run dotnet format verification and warnings-as-errors build")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    implementation = args.implementation or implementation_sha()
    latest = implementation_sha()
    if implementation != latest:
        raise VerificationError(
            f"Stale implementation SHA: requested {implementation!r}, latest src/tests commit is {latest!r}.")
    manifest = load_json(args.manifest)
    oracle = load_json(args.oracle)
    checkpoints = load_json(args.checkpoints)
    ledger = load_json(args.ledger)
    if args.self_test:
        manifest["implementationSha"] = implementation
        if args.trx is None:
            args.trx = run_focused_tests(implementation)
        results = trx_results(args.trx)
        self_test(manifest, oracle, checkpoints, results, ledger)
        return
    validate_manifest(manifest, implementation)
    validate_oracle(oracle, manifest, args.pi)
    validate_terminal_fixture(manifest)
    validate_ledger(ledger, manifest)
    if args.quality:
        run_quality()
    if args.pi:
        run(["python3", "tools/parity/pi_reference.py", "verify", "--pi", str(args.pi)])
    index, by_case, dimensions, modes = validate_checkpoints(checkpoints, manifest)
    validate_oracle_pty_scenarios(oracle, index, by_case)
    if args.full_trx:
        validate_full_trx(trx_results(args.full_trx))
    if args.trx:
        trx = args.trx
    elif args.skip_tests:
        raise VerificationError("--skip-tests requires --trx; required executable evidence is missing.")
    else:
        trx = run_focused_tests(implementation)
    results = trx_results(trx)
    validate_trx(results, manifest)
    validate_case_references(manifest, oracle, results, index, by_case)
    print(f"keys-tree-scoped-picker verified at {implementation}: {len(manifest['cases'])} cases, "
          f"{len(oracle['scenarios'])} pinned-Pi scenarios, {len(checkpoints['cases'])} paired cases, "
          f"{len(index)} captured checkpoints, dimensions {sorted(dimensions)}, modes {sorted(modes)}.")


if __name__ == "__main__":
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
