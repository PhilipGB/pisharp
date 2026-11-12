#!/usr/bin/env python3
"""Verify the settings-doubleescapeaction acceptance manifest and committed evidence.

Every gate is recomputed from the frozen evidence and the repository sources: the
manifest's own status fields are only accepted when the corresponding observation,
test outcome and provenance check independently pass.
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
CAPABILITY = "settings-doubleescapeaction"
VALUES = ["tree", "fork", "none"]
DEFAULT = "tree"
WINDOW = 500
LABEL = "Double-escape action"
DESCRIPTION = "Action when pressing Escape twice with empty editor"
REQUIRED_CASES = [f"DE{index:02d}" for index in range(1, 19)]
MAIN_CASE_IDS = ("100x30-dark-fullscreen", "140x40-dark-fullscreen")
REGULAR_CASE_IDS = ("100x30-dark-regular", "140x40-dark-regular")
SINGLE_CASE = "100x30-dark-fullscreen"
FIXTURES = [
    "tools/parity/terminal/fixtures/terminal-double-escape-action.json",
    "tools/parity/terminal/fixtures/terminal-double-escape-action-regular.json",
    "tools/parity/terminal/fixtures/terminal-double-escape-action-malformed.json",
    "tools/parity/terminal/fixtures/terminal-double-escape-action-project.json",
    "tools/parity/terminal/fixtures/terminal-double-escape-action-project-malformed.json",
    "tools/parity/terminal/fixtures/terminal-double-escape-action-composition.json",
    "tools/parity/terminal/fixtures/terminal-double-escape-action-settings.json",
]
MAIN_CHECKPOINTS = ("unset", "tree", "fork", "none", "outside", "draft", "whitespace", "bash-entry")
EXPECTED_OVERLAY = {"unset": "tree", "tree": "tree", "fork": "fork", "none": "none",
                    "outside": "none", "draft": "none", "whitespace": "tree",
                    "bash-entry": "none"}
MALFORMED_LABELS = ("bogus-string", "empty-string", "number", "boolean", "null", "object", "array")
MALFORMED_OVERLAY = {"bogus-string": "fork", "empty-string": "fork", "number": "fork",
                     "boolean": "fork", "null": "tree", "object": "fork", "array": "fork"}
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
    match = re.search(r"class DoubleEscapeActionSetting\b.*?Values\s*=\s*\[(.*?)\];", text, re.S)
    require(match is not None, f"{VALUE_SOURCE} has no doubleEscapeAction value list.")
    return re.findall(r'"([^"]+)"', match.group(1))


def validate_oracle(oracle):
    require(oracle.get("schema") == 1, "Oracle schema must be 1.")
    require(oracle.get("capability") == CAPABILITY, "Oracle capability mismatch.")
    require(oracle.get("piSha") == PI_SHA, f"Oracle Pi SHA is {oracle.get('piSha')!r}.")
    require(oracle.get("values") == VALUES, f"Oracle values must be {VALUES}.")
    require(oracle.get("default") == DEFAULT, "Oracle default must be 'tree'.")
    require(oracle.get("windowMilliseconds") == WINDOW, "Oracle window must be 500 ms.")
    require(oracle.get("windowComparison") == "<", "Oracle comparison must be strict '<'.")
    require(oracle.get("label") == LABEL, "Oracle settings label differs.")
    require(oracle.get("description") == DESCRIPTION, "Oracle settings description differs.")
    require(source_values() == oracle["values"],
            f"{VALUE_SOURCE} values {source_values()} differ from the oracle order.")
    sources = oracle.get("sources") or []
    require(len(sources) >= 8, "Oracle must cite the pinned-Pi source inventory.")
    for entry in sources:
        require(entry.get("file") and entry.get("symbol") and entry.get("observation"),
                f"Oracle source entry is incomplete: {entry}")
    scenarios = oracle.get("scenarios") or []
    require([scenario.get("id") for scenario in scenarios] == REQUIRED_CASES,
            f"Oracle scenarios must be {REQUIRED_CASES}.")
    projections = oracle.get("projections") or {}
    require(set(projections) == set(MAIN_CASE_IDS),
            f"Oracle projections must cover {sorted(MAIN_CASE_IDS)}.")
    for case_id, case in projections.items():
        for name in MAIN_CHECKPOINTS:
            projection = case.get(name)
            require(isinstance(projection, dict), f"Oracle {case_id} has no {name} projection.")
            require(projection.get("overlay") == EXPECTED_OVERLAY[name],
                    f"Oracle {case_id} {name} overlay is {projection.get('overlay')!r}.")
            if EXPECTED_OVERLAY[name] == "tree":
                require(projection.get("tree"), f"Oracle {case_id} {name} has no tree state.")
            if name == "fork":
                require(projection["fork"]["total"] == 3,
                        f"Oracle {case_id} fork projection must list three messages.")
                require(projection["fork"]["selectedIndex"] == 3,
                        f"Oracle {case_id} fork projection must select the newest message.")
                require(projection["fork"]["selectedPreview"] == "second prompt",
                        f"Oracle {case_id} fork projection preview differs.")
        require(case["unset"] == case["tree"],
                f"{case_id}: unset must project exactly like explicit tree.")
        require(case["none"]["overlay"] == case["outside"]["overlay"] == "none",
                f"{case_id}: none and out-of-window presses must both open nothing.")
    regular = oracle.get("regularProjections") or {}
    require(set(regular) == set(REGULAR_CASE_IDS), "Oracle has no regular-mode projections.")
    for case_id, case in regular.items():
        for name in MAIN_CHECKPOINTS:
            require(case[name]["overlay"] == EXPECTED_OVERLAY[name],
                    f"Oracle {case_id} {name} overlay differs in regular mode.")
    malformed = oracle.get("malformedProjections") or {}
    require(malformed, "Oracle has no malformed-value projections.")
    for case_id, case in malformed.items():
        for label in MALFORMED_LABELS:
            require(case[label]["overlay"] == MALFORMED_OVERLAY[label],
                    f"Oracle {case_id} {label} overlay is {case[label]['overlay']!r}.")
    layered = oracle.get("layeredProjections") or {}
    require(layered, "Oracle has no layered project projections.")
    for case_id, case in layered.items():
        require(case["trusted"]["overlay"] == "fork",
                f"Oracle {case_id} trusted project overlay is {case['trusted']['overlay']!r}.")
        require(case["untrusted"]["overlay"] == "none",
                f"Oracle {case_id} untrusted project overlay is {case['untrusted']['overlay']!r}.")
    settings = oracle.get("settingsProjection") or {}
    for name, value in (("row", "tree"), ("fork", "fork")):
        state = settings.get(name)
        require(isinstance(state, dict), f"Oracle settings projection {name} is missing.")
        require(state["value"] == value, f"Oracle settings projection {name} value differs.")
        require(state["description"] is True, f"Oracle settings projection {name} description missing.")
    require(settings["persisted"]["doubleEscapeAction"] == "fork",
            "Oracle settings projection must record the persisted fork value.")


def validate_manifest(manifest, implementation, oracle):
    require(manifest.get("schema") == 1, "Manifest schema must be 1.")
    require(manifest.get("capability") == CAPABILITY, "Manifest capability mismatch.")
    require(manifest.get("piSha") == PI_SHA, f"Manifest Pi SHA is {manifest.get('piSha')!r}.")
    recorded = manifest.get("implementationSha")
    require(len(recorded or "") == 40, "Manifest must record a 40-character implementation SHA.")
    require(recorded == implementation,
            f"Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.")
    require(is_ancestor(recorded), "Recorded implementation SHA is not an ancestor of HEAD.")
    require(manifest.get("baseSha"), "Manifest must record the post-merge base SHA.")
    require(manifest.get("oracleFile"), "Manifest must name the oracle file.")
    require(manifest.get("terminalFixtures") == FIXTURES,
            f"Manifest terminal fixtures must be {FIXTURES}.")
    checkpoints = manifest.get("checkpointFiles") or []
    require(len(checkpoints) == 7, "Manifest must name all seven checkpoint documents.")
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
            {"paired-pty", "same-pi-calibration", "settings-selector-calibration",
             "layered-project-precedence", "malformed-value-parity"},
            "Manifest must require every milestone comparison.")
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
    require(len(facts) >= 20, "Manifest must list at least twenty required deterministic tests.")
    require(len(set(facts)) == len(facts), "Duplicate required test names.")
    evidence_ids = {entry.rsplit("/", 1)[-1] for entry in tests.get("ptyEvidence") or []}
    require(evidence_ids == set(MAIN_CASE_IDS) | set(REGULAR_CASE_IDS) | {SINGLE_CASE},
            "Manifest PTY evidence ids must name every paired case.")
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


def checkpoint(document, case_id, name, product="pi"):
    case = document["cases"][case_id]
    found = case[product]["checkpoints"].get(name)
    require(found is not None, f"{case_id} has no {name} checkpoint in the {product} evidence.")
    return found


def require_main_projection(document, case_id, name, projection, context):
    for product in ("pi", "pisharp"):
        state = checkpoint(document, case_id, name, product)
        require(state["overlay"] == projection["overlay"],
                f"{context} ({product}): overlay is {state['overlay']!r}, "
                f"expected {projection['overlay']!r}.")
        if projection["overlay"] == "tree":
            require(state["tree"] is not None, f"{context} ({product}): tree state missing.")
            require(state["tree"]["count"] == projection["tree"]["count"],
                    f"{context} ({product}): tree count differs: {state['tree']['count']}")
            require(state["tree"]["tags"] == projection["tree"]["tags"],
                    f"{context} ({product}): tree status label differs: {state['tree']['tags']}")
            require(state["tree"]["selected"] == projection["tree"]["selected"],
                    f"{context} ({product}): tree selection differs: {state['tree']['selected']}")
        if projection["overlay"] == "fork":
            require(state["fork"] is not None, f"{context} ({product}): fork state missing.")
            require(state["fork"] == projection["fork"],
                    f"{context} ({product}): fork projection differs: {state['fork']}")


def validate_paired_document(document, oracle, case_ids, label, projections_key="projections"):
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
            for name, state in case[product]["checkpoints"].items():
                require(not state["errors"],
                        f"{case_id}/{product}/{name}: error text {state['errors']}")
        for name in MAIN_CHECKPOINTS:
            require_main_projection(document, case_id, name,
                                    oracle[projections_key][case_id][name],
                                    f"{case_id} {name}")
        require(case["pi"]["settings"] == case["pisharp"]["settings"],
                f"{case_id}: persisted settings evidence differs.")


def validate_malformed_document(document, oracle):
    require(document.get("capability") == CAPABILITY, "malformed: capability mismatch.")
    require(document.get("piSha") == PI_SHA, "malformed: Pi SHA mismatch.")
    for case_id, case in document["cases"].items():
        require(case_id in oracle["malformedProjections"], "malformed: unknown case.")
        require(case["paired"]["semanticDifferences"] == 0,
                f"{case_id}: {case['paired']['semanticDifferences']} unexplained paired differences: "
                f"{case['paired']['semanticFirstDifferences'][:3]}")
        require(not case["paired"]["scenarioErrors"], f"{case_id}: scenario errors")
        require(case.get("calibration", {}).get("semanticDifferences") == 0,
                f"{case_id}: calibration reported differences.")
        for product in ("pi", "pisharp"):
            require(not case[product]["scenarioError"],
                    f"{case_id}/{product}: a malformed stored value failed the process: "
                    f"{case[product]['scenarioError']}")
            for name, state in case[product]["checkpoints"].items():
                require(not state["errors"],
                        f"{case_id}/{product}/{name}: error text {state['errors']}")
        for label in MALFORMED_LABELS:
            name = f"{label}-double-escape"
            for product in ("pi", "pisharp"):
                state = checkpoint(document, case_id, name, product)
                require(state["overlay"] == MALFORMED_OVERLAY[label],
                        f"{case_id} {label} ({product}): overlay is {state['overlay']!r}, "
                        f"expected {MALFORMED_OVERLAY[label]!r}.")


def validate_layered_document(document, oracle):
    require(document.get("capability") == CAPABILITY, "layered: capability mismatch.")
    require(document.get("piSha") == PI_SHA, "layered: Pi SHA mismatch.")
    for case_id, case in document["cases"].items():
        require(case_id in oracle["layeredProjections"], "layered: unknown case.")
        require(case["paired"]["semanticDifferences"] == 0,
                f"{case_id}: {case['paired']['semanticDifferences']} unexplained paired differences: "
                f"{case['paired']['semanticFirstDifferences'][:3]}")
        require(not case["paired"]["scenarioErrors"], f"{case_id}: scenario errors")
        require(case.get("calibration", {}).get("semanticDifferences") == 0,
                f"{case_id}: calibration reported differences.")
        for product in ("pi", "pisharp"):
            require(not case[product]["scenarioError"], f"{case_id}/{product}: scenario error")
            require(case[product]["project"] is not None,
                    f"{case_id}/{product}: the project settings file was not captured.")
            for name, state in case[product]["checkpoints"].items():
                require(not state["errors"], f"{case_id}/{product}/{name}: error text {state['errors']}")
        for label in ("trusted", "untrusted"):
            for product in ("pi", "pisharp"):
                state = checkpoint(document, case_id, f"{label}-double-escape", product)
                require(state["overlay"] == oracle["layeredProjections"][case_id][label]["overlay"],
                        f"{case_id} {label} ({product}): overlay is {state['overlay']!r}, "
                        f"expected {oracle['layeredProjections'][case_id][label]['overlay']!r}.")


def validate_composition_document(document, oracle):
    require(document.get("capability") == CAPABILITY, "composition: capability mismatch.")
    require(document.get("piSha") == PI_SHA, "composition: Pi SHA mismatch.")
    for case_id, case in document["cases"].items():
        require(case["paired"]["semanticDifferences"] == 0,
                f"{case_id}: {case['paired']['semanticDifferences']} unexplained paired differences: "
                f"{case['paired']['semanticFirstDifferences'][:3]}")
        require(case.get("calibration", {}).get("semanticDifferences") == 0,
                f"{case_id}: calibration reported differences.")
        for product in ("pi", "pisharp"):
            require(not case[product]["scenarioError"], f"{case_id}/{product}: scenario error")
            state = checkpoint(document, case_id, "compose-double-escape", product)
            require(state["overlay"] == "tree",
                    f"{case_id} ({product}): double Escape did not open the tree.")
            require(state["tree"]["tags"] == ["user"],
                    f"{case_id} ({product}): initial filter is {state['tree']['tags']!r}, expected [user].")
            require(state["tree"]["count"] == "2/3",
                    f"{case_id} ({product}): filtered count is {state['tree']['count']!r}.")
        require(case["pi"]["settings"]["treeFilterMode"] == "user-only",
                f"{case_id}: the treeFilterMode setting was not preserved.")
        require(case["pi"]["settings"]["doubleEscapeAction"] == "tree",
                f"{case_id}: the doubleEscapeAction setting was not preserved.")


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
    for name in ("settings-state", "settings-fork-state"):
        state = pi["checkpoints"].get(name, {}).get("settings")
        require(state is not None, f"settings-selector: {name} did not render the setting row.")
        require(state["row"] is True, f"settings-selector: {name} has no row.")
        require(state["description"] is True,
                f"settings-selector: {name} does not show the pinned description.")
        require(state["value"] in VALUES, f"settings-selector: {name} value {state['value']!r}")
    projection = oracle["settingsProjection"]
    keys = {"settings-state": "row", "settings-fork-state": "fork"}
    for name, key in keys.items():
        state = pi["checkpoints"][name]["settings"]
        require(state["value"] == projection[key]["value"],
                f"settings-selector: {name} shows {state['value']!r}, "
                f"expected {projection[key]['value']!r}.")
        require(state == projection[key],
                f"settings-selector: {name} differs from the frozen projection.")
    # Pinned Pi reads the setting on every press, so the change applies without a restart.
    require(pi["checkpoints"]["live-double-escape"]["overlay"] == "fork",
            "settings-selector: the live change did not take effect.")
    require(pi["checkpoints"]["restart-double-escape"]["overlay"] == "fork",
            "settings-selector: the persisted value did not survive the restart.")
    persisted = pi["settings"]
    require(persisted is not None, "settings-selector: no persisted settings evidence.")
    require(persisted["doubleEscapeAction"] == "fork",
            f"settings-selector: persisted value {persisted['doubleEscapeAction']!r}")
    require(persisted["quietStartup"] is True and persisted["shellCommandPrefix"] == "preserved",
            f"settings-selector: unrelated settings changed: {persisted}")
    require(persisted["theme"] == "dark" and persisted["tuiMode"] == "fullscreen",
            f"settings-selector: harness settings changed: {persisted}")


def validate_checkpoints(documents, oracle):
    require(set(documents) == {"paired", "regular", "settings", "malformed", "project",
                               "project-malformed", "composition"},
            "Checkpoint evidence must contain every fixture document.")
    validate_paired_document(documents["paired"], oracle, MAIN_CASE_IDS, "fullscreen")
    validate_paired_document(documents["regular"], oracle, REGULAR_CASE_IDS, "regular",
                             projections_key="regularProjections")
    validate_settings_document(documents["settings"], oracle)
    validate_malformed_document(documents["malformed"], oracle)
    validate_layered_document(documents["project"], oracle)
    validate_layered_document(documents["project-malformed"], oracle)
    validate_composition_document(documents["composition"], oracle)


def fixture_state():
    restarts = {}
    actions = {}
    for name in FIXTURES:
        fixture = load_json(ROOT / name)
        restarts[name] = [action["id"] for action in fixture.get("actions") or [] if action.get("restart")]
        actions[name] = [action["id"] for action in fixture.get("actions") or []]
    return restarts, actions


def validate_fixtures(restarts=None, actions=None):
    default_restarts, default_actions = fixture_state()
    restarts = restarts if restarts is not None else default_restarts
    actions = actions if actions is not None else default_actions
    for name in FIXTURES[:2]:
        expected = ["tree-config", "fork-config", "none-config", "outside-config"]
        require(restarts[name] == expected,
                f"{name} must install every configured action through a restart: {restarts[name]}")
        ids = actions[name]
        for value in VALUES:
            require(f"{value}-double-escape" in ids, f"{name} has no {value} double Escape.")
        for name_id in ("outside-first", "outside-second", "draft-double-escape",
                        "whitespace-double-escape", "bash-entry-escape"):
            require(name_id in ids, f"{name} has no {name_id} case.")
    malformed_restarts = restarts[FIXTURES[2]]
    require(malformed_restarts == [f"{label}-config" for label in MALFORMED_LABELS],
            f"{FIXTURES[2]} must install every malformed value through a restart: {malformed_restarts}")
    for label in MALFORMED_LABELS:
        require(f"{label}-double-escape" in actions[FIXTURES[2]],
                f"{FIXTURES[2]} has no {label} observation.")
    for name in FIXTURES[3:5]:
        require(restarts[name] == ["trusted-config", "untrusted-config"],
                f"{name} must install both trust cases through a restart: {restarts[name]}")
        require("trusted-double-escape" in actions[name] and "untrusted-double-escape" in actions[name],
                f"{name} has no layered observations.")
    require(restarts[FIXTURES[5]] == ["compose-config"],
            f"{FIXTURES[5]} must install the composed settings through a restart.")
    require("compose-double-escape" in actions[FIXTURES[5]],
            f"{FIXTURES[5]} has no double Escape observation.")
    require(restarts[FIXTURES[6]] == ["restart"],
            f"{FIXTURES[6]} must restart once to prove persistence.")
    for name_id in ("live-double-escape", "restart-double-escape"):
        require(name_id in actions[FIXTURES[6]], f"{FIXTURES[6]} has no {name_id} case.")


def checkpoint_index(documents):
    index = set()
    for label, document in documents.items():
        if label == "settings":
            for name in document["pi"]["checkpoints"]:
                index.add(name)
                index.add(f"settings/{name}")
            continue
        for case_id, case in (document.get("cases") or {}).items():
            for product in ("pi", "pisharp"):
                for name in case[product]["checkpoints"]:
                    index.add(f"{case_id}/{name}")
                    index.add(name)
                    index.add(f"{label}/{name}")
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
            require(reference in index,
                    f"{case['id']} references a missing terminal checkpoint: {reference!r}")
    require(referenced_tests == allowed,
            f"Required tests are not referenced by any case: {sorted(allowed - referenced_tests)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in case.get("oracle") or []}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced: {sorted(scenario_names - referenced_scenarios)}")


def read_trx(path):
    tree = ET.parse(path)
    return [(result.get("testName") or "", result.get("outcome") or "")
            for result in tree.iter("UnitTestResult")]


def validate_trx(results, manifest):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    for fact in manifest["requiredTests"]["facts"]:
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
    return next((task for task in ledger.get("tasks") or [] if task.get("id") == CAPABILITY), None)


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
    for future in ("settings-branchsummary-reservetokens", "settings-branchsummary-skipprompt"):
        other = next((task for task in ledger.get("tasks") or [] if task.get("id") == future), None)
        require(other is not None, f"Ledger is missing the future item {future}.")
        require(other.get("status") != "verified",
                f"Out-of-scope ledger item {future} must not be verified.")


def run(command, env=None, timeout=7200):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-4000:] + result.stderr[-4000:]).strip()
        raise VerificationError(f"Command failed: {' '.join(command)}\n{tail}")
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-double-escape-verify-")
    trx = Path(directory) / "double-escape.trx"
    environment = dict(os.environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~DoubleEscapeActionSettingTests",
        "FullyQualifiedName~DoubleEscapeActionOracleTests",
        "FullyQualifiedName~DoubleEscapeActionPtyTests",
        "FullyQualifiedName~AppControlTests",
        "FullyQualifiedName~AppControlParityTests",
        "FullyQualifiedName~AppControlPtyTests.DoubleEscapeHonoursTheConfiguredActionAndReturnsToTheEditor",
        "FullyQualifiedName~AppControlPtyTests.RegularModeDoubleEscapeAfterBashEntryOpensTheTreeSelectorThroughLinuxPty",
        "FullyQualifiedName~TerminalSettingsPickerTests",
        "FullyQualifiedName~TerminalPtyTests.SettingsListRendersFiltersChangesAndPersistsThroughLinuxPty",
        "FullyQualifiedName~TreeFilterModeSettingTests",
        "FullyQualifiedName~TreeFilterModeInitialFilterTests",
        "FullyQualifiedName~TerminalPtyTests.SessionTreeShortcutKeepsAnExistingEditorDraftThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.ConversationTreeSearchSelectsAndPersistsABranchThroughLinuxPty",
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=double-escape.trx", "--results-directory", directory,
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
    validate_oracle(oracle)
    validate_manifest(manifest, implementation, oracle)
    validate_checkpoints(documents, oracle)
    validate_fixtures()
    validate_trx(results, manifest)

    def mutate(path, value):
        document = copy.deepcopy(documents)
        target = document
        for step in path[:-1]:
            target = target[step]
        target[path[-1]] = value
        return document

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "DE03"]
    rejects(lambda: validate_manifest(missing, implementation, oracle), "missing fork case")

    reordered = copy.deepcopy(manifest)
    reordered["cases"][2], reordered["cases"][3] = reordered["cases"][3], reordered["cases"][2]
    rejects(lambda: validate_manifest(reordered, implementation, oracle), "case order changed")

    swapped = copy.deepcopy(oracle)
    swapped["values"] = ["fork", "tree", "none"]
    rejects(lambda: validate_oracle(swapped), "value ordering changed")

    bad_default = copy.deepcopy(oracle)
    bad_default["default"] = "fork"
    rejects(lambda: validate_oracle(bad_default), "wrong default")

    bad_window = copy.deepcopy(oracle)
    bad_window["windowMilliseconds"] = 499
    rejects(lambda: validate_oracle(bad_window), "wrong window")

    case_id = MAIN_CASE_IDS[0]
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pi", "checkpoints", "tree-double-escape", "overlay"], "fork"),
        oracle), "tree action opened fork")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pi", "checkpoints", "fork-double-escape", "overlay"], "tree"),
        oracle), "fork action opened tree")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pisharp", "checkpoints", "none-double-escape", "overlay"], "tree"),
        oracle), "none opened a selector")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pisharp", "checkpoints", "unset-double-escape", "overlay"], "none"),
        oracle), "unset did not behave as the default")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pi", "checkpoints", "draft-double-escape", "overlay"], "tree"),
        oracle), "non-empty draft opened a selector")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pi", "checkpoints", "outside-second", "overlay"], "tree"),
        oracle), "out-of-window press fired")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pi", "checkpoints", "whitespace-double-escape", "overlay"], "none"),
        oracle), "whitespace draft suppressed the action")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "calibration", "semanticDifferences"], 1),
        oracle), "same-Pi calibration fails")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pisharp", "scenarioError"], "timeout"),
        oracle), "scenario error")
    rejects(lambda: validate_checkpoints(
        mutate(["paired", "cases", case_id, "pisharp", "settings", "shellCommandPrefix"], None),
        oracle), "unrelated setting modified")
    rejects(lambda: validate_checkpoints(
        mutate(["malformed", "cases", case_id, "pi", "checkpoints", "number-double-escape", "overlay"], "none"),
        oracle), "malformed value result differs from pinned Pi")
    rejects(lambda: validate_checkpoints(
        mutate(["malformed", "cases", case_id, "pi", "scenarioError"], "Invalid settings"),
        oracle), "malformed value failed startup")
    rejects(lambda: validate_checkpoints(
        mutate(["project", "cases", case_id, "pi", "checkpoints", "trusted-double-escape", "overlay"], "none"),
        oracle), "trusted project precedence mismatch")
    rejects(lambda: validate_checkpoints(
        mutate(["project", "cases", case_id, "pisharp", "checkpoints", "untrusted-double-escape", "overlay"], "fork"),
        oracle), "untrusted project affected behaviour")
    rejects(lambda: validate_checkpoints(
        mutate(["composition", "cases", case_id, "pi", "checkpoints", "compose-double-escape",
                "tree", "tags"], []),
        oracle), "tree filter composition ignored")
    rejects(lambda: validate_checkpoints(
        mutate(["settings", "pi", "checkpoints", "live-double-escape", "overlay"], "none"),
        oracle), "settings change did not apply live")
    rejects(lambda: validate_checkpoints(
        mutate(["settings", "pi", "checkpoints", "restart-double-escape", "overlay"], "none"),
        oracle), "persistence evidence wrong")
    rejects(lambda: validate_checkpoints(
        mutate(["settings", "pi", "settings", "doubleEscapeAction"], None),
        oracle), "persistence evidence missing")
    rejects(lambda: validate_checkpoints(
        mutate(["settings", "pi", "checkpoints", "settings-fork-state", "settings", "value"], "none"),
        oracle), "settings value order changed")

    restarts, actions = fixture_state()
    no_fork = copy.deepcopy(restarts)
    no_fork[FIXTURES[0]].remove("fork-config")
    rejects(lambda: validate_fixtures(no_fork, actions), "configured fork install removed")
    no_none = copy.deepcopy(actions)
    no_none[FIXTURES[1]].remove("none-double-escape")
    rejects(lambda: validate_fixtures(restarts, no_none), "none observation removed")
    no_malformed = copy.deepcopy(restarts)
    no_malformed[FIXTURES[2]].remove("null-config")
    rejects(lambda: validate_fixtures(no_malformed, actions), "malformed value removed")

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

    rejects(lambda: validate_full_trx(results + [("PiSharp.Tests.SomeTest", "Failed")]),
            "failed full suite")
    rejects(lambda: validate_full_trx(results + [("PiSharp.Tests.SomeTest", "Skipped")]),
            "skipped full suite")

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
        "malformed": load_json(args.malformed_checkpoints),
        "project": load_json(args.project_checkpoints),
        "project-malformed": load_json(args.project_malformed_checkpoints),
        "composition": load_json(args.composition_checkpoints),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-acceptance-2026-11-12.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-pi-oracle-2026-11-12.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-fullscreen-2026-11-12.json")
    parser.add_argument("--regular-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-regular-2026-11-12.json")
    parser.add_argument("--settings-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-settings-2026-11-12.json")
    parser.add_argument("--malformed-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-malformed-2026-11-12.json")
    parser.add_argument("--project-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-project-2026-11-12.json")
    parser.add_argument("--project-malformed-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-project-malformed-2026-11-12.json")
    parser.add_argument("--composition-checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/settings-doubleescapeaction-checkpoints-composition-2026-11-12.json")
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
    validate_checkpoints(documents, oracle)
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
    print(f"settings-doubleescapeaction verified at {implementation} against pinned Pi {PI_SHA}")


if __name__ == "__main__":
    try:
        main()
    except VerificationError as error:
        print(f"verification failed: {error}", file=sys.stderr)
        sys.exit(1)
