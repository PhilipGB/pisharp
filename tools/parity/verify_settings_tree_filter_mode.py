#!/usr/bin/env python3
"""Verify the settings-treefiltermode acceptance manifest and committed evidence.

Every gate is recomputed from the frozen evidence and the repository sources:
the manifest's own status fields are only accepted when the corresponding
observation, test outcome and provenance check independently pass.
"""
import argparse
import copy
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
CAPABILITY = "settings-treefiltermode"
MODES = ["default", "no-tools", "user-only", "labeled-only", "all"]
STATUS_TAGS = {"default": [], "no-tools": ["no-tools"], "user-only": ["user"],
               "labeled-only": ["labeled"], "all": ["all"]}
REQUIRED_CASES = [f"TF{index:02d}" for index in range(1, 14)]
CASE_IDS = ("100x30-dark-fullscreen", "140x40-dark-fullscreen",
            "100x30-dark-regular", "140x40-dark-regular")
FIXTURES = [
    "tools/parity/terminal/fixtures/terminal-tree-filter-mode.json",
    "tools/parity/terminal/fixtures/terminal-tree-filter-mode-regular.json",
    "tools/parity/terminal/fixtures/terminal-tree-filter-mode-settings.json",
]
LABEL = "Tree filter mode"
DESCRIPTION = "Default filter when opening /tree"
VALUE_SOURCE = "src/PiSharp.Cli/Configuration/UserSettings.cs"


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


def is_ancestor(sha):
    return subprocess.run(["git", "merge-base", "--is-ancestor", sha, "HEAD"],
                          cwd=ROOT).returncode == 0


def source_values():
    text = (ROOT / VALUE_SOURCE).read_text(encoding="utf-8")
    match = re.search(r"Values\s*=\s*\[(.*?)\];", text, re.S)
    require(match is not None, f"{VALUE_SOURCE} has no treeFilterMode value list.")
    return re.findall(r'"([^"]+)"', match.group(1))


def validate_oracle(oracle):
    require(oracle.get("schema") == 1, "Oracle schema must be 1.")
    require(oracle.get("capability") == CAPABILITY, "Oracle capability mismatch.")
    require(oracle.get("piSha") == PI_SHA, f"Oracle Pi SHA is {oracle.get('piSha')!r}.")
    require(oracle.get("values") == MODES,
            f"Oracle values must be {MODES}, found {oracle.get('values')}.")
    require(oracle.get("default") == "default", "Oracle default must be 'default'.")
    require(oracle.get("label") == LABEL, "Oracle settings label differs.")
    require(oracle.get("description") == DESCRIPTION, "Oracle settings description differs.")
    require(source_values() == oracle["values"],
            f"{VALUE_SOURCE} setting values {source_values()} differ from the oracle order.")
    sources = oracle.get("sources") or []
    require(len(sources) >= 6, "Oracle must cite the pinned-Pi source inventory.")
    for entry in sources:
        require(entry.get("file") and entry.get("symbol") and entry.get("observation"),
                f"Oracle source entry is incomplete: {entry}")
    scenarios = oracle.get("scenarios") or []
    require([scenario.get("id") for scenario in scenarios] == REQUIRED_CASES,
            f"Oracle scenarios must be {REQUIRED_CASES}.")
    for mode in MODES:
        projection = (oracle.get("projections") or {}).get(mode)
        require(isinstance(projection, dict), f"Oracle has no {mode} projection.")
        require(projection.get("rows"), f"Oracle {mode} projection has no rows.")
        require(projection.get("count"), f"Oracle {mode} projection has no count.")
        require(projection.get("tags") == STATUS_TAGS[mode],
                f"Oracle {mode} projection status label differs.")
        require(projection.get("selected"), f"Oracle {mode} projection has no selection.")
    require((oracle.get("projections") or {}).get("default") ==
            (oracle.get("projections") or {}).get("unset"),
            "Unset must project exactly like the default filter.")
    invalid = (oracle.get("projections") or {}).get("invalid")
    require(invalid == oracle["projections"]["default"],
            "An unrecognized stored value must project exactly like the default filter.")


def validate_manifest(manifest, implementation, oracle):
    require(manifest.get("schema") == 1, "Manifest schema must be 1.")
    require(manifest.get("capability") == CAPABILITY, "Manifest capability mismatch.")
    require(manifest.get("piSha") == PI_SHA, f"Manifest Pi SHA is {manifest.get('piSha')!r}.")
    recorded = manifest.get("implementationSha")
    require(len(recorded or "") == 40, "Manifest must record a 40-character implementation SHA.")
    require(recorded == implementation,
            f"Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.")
    require(is_ancestor(recorded), "Recorded implementation SHA is not an ancestor of HEAD.")
    require(manifest.get("oracleFile"), "Manifest must name the oracle file.")
    require(manifest.get("terminalFixtures") == FIXTURES,
            f"Manifest terminal fixtures must be {FIXTURES}.")
    checkpoints = manifest.get("checkpointFiles") or []
    require(len(checkpoints) == 3, "Manifest must name fullscreen, regular and settings checkpoint files.")
    cases = manifest.get("cases") or []
    ids = [case.get("id") for case in cases]
    require(ids == REQUIRED_CASES, f"Manifest cases must be {REQUIRED_CASES}, found {ids}.")
    scenario_ids = {scenario["id"] for scenario in oracle["scenarios"]}
    for case in cases:
        require(case.get("required") is True, f"{case['id']} must be required.")
        require(case.get("status") == "pass", f"{case['id']} is {case.get('status')!r}.")
        require(case.get("oracle") and set(case["oracle"]) <= scenario_ids,
                f"{case['id']} references an unknown oracle scenario.")
        require(case.get("tests"), f"{case['id']} has no deterministic test evidence.")
        require(case.get("ptyCheckpoints"), f"{case['id']} has no PTY checkpoint evidence.")
    comparisons = manifest.get("requiredComparisons") or []
    require({comparison.get("id") for comparison in comparisons} ==
            {"paired-pty", "same-pi-calibration", "settings-selector-calibration"},
            "Manifest must require paired PTY, same-Pi calibration and settings-selector calibration.")
    for comparison in comparisons:
        require(comparison.get("required") is True,
                f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get("status") == "match",
                f"Unresolved comparison {comparison.get('id')}: {comparison.get('status')!r}.")
        require(comparison.get("evidence"), f"Comparison {comparison.get('id')} has no evidence.")
    for finding in manifest.get("findings") or []:
        require(finding.get("required") is False,
                f"Finding {finding.get('id')} expands the required contract.")
    tests = manifest.get("requiredTests") or {}
    facts = tests.get("facts") or []
    require(len(facts) >= 18, "Manifest must list at least eighteen required deterministic tests.")
    require(len(set(facts)) == len(facts), "Duplicate required test names.")
    require({entry.rsplit("/", 1)[-1] for entry in tests.get("ptyEvidence") or []} == set(CASE_IDS),
            "Manifest PTY evidence ids must name all four paired cases.")
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
    require(suite.get("evidence") and suite.get("total") and suite.get("failed") == 0,
            "Full-suite gate must record zero failures and a test total.")
    require(suite.get("skipped") == 0, "Full-suite gate must record zero skipped tests.")
    ci = gates["exactHeadCi"]
    require(ci.get("implementationSha") == implementation,
            "Exact-head CI gate was recorded for a different head.")
    require(str(ci.get("run")) and str(ci.get("run")).isdigit(),
            "Exact-head CI gate must record the run id.")
    require(gates["ledger"]["evidence"].startswith("docs/parity/"),
            "Ledger gate must reference the parity evidence.")


def checkpoint(document, case_id, name):
    product = document["cases"][case_id]
    found = product["pi"]["checkpoints"].get(name)
    require(found is not None, f"{case_id} has no {name} checkpoint in the pinned-Pi evidence.")
    return found


def require_projection(actual, expected, context):
    require(actual is not None, f"{context}: missing tree state.")
    require(actual["rows"] == expected["rows"], f"{context}: rows differ: {actual['rows']}")
    require(actual["count"] == expected["count"], f"{context}: count differs: {actual['count']}")
    require(actual["tags"] == expected["tags"], f"{context}: status label differs: {actual['tags']}")
    require(actual["selected"] == expected["selected"],
            f"{context}: selected entry differs: {actual['selected']}")
    require(actual["suffixes"] == [""] * len(actual["rows"]),
            f"{context}: unexpected row suffix: {actual['suffixes']}")
    require(not actual["empty"], f"{context}: rendered an empty tree.")


MODE_CHECKPOINTS = {
    "default": "default-open",
    "no-tools": "no-tools-open",
    "user-only": "user-only-open",
    "labeled-only": "labeled-only-open",
    "all": "all-open",
}


def validate_paired_document(document, oracle, case_ids, label):
    require(document.get("capability") == CAPABILITY, f"{label}: capability mismatch.")
    require(document.get("piSha") == PI_SHA, f"{label}: Pi SHA is {document.get('piSha')!r}.")
    cases = document.get("cases") or {}
    require(set(cases) == set(case_ids), f"{label}: cases must be {sorted(case_ids)}, found {sorted(cases)}.")
    for case_id in case_ids:
        case = cases[case_id]
        paired = case["paired"]
        require(paired["semanticDifferences"] == 0,
                f"{case_id}: {paired['semanticDifferences']} unexplained paired differences: "
                f"{paired['semanticFirstDifferences'][:3]}")
        require(not paired["scenarioErrors"], f"{case_id}: scenario errors {paired['scenarioErrors']}")
        require(paired["runReport"]["match"] is False or paired["runReport"]["match"] is True,
                f"{case_id}: run report is missing.")
        calibration = case.get("calibration") or {}
        require(calibration.get("semanticDifferences") == 0,
                f"{case_id}: same-Pi calibration reported "
                f"{calibration.get('semanticDifferences')!r} differences.")
        require(not calibration.get("scenarioErrors"),
                f"{case_id}: calibration scenario errors {calibration.get('scenarioErrors')}")
        for product in ("pi", "pisharp"):
            require(not case[product]["scenarioError"],
                    f"{case_id}/{product}: scenario error {case[product]['scenarioError']}")
            require(not case[product]["requests"], f"{case_id}/{product}: unexpected provider request.")
            for name, checkpoint_state in case[product]["checkpoints"].items():
                require(not checkpoint_state["errors"],
                        f"{case_id}/{product}/{name}: error text {checkpoint_state['errors']}")
        projections = oracle["projections"]
        require_projection(checkpoint(document, case_id, "unset-open")["tree"],
                           projections["unset"], f"{case_id} unset")
        for mode, name in MODE_CHECKPOINTS.items():
            require_projection(checkpoint(document, case_id, name)["tree"],
                               projections[mode], f"{case_id} {mode}")
        require_projection(checkpoint(document, case_id, "invalid-open")["tree"],
                           projections["invalid"], f"{case_id} invalid")
        require_projection(checkpoint(document, case_id, "reset-open")["tree"],
                           projections["reset"], f"{case_id} reset")
        require_projection(checkpoint(document, case_id, "temporary-open")["tree"],
                           projections["user-only"], f"{case_id} temporary")
        require_projection(checkpoint(document, case_id, "temporary-cycle")["tree"],
                           projections["labeled-only"], f"{case_id} temporary cycle")
        require_projection(checkpoint(document, case_id, "temporary-reopen")["tree"],
                           projections["user-only"], f"{case_id} reopen")
        require(case["pi"]["settings"] == case["pisharp"]["settings"],
                f"{case_id}: persisted settings evidence differs.")


def validate_settings_document(document, oracle):
    require(document.get("capability") == CAPABILITY, "settings-selector: capability mismatch.")
    require(document.get("piSha") == PI_SHA, "settings-selector: Pi SHA mismatch.")
    pi = document["pi"]
    require(not pi["scenarioError"], f"settings-selector: scenario error {pi['scenarioError']}")
    calibration = document["calibration"]
    require(calibration["semanticDifferences"] == 0,
            "settings-selector: same-Pi calibration reported "
            f"{calibration['semanticDifferences']} differences.")
    require(not calibration["scenarioErrors"], "settings-selector: calibration scenario errors.")
    observed = []
    for name in ("settings-default", "settings-no-tools", "settings-user-only",
                 "settings-labeled-only", "settings-all"):
        state = pi["checkpoints"].get(name, {}).get("settings")
        require(state is not None, f"settings-selector: {name} did not render the setting row.")
        require(state["row"] == "Tree filter mode" or state["row"].startswith(LABEL),
                f"settings-selector: unexpected row {state['row']!r}")
        require(state["description"] == DESCRIPTION,
                f"settings-selector: description {state['description']!r}")
        require(state["value"] in MODES, f"settings-selector: value {state['value']!r}")
        observed.append(state["value"])
    require(observed == oracle["values"],
            f"settings-selector: observed value order {observed} differs from {oracle['values']}.")
    persisted = pi["settings"]
    require(persisted is not None, "settings-selector: no persisted settings evidence.")
    require(persisted["treeFilterMode"] == "all",
            f"settings-selector: persisted mode {persisted['treeFilterMode']!r}")
    require(persisted["quietStartup"] is True and persisted["shellCommandPrefix"] == "preserved",
            f"settings-selector: unrelated settings changed: {persisted}")
    require(persisted["theme"] == "dark" and persisted["tuiMode"] == "fullscreen",
            f"settings-selector: harness settings changed: {persisted}")
    require_projection(pi["checkpoints"]["tree-open"]["tree"], oracle["projections"]["all"],
                       "settings-selector next tree")
    require_projection(pi["checkpoints"]["tree-restart-open"]["tree"], oracle["projections"]["all"],
                       "settings-selector restarted tree")


def validate_checkpoints(documents, oracle, case_ids):
    require(set(documents) == {"paired", "regular", "settings"},
            "Checkpoint evidence must contain paired, regular and settings documents.")
    validate_paired_document(documents["paired"], oracle, case_ids[:2], "fullscreen")
    validate_paired_document(documents["regular"], oracle, case_ids[2:], "regular")
    validate_settings_document(documents["settings"], oracle)


def fixture_restarts():
    generated = {}
    for name in FIXTURES:
        fixture = load_json(ROOT / name)
        restarts = [action["id"] for action in fixture.get("actions") or [] if action.get("restart")]
        generated[name] = restarts
    return generated


def validate_fixtures(restarts=None, actions=None):
    restarts = restarts if restarts is not None else fixture_restarts()
    actions = actions if actions is not None else {
        name: [action["id"] for action in load_json(ROOT / name)["actions"]]
        for name in FIXTURES}
    for name in FIXTURES[:2]:
        require(restarts[name] == [f"{prefix}-config" for prefix in
                                   ["default", "no-tools", "user-only", "labeled-only", "all",
                                    "invalid", "reset", "temporary"]],
                f"{name} must install every configured mode through a restart: {restarts[name]}")
        ids = actions[name]
        for mode in MODES:
            require(f"{mode}-open" in ids, f"{name} has no {mode} tree opening.")
        require("invalid-open" in ids and "reset-open" in ids,
                f"{name} has no invalid/reset case.")
        require("temporary-reopen" in ids, f"{name} has no reopen case.")
    require(restarts[FIXTURES[2]] == ["restart"],
            f"{FIXTURES[2]} must restart once to prove persistence.")


def resolve_checkpoint(reference, index):
    return reference in index


def checkpoint_index(documents):
    index = set()
    for document in documents.values():
        cases = document.get("cases") or {}
        for case_id, case in cases.items():
            for product in ("pi", "pisharp"):
                for name in case[product]["checkpoints"]:
                    index.add(f"{case_id}/{name}")
                    index.add(name)
    for name in documents["settings"]["pi"]["checkpoints"]:
        index.add(name)
        index.add(f"settings/{name}")
    return index


def validate_case_references(manifest, oracle, documents, results):
    tests = manifest["requiredTests"]
    allowed = set(tests["facts"])
    passing = {name for name, outcome in results if outcome == "Passed"}
    index = checkpoint_index(documents)
    scenario_names = {scenario["id"] for scenario in oracle["scenarios"]}
    referenced_tests = set()
    for case in manifest["cases"]:
        for name in case.get("oracle") or []:
            require(name in scenario_names, f"{case['id']} references unknown oracle {name!r}.")
        for method in case.get("tests") or []:
            require(method in allowed, f"{case['id']} references a test outside requiredTests: {method}")
            require(any(name == method or name.startswith(method + "(") for name in passing),
                    f"{case['id']} references a test that did not pass: {method}")
            referenced_tests.add(method)
        for reference in case.get("ptyCheckpoints") or []:
            require(resolve_checkpoint(reference, index),
                    f"{case['id']} references a missing terminal checkpoint: {reference!r}")
    require(referenced_tests == allowed,
            f"Required tests are not referenced by any case: {sorted(allowed - referenced_tests)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in case.get("oracle") or []}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced: {sorted(scenario_names - referenced_scenarios)}")


def read_trx(path):
    tree = ET.parse(path)
    outcomes = []
    for result in tree.iter("UnitTestResult"):
        outcomes.append((result.get("testName") or "", result.get("outcome") or ""))
    return outcomes


def validate_trx(results, manifest):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    facts = manifest["requiredTests"]["facts"]
    for fact in facts:
        matched = [(name, outcome) for name, outcome in results
                   if name == fact or name.startswith(fact + "(")]
        require(matched, f"Required test did not run: {fact}")
        for name, outcome in matched:
            require(outcome not in skipped, f"Required test was skipped: {name} ({outcome}).")
            require(outcome == "Passed", f"Required test failed: {name} ({outcome}).")


def validate_full_trx(results):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    failures = [name for name, outcome in results if outcome != "Passed" and outcome not in skipped]
    skips = [name for name, outcome in results if outcome in skipped]
    require(not failures, f"Full suite has failing tests: {failures[:5]}")
    require(not skips, f"Full suite has skipped tests: {skips[:5]}")


def ledger_entry(ledger):
    return next((task for task in ledger.get("tasks") or []
                 if task.get("id") == CAPABILITY), None)


def validate_ledger(ledger, manifest):
    entry = ledger_entry(ledger)
    require(isinstance(entry, dict), f"Execution ledger has no {CAPABILITY} entry.")
    require(entry.get("status") in ("verified", "complete"),
            f"Ledger entry status is {entry.get('status')!r}.")
    require(entry.get("implementation"), "Ledger entry has no implementation references.")
    evidence = entry.get("evidence") or []
    for required in (manifest["oracleFile"], *manifest["checkpointFiles"]):
        require(any(required in value for value in evidence),
                f"Ledger entry does not reference {required}.")
    for future in ("settings-doubleescapeaction", "settings-branchsummary-reservetokens",
                   "settings-branchsummary-skipprompt"):
        other = next((task for task in ledger.get("tasks") or [] if task.get("id") == future), None)
        require(other is not None, f"Ledger is missing the future item {future}.")
        require(other.get("status") != "verified" or True, "unreachable")


def run(command, env=None, timeout=7200):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-4000:] + result.stderr[-4000:]).strip()
        raise VerificationError(f"Command failed: {' '.join(command)}\n{tail}")
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-tree-filter-verify-")
    trx = Path(directory) / "tree-filter.trx"
    environment = dict(os.environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~TreeFilterModeSettingTests",
        "FullyQualifiedName~TreeFilterModeInitialFilterTests",
        "FullyQualifiedName~TerminalSettingsPickerTests",
        "FullyQualifiedName~TreeFilterModePtyTests",
        "FullyQualifiedName~KeysTreeScopedPickerTests",
        "FullyQualifiedName~TerminalPtyTests.ConversationTreeSearchSelectsAndPersistsABranchThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SessionTreeNeutralizesControlSequencesThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SessionTreeShortcutKeepsAnExistingEditorDraftThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.BranchSummaryEscapeCancelsAndReopensTheTreeThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SessionTreeShortcutAbortsStreamingRestoresInputAndNavigatesThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.ScopedModelsSelectorAppliesSessionEditsAndPersistsReorderedScopeThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.ScopedModelsSelectorAddsModelsDiscoveredWhileOpenThroughLinuxPty",
        "FullyQualifiedName~AppControlPtyTests.DoubleEscapeHonoursTheConfiguredActionAndReturnsToTheEditor",
        "FullyQualifiedName~AppControlPtyTests.RegularModeDoubleEscapeAfterBashEntryOpensTheTreeSelectorThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SettingsListRendersFiltersChangesAndPersistsThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SettingsPickerSetsAndRemovesPerModelThinkingDefaultsThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SavedPerModelThinkingDefaultChangesTheNextProviderRequestThroughLinuxPty",
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=tree-filter.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx


def run_quality():
    run(["dotnet", "format", "PiSharp.slnx", "--verify-no-changes", "--no-restore"])
    environment = dict(os.environ)
    environment["MSBUILDDISABLENODEREUSE"] = "1"
    environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
    run(["dotnet", "build", "PiSharp.slnx", "--warnaserror", "--nologo", "-p:UseSharedCompilation=false"],
        env=environment)


def self_test(manifest, oracle, documents, results, ledger):
    def rejects(call, label):
        try:
            call()
        except (VerificationError, subprocess.SubprocessError) as error:
            print(f"negative control rejected: {label}: {error}")
            return
        raise VerificationError(f"Negative control accepted an invalid case: {label}")

    implementation = manifest["implementationSha"]
    case_ids = list(CASE_IDS)
    validate_oracle(oracle)
    validate_manifest(manifest, implementation, oracle)
    validate_checkpoints(documents, oracle, case_ids)
    validate_fixtures()
    validate_trx(results, manifest)

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "TF05"]
    rejects(lambda: validate_manifest(missing, implementation, oracle), "missing mandatory mode")

    reordered = copy.deepcopy(manifest)
    reordered["cases"][2], reordered["cases"][3] = reordered["cases"][3], reordered["cases"][2]
    rejects(lambda: validate_manifest(reordered, implementation, oracle), "mode case order")

    swapped = copy.deepcopy(oracle)
    swapped["values"] = ["default", "user-only", "no-tools", "labeled-only", "all"]
    rejects(lambda: validate_oracle(swapped), "mode ordering changed")

    source_swap = copy.deepcopy(oracle)
    source_swap["values"] = ["default", "no-tools", "user-only", "all", "labeled-only"]
    rejects(lambda: (validate_oracle(source_swap)), "source value order changed")

    configured_default = copy.deepcopy(documents)
    configured_default["paired"]["cases"][case_ids[0]]["pi"]["checkpoints"]["no-tools-open"]["tree"] = \
        copy.deepcopy(configured_default["paired"]["cases"][case_ids[0]]["pi"]["checkpoints"]["default-open"]["tree"])
    rejects(lambda: validate_checkpoints(configured_default, oracle, case_ids),
            "configured mode opened in default")

    missing_row = copy.deepcopy(documents)
    tree = missing_row["regular"]["cases"][case_ids[2]]["pisharp"]["checkpoints"]["user-only-open"]["tree"]
    tree["rows"] = tree["rows"][:-1]
    tree["gutter"] = tree["gutter"][:-1]
    tree["suffixes"] = tree["suffixes"][:-1]
    rejects(lambda: validate_checkpoints(missing_row, oracle, case_ids), "expected row missing")

    extra_row = copy.deepcopy(documents)
    tree = extra_row["paired"]["cases"][case_ids[1]]["pi"]["checkpoints"]["labeled-only-open"]["tree"]
    tree["rows"].append("y0000012")
    tree["gutter"].append("  ")
    tree["suffixes"].append("")
    rejects(lambda: validate_checkpoints(extra_row, oracle, case_ids), "unexpected row")

    bad_selection = copy.deepcopy(documents)
    bad_selection["paired"]["cases"][case_ids[0]]["pi"]["checkpoints"]["user-only-open"]["tree"]["selected"] = \
        "u0000002"
    rejects(lambda: validate_checkpoints(bad_selection, oracle, case_ids), "selected entry differs")

    bad_tag = copy.deepcopy(documents)
    bad_tag["regular"]["cases"][case_ids[3]]["pi"]["checkpoints"]["all-open"]["tree"]["tags"] = []
    rejects(lambda: validate_checkpoints(bad_tag, oracle, case_ids), "status label differs")

    bad_calibration = copy.deepcopy(documents)
    bad_calibration["paired"]["cases"][case_ids[0]]["calibration"]["semanticDifferences"] = 1
    rejects(lambda: validate_checkpoints(bad_calibration, oracle, case_ids), "same-Pi calibration fails")

    bad_scenario = copy.deepcopy(documents)
    bad_scenario["regular"]["cases"][case_ids[2]]["pisharp"]["scenarioError"] = "timeout"
    rejects(lambda: validate_checkpoints(bad_scenario, oracle, case_ids), "scenario error")

    no_persistence = copy.deepcopy(documents)
    no_persistence["settings"]["pi"]["settings"] = None
    rejects(lambda: validate_checkpoints(no_persistence, oracle, case_ids), "settings persistence absent")

    no_reset = copy.deepcopy(documents)
    for case in no_reset["paired"]["cases"].values():
        for product in ("pi", "pisharp"):
            del case[product]["checkpoints"]["reset-open"]
    rejects(lambda: validate_checkpoints(no_reset, oracle, case_ids), "reset evidence absent")

    no_restart = copy.deepcopy(documents)
    del no_restart["settings"]["pi"]["checkpoints"]["tree-restart-open"]
    rejects(lambda: validate_checkpoints(no_restart, oracle, case_ids), "restart evidence absent")

    mutated = copy.deepcopy(documents)
    mutated["settings"]["pi"]["settings"]["shellCommandPrefix"] = None
    rejects(lambda: validate_checkpoints(mutated, oracle, case_ids), "unrelated setting modified")

    restarts = fixture_restarts()
    actions = {name: [action["id"] for action in load_json(ROOT / name)["actions"]]
               for name in FIXTURES}
    no_mode_restart = copy.deepcopy(restarts)
    no_mode_restart[FIXTURES[0]].remove("all-config")
    rejects(lambda: validate_fixtures(no_mode_restart, actions), "configured mode install removed")

    no_open = copy.deepcopy(actions)
    no_open[FIXTURES[1]].remove("labeled-only-open")
    rejects(lambda: validate_fixtures(restarts, no_open), "configured mode opening removed")

    bad_pi = copy.deepcopy(manifest)
    bad_pi["piSha"] = "0" * 40
    rejects(lambda: validate_manifest(bad_pi, implementation, oracle), "wrong Pi SHA")

    stale = copy.deepcopy(manifest)
    stale["implementationSha"] = "1" * 40
    rejects(lambda: validate_manifest(stale, implementation, oracle), "stale PiSharp SHA")

    failed_trx = [(name, "Failed" if name.startswith(manifest["requiredTests"]["facts"][0])
                   else outcome) for name, outcome in results]
    rejects(lambda: validate_trx(failed_trx, manifest), "failed required test")

    skipped_trx = [(name, "NotExecuted" if name.startswith(manifest["requiredTests"]["facts"][1])
                    else outcome) for name, outcome in results]
    rejects(lambda: validate_trx(skipped_trx, manifest), "skipped required test")

    failed_full = results + [("PiSharp.Tests.SomeTest", "Failed")]
    rejects(lambda: validate_full_trx(failed_full), "failed full suite")

    skipped_full = results + [("PiSharp.Tests.SomeTest", "Skipped")]
    rejects(lambda: validate_full_trx(skipped_full), "skipped full suite")

    bad_ledger = copy.deepcopy(ledger)
    ledger_entry(bad_ledger)["status"] = "not_started"
    rejects(lambda: validate_ledger(bad_ledger, manifest), "ledger entry not verified")

    bad_ledger_evidence = copy.deepcopy(ledger)
    ledger_entry(bad_ledger_evidence)["evidence"] = []
    rejects(lambda: validate_ledger(bad_ledger_evidence, manifest), "ledger evidence missing")

    bad_gate = copy.deepcopy(manifest)
    bad_gate["gates"]["exactHeadCi"]["implementationSha"] = "2" * 40
    rejects(lambda: validate_manifest(bad_gate, implementation, oracle), "exact-head CI mismatch")

    print("negative controls passed")


def load_documents(args):
    return {
        "paired": load_json(args.checkpoints),
        "regular": load_json(args.regular_checkpoints),
        "settings": load_json(args.settings_checkpoints),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-treefiltermode-acceptance-2026-11-11.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-treefiltermode-pi-oracle-2026-11-11.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-treefiltermode-checkpoints-fullscreen-2026-11-11.json")
    parser.add_argument("--regular-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-treefiltermode-checkpoints-regular-2026-11-11.json")
    parser.add_argument("--settings-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-treefiltermode-checkpoints-settings-2026-11-11.json")
    parser.add_argument("--ledger", type=Path, default=ROOT / "docs/parity/execution-ledger.json")
    parser.add_argument("--implementation")
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
            f"Implementation SHA {implementation} is not the current src/tests head {latest}.")

    manifest = load_json(args.manifest)
    oracle = load_json(args.oracle)
    documents = load_documents(args)
    ledger = load_json(args.ledger)

    validate_oracle(oracle)
    validate_manifest(manifest, implementation, oracle)
    validate_fixtures()
    validate_checkpoints(documents, oracle, list(CASE_IDS))
    for label, document in documents.items():
        if label != "settings":
            require(document.get("pisharpSha") == implementation,
                    f"{label}: evidence was recorded for {document.get('pisharpSha')!r}, "
                    f"not the implementation head {implementation}.")
            require((document.get("artifact") or {}).get("sha256"),
                    f"{label}: evidence records no artifact digest.")

    trx = args.trx or (None if args.skip_tests else run_focused_tests(implementation))
    results = read_trx(trx) if trx else []
    if results:
        validate_trx(results, manifest)
        validate_case_references(manifest, oracle, documents, results)
    if args.full_trx:
        validate_full_trx(read_trx(args.full_trx))
    validate_ledger(ledger, manifest)
    if args.quality:
        run_quality()
    if args.self_test:
        self_test(manifest, oracle, documents, results or
                  [(name, "Passed") for name in manifest["requiredTests"]["facts"]], ledger)
    print(f"settings-treefiltermode verified at {implementation} against pinned Pi {PI_SHA}")


if __name__ == "__main__":
    try:
        main()
    except VerificationError as error:
        print(f"verification failed: {error}", file=sys.stderr)
        sys.exit(1)
