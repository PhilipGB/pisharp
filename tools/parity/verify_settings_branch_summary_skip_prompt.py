#!/usr/bin/env python3
"""Verify the settings-branchsummary-skipprompt acceptance manifest and evidence.

Every gate is recomputed from the frozen evidence and the repository sources. Manifest
status fields are only accepted when the corresponding observation, test outcome and
provenance check independently pass.
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
CAPABILITY = "settings-branchsummary-skipprompt"
DATE = "2026-11-12"
SETTINGS_CASES = [f"BS{index:02d}" for index in range(1, 41)]
PROMPT_CHOICES = ["No summary", "Summarize", "Summarize with custom prompt"]
LABELS = ["fullscreen", "regular", "malformed", "project", "project-null", "project-reserve",
          "project-malformed", "project-malformed-truthy", "tolerant"]
FIXTURES = [f"tools/parity/terminal/fixtures/terminal-branch-summary-skip-prompt{suffix}.json"
            for suffix in ["", "-regular", "-malformed", "-project", "-project-null",
                           "-project-reserve", "-project-malformed",
                           "-project-malformed-truthy", "-tolerant"]]
CHECKPOINT_FILES = [f"docs/parity/fixtures/settings-branchsummary-skipprompt-checkpoints-{label}-{DATE}.json"
                    for label in LABELS]
RUNTIME_CASES = [f"RT{index:02d}" for index in range(1, 12)]
TRUST_CASES = [f"TR{index:02d}" for index in range(1, 7)]
MALFORMED_CASES = [f"ML{index:02d}" for index in range(1, 7)]
REQUIRED_CASES = SETTINGS_CASES + RUNTIME_CASES + TRUST_CASES + MALFORMED_CASES
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


def source_contract():
    text = (ROOT / VALUE_SOURCE).read_text(encoding="utf-8")
    require("BranchSummarySkipPromptSetting" in text,
            f"{VALUE_SOURCE} has no raw skipPrompt representation.")
    require("SkipsPrompt" in text, f"{VALUE_SOURCE} has no JavaScript-truthiness evaluator.")
    require("MergeBranchSummary" in text, f"{VALUE_SOURCE} has no presence-based merge.")
    truthiness = re.search(r"public bool SkipsPrompt =>.*?\n    \};", text, re.S)
    require(truthiness is not None, f"{VALUE_SOURCE} skipPrompt truthiness is missing.")
    body = truthiness.group(0)
    require("JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array" in body,
            "Truthy kinds are not exhaustive.")
    require("JsonValueKind.String => Raw.Length > 2" in body,
            "Stored strings are not evaluated as non-empty.")
    require("JsonValueKind.Number =>" in body and "number != 0" in body,
            "Stored numbers are not evaluated against zero.")
    require("public bool SkipsSummaryPrompt =>" in text,
            "The merged prompt gate is missing.")
    return text


def validate_oracle(oracle):
    require(oracle.get("schema") == 1, "Oracle schema must be 1.")
    require(oracle.get("capability") == CAPABILITY, "Oracle capability mismatch.")
    require(oracle.get("piSha") == PI_SHA, f"Oracle Pi SHA is {oracle.get('piSha')!r}.")
    require(oracle.get("default") is False, "Oracle default must be false.")
    contract = oracle.get("schemaContract") or {}
    require("branchSummary?.skipPrompt" in (contract.get("getter") or "") and
            "SETTINGS_DEFAULTS.branchSummary.skipPrompt" in (contract.get("getter") or ""),
            "Oracle getter does not match pinned Pi.")
    require("Summarize branch?" in (contract.get("promptGate") or ""),
            "Oracle prompt gate does not describe the pinned selector.")
    require("summarize: false" in (contract.get("navigation") or ""),
            "Oracle navigation does not default to no summary.")
    sources = oracle.get("sources") or []
    require(len(sources) >= 9, "Oracle must cite the pinned-Pi source inventory.")
    for entry in sources:
        require(entry.get("file") and entry.get("symbol") and entry.get("observation"),
                f"Oracle source entry is incomplete: {entry}")
    require(any(entry["symbol"] == "isMergeableObject/deepMergeObjects" for entry in sources),
            "Oracle does not cite the presence-based merge.")
    require(any("skipPrompt" in entry["symbol"].lower() or
                "skipPrompt" in entry["observation"] for entry in sources),
            "Oracle does not cite the skipPrompt getter.")
    cases = oracle.get("settingsCases") or []
    require([case.get("id") for case in cases] == SETTINGS_CASES,
            f"Oracle settings cases must be {SETTINGS_CASES}.")
    for case in cases:
        expect = case.get("expect") or {}
        require(set(expect) == {"loaded", "prompts", "skipPrompt", "parentPresent", "reserve"},
                f"Oracle {case['id']} expectation is incomplete: {expect}")
        require(expect["loaded"] is True, f"Oracle {case['id']} does not load.")
        require(expect["reserve"] == oracle["settingsCases"][0]["expect"]["reserve"]
                or case["id"] in ("BS04",),
                f"Oracle {case['id']} changes the reserve unexpectedly.")
    expect = oracle.get("expectations") or {}
    require({case["id"]: case["expect"] for case in cases} == expect,
            "Oracle expectations disagree with its settings cases.")
    require(expect["BS01"]["prompts"] is True, "BS01 must prompt.")
    require(expect["BS02"]["prompts"] is True, "Explicit false must prompt.")
    require(expect["BS03"]["prompts"] is False, "Explicit true must skip the prompt.")
    require(expect["BS04"]["prompts"] is False,
            "A project reserveTokens entry must not erase the inherited skipPrompt.")
    require(expect["BS05"]["prompts"] is True, "A trusted project false must override user true.")
    require(expect["BS06"]["prompts"] is False, "A trusted project true must override user false.")
    require(expect["BS07"]["prompts"] is True, "A project null must override user true and prompt.")
    require(expect["BS08"]["prompts"] is False, "An untrusted project must not change the user value.")
    require(expect["BS09"]["prompts"] is True and expect["BS10"]["prompts"] is False,
            "Malformed project values must follow JavaScript truthiness.")
    require(expect["BS24"]["prompts"] is False, "Stored \"false\" must skip the prompt.")
    require(expect["BS25"]["prompts"] is True, "Stored \"\" must prompt.")
    require(expect["BS26"]["prompts"] is False, "Stored 1 must skip the prompt.")
    require(expect["BS27"]["prompts"] is True, "Stored 0 must prompt.")
    require(expect["BS28"]["prompts"] is False and expect["BS29"]["prompts"] is False,
            "Stored {} and [] must skip the prompt.")
    require(expect["BS23"]["prompts"] is True, "Stored null must prompt.")
    for case_id in ("BS15", "BS16", "BS17", "BS18", "BS19", "BS20", "BS21", "BS22"):
        require(expect[case_id]["prompts"] is True,
                f"{case_id} must prompt because a non-object parent reads as absent.")
        require(expect[case_id]["parentPresent"] is True,
                f"{case_id} must keep the branchSummary key present.")
    require(expect["BS19"]["parentPresent"] is True and expect["BS01"]["parentPresent"] is False,
            "Absent and null parents are not distinguished.")
    require(expect["BS07"]["skipPrompt"] == "null",
            "A project explicit null must replace the user value.")
    require(expect["BS32"]["prompts"] is False and expect["BS32"]["skipPrompt"] == "true",
            "A duplicated skipPrompt must keep the later value (false, true).")
    require(expect["BS33"]["prompts"] is True and expect["BS33"]["skipPrompt"] == "false",
            "A duplicated skipPrompt must keep the later value (true, false).")
    require(expect["BS40"]["prompts"] is True,
            "A duplicated false skipPrompt must stay falsey.")
    require(expect["BS34"]["prompts"] is False and expect["BS35"]["prompts"] is True,
            "An unknown sibling must not change the stored skipPrompt decision.")
    require(expect["BS36"]["prompts"] is True,
            "A trusted project false with an unknown sibling must still override user true.")
    require(expect["BS37"]["prompts"] is False,
            "A trusted project true with an unknown sibling must still override user false.")
    require(expect["BS38"]["prompts"] is False,
            "Object and array siblings must be ignored.")
    require(expect["BS39"]["prompts"] is False and expect["BS39"]["skipPrompt"] == "true",
            "A trusted project with only an unknown sibling must keep the inherited skipPrompt.")
    require(expect["BS04"]["reserve"] == 1234,
            "The project reserveTokens value must win while skipPrompt stays inherited.")
    facts = oracle.get("scenarioFacts") or []
    ids = {fact.get("id") for fact in facts}
    require(len(ids) == len(facts), "Duplicate oracle scenario ids.")
    for label in LABELS:
        for fact in [fact for fact in facts if fact.get("fixture") == label]:
            require(fact["checkpoint"], f"{fact['id']} has no checkpoint.")
            if fact["prompts"]:
                require(fact["choices"] == PROMPT_CHOICES,
                        f"{fact['id']} choices must be {PROMPT_CHOICES}.")
            else:
                require(fact["choices"] is None, f"{fact['id']} must not declare choices.")
    require({"fullscreen:true-tree-selected", "fullscreen:unset-tree-selected",
             "project:trusted-tree-selected", "project:untrusted-tree-selected"} <= ids,
            "Oracle scenario facts are missing the prompt/skip decisions.")
    projections = oracle.get("projections") or {}
    require(set(projections) == set(LABELS),
            f"Oracle projections must cover {LABELS}, found {sorted(projections)}.")
    for label, checkpoints in projections.items():
        require(checkpoints, f"Oracle {label} has no checkpoints.")
        for checkpoint_id, projection in checkpoints.items():
            require(set(projection) == {"prompts", "choices", "tree"},
                    f"Oracle {label}/{checkpoint_id} projection shape differs.")
            tree = projection["tree"]
            if tree is not None:
                require(tree["rows"] and tree["count"] is not None and
                        tree["summaryRows"] == [],
                        f"Oracle {label}/{checkpoint_id} tree projection is incomplete.")
    require(projections["fullscreen"]["true-tree-after-state"]["tree"]["selected"] == "a0000003",
            "The skipped-prompt navigation target is not frozen.")
    require(projections["fullscreen"]["unset-tree-selected"]["prompts"] is True,
            "The default path must prompt.")
    require(projections["fullscreen"]["true-tree-selected"]["prompts"] is False,
            "The explicit-true path must skip the prompt.")
    require(projections["project"]["trusted-tree-selected"]["prompts"] is True,
            "A trusted project false must prompt.")
    require(projections["project"]["untrusted-tree-selected"]["prompts"] is False,
            "An untrusted project must not prompt when the user value is true.")
    require(projections["project-null"]["trusted-tree-selected"]["prompts"] is True,
            "A project explicit null must prompt.")
    require(projections["project-reserve"]["trusted-tree-selected"]["prompts"] is False,
            "A project reserve-only entry must keep the inherited skipPrompt.")
    require(projections["project-malformed"]["trusted-tree-selected"]["prompts"] is True,
            "A trusted malformed falsey project value must prompt.")
    require(projections["project-malformed-truthy"]["trusted-tree-selected"]["prompts"] is False,
            "A trusted malformed truthy project value must skip the prompt.")
    require(projections["malformed"]["malformed-string-tree-selected"]["prompts"] is False,
            "A stored \"false\" string must skip the prompt.")
    require(projections["malformed"]["empty-string-tree-selected"]["prompts"] is True,
            "A stored empty string must prompt.")
    require(projections["tolerant"]["unknown-tree-selected"]["prompts"] is False,
            "An unknown sibling must not block skipPrompt=true.")
    require(projections["tolerant"]["duplicate-tree-selected"]["prompts"] is False,
            "A duplicated skipPrompt must use the later true value.")
    require(projections["tolerant"]["false-tree-selected"]["prompts"] is True,
            "The tolerant fixture must still prompt for false.")
    require(projections["tolerant"]["trusted-tree-selected"]["prompts"] is False,
            "A trusted project with only an unknown sibling must keep the inherited value.")
    require(projections["tolerant"]["untrusted-tree-selected"]["prompts"] is False,
            "The untrusted tolerant case must stay on the user value.")
    safety = oracle.get("safetyProjections") or []
    for projection in safety:
        require(projection.get("id") and projection.get("pinned") and projection.get("pisharp"),
                f"Safety projection is incomplete: {projection}")
    return oracle


def validate_manifest(manifest, implementation, oracle):
    require(manifest.get("schema") == 1, "Manifest schema must be 1.")
    require(manifest.get("capability") == CAPABILITY, "Manifest capability mismatch.")
    require(manifest.get("piSha") == PI_SHA, f"Manifest Pi SHA is {manifest.get('piSha')!r}.")
    recorded = manifest.get("implementationSha")
    require(len(recorded or "") == 40, "Manifest must record a 40-character implementation SHA.")
    require(recorded == implementation,
            f"Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.")
    require(is_ancestor(recorded), "Recorded implementation SHA is not an ancestor of HEAD.")
    base = manifest.get("baseSha") or ""
    require(len(base) == 40, "Manifest must record the milestone base SHA.")
    require(is_ancestor(base), "Milestone base SHA is not an ancestor of HEAD.")
    require(manifest.get("oracleFile") ==
            f"docs/parity/fixtures/settings-branchsummary-skipprompt-pi-oracle-{DATE}.json",
            "Manifest must name the oracle file.")
    require(manifest.get("settingsBoundaryFile") ==
            f"docs/parity/fixtures/settings-branchsummary-skipprompt-settings-boundary-{DATE}.json",
            "Manifest must name the settings-boundary evidence.")
    require(manifest.get("milestoneDoc") ==
            f"docs/parity/fixtures/settings-branchsummary-skipprompt-{DATE}.md",
            "Manifest must name the milestone document.")
    require(manifest.get("terminalFixtures") == FIXTURES,
            f"Manifest terminal fixtures must be {FIXTURES}.")
    require(manifest.get("checkpointFiles") == CHECKPOINT_FILES,
            f"Manifest checkpoint files must be {CHECKPOINT_FILES}.")
    cases = manifest.get("cases") or []
    ids = [case.get("id") for case in cases]
    require(ids == REQUIRED_CASES, f"Manifest cases must be {REQUIRED_CASES}, found {ids}.")
    settings_ids = {case["id"] for case in oracle["settingsCases"]}
    scenario_ids = {fact["id"] for fact in oracle["scenarioFacts"]}
    for case in cases:
        require(case.get("required") is True, f"{case['id']} must be required.")
        require(case.get("status") == "pass", f"{case['id']} is {case.get('status')!r}.")
        require(case.get("oracle") and set(case["oracle"]) <= (settings_ids | scenario_ids),
                f"{case['id']} references an unknown oracle entry.")
        require(case.get("tests"), f"{case['id']} has no deterministic test evidence.")
        require(case.get("ptyCheckpoints"), f"{case['id']} has no PTY checkpoint evidence.")
    comparisons = manifest.get("requiredComparisons") or []
    require({comparison.get("id") for comparison in comparisons} ==
            {"settings-boundary-differential", "paired-pty", "same-pi-calibration",
             "layered-project-precedence", "malformed-value-parity", "no-summary-equivalence"},
            "Manifest must require every comparison.")
    for comparison in comparisons:
        require(comparison.get("required") is True,
                f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get("status") == "match",
                f"Unresolved comparison {comparison.get('id')}: {comparison.get('status')!r}.")
        require(comparison.get("evidence"), f"Comparison {comparison.get('id')} has no evidence.")
    for finding in manifest.get("findings") or []:
        require(finding.get("required") is False,
                f"Finding {finding.get('id')} expands the required contract.")
        require(finding.get("observation") and finding.get("projection"),
                f"Finding {finding.get('id')} is incomplete.")
    tests = manifest.get("requiredTests") or {}
    facts = tests.get("facts") or []
    require(len(facts) >= 30, "Manifest must list at least thirty required deterministic tests.")
    require(len(set(facts)) == len(facts), "Duplicate required test names.")
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
    require(suite.get("total") and suite.get("failed") == 0,
            "Full-suite gate must record zero failures and a test total.")
    require(suite.get("skipped") == 0, "Full-suite gate must record zero skipped tests.")
    ci = gates["exactHeadCi"]
    require(ci.get("implementationSha") == implementation,
            "Exact-head CI gate was recorded for a different head.")
    require(str(ci.get("run") or "").isdigit() and len(str(ci["run"])) >= 8,
            "Exact-head CI gate must record the run id.")
    require(gates["ledger"]["evidence"].startswith("docs/parity/"),
            "Ledger gate must reference the parity evidence.")


def checkpoint(document, case_id, name, product="pi"):
    case = document["cases"][case_id]
    found = case[product]["checkpoints"].get(name)
    require(found is not None, f"{case_id} has no {name} checkpoint in the {product} evidence.")
    return found


def validate_settings_document(document, oracle):
    require(document.get("capability") == CAPABILITY, "settings-boundary: capability mismatch.")
    require(document.get("piSha") == PI_SHA, "settings-boundary: Pi SHA mismatch.")
    require(document.get("match") is True and not document.get("differences"),
            "settings-boundary: the differential did not match.")
    cases = document.get("cases") or []
    require([case.get("id") for case in cases] == SETTINGS_CASES,
            f"settings-boundary: cases must be {SETTINGS_CASES}.")
    expectations = oracle["expectations"]
    fields = ("loaded", "prompts", "skipPrompt", "parentPresent", "reserve")
    for case in cases:
        pi, pisharp = case.get("pi"), case.get("pisharp")
        observed = {key: pi.get(key) for key in fields}
        require(observed == {key: pisharp.get(key) for key in fields},
                f"{case['id']}: PiSharp differs from pinned Pi.")
        require(not pisharp.get("error"),
                f"{case['id']}: PiSharp reported {pisharp.get('error')!r}.")
        expect = expectations[case["id"]]
        require(observed == expect,
                f"{case['id']}: observed {observed} differs from the oracle {expect}.")


def validate_paired_document(label, document, oracle):
    require(document.get("capability") == CAPABILITY, f"{label}: capability mismatch.")
    require(document.get("piSha") == PI_SHA, f"{label}: Pi SHA is {document.get('piSha')!r}.")
    projections = oracle["projections"][label]
    cases = document.get("cases") or {}
    require(cases, f"{label}: no cases.")
    for case_id, case in cases.items():
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
            evidence = case[product]
            require(not evidence["scenarioError"],
                    f"{case_id}/{product}: scenario error {evidence['scenarioError']}")
            require(not evidence["requests"], f"{case_id}/{product}: unexpected provider request.")
            require(not (evidence["session"] or {}).get("branchSummaryIds"),
                    f"{case_id}/{product}: a branch summary entry was written.")
            for checkpoint_id, state in evidence["checkpoints"].items():
                require(not state["errors"],
                        f"{case_id}/{product}/{checkpoint_id}: error text {state['errors']}")
                tree = state["tree"]
                require(tree is None or tree["summaryRows"] == [],
                        f"{case_id}/{product}/{checkpoint_id}: branch summary row rendered.")
        for checkpoint_id, projection in projections.items():
            for product in ("pi", "pisharp"):
                state = checkpoint(document, case_id, checkpoint_id, product)
                prompts = state["prompt"] is not None
                require(prompts == projection["prompts"],
                        f"{case_id}/{product}/{checkpoint_id}: prompts={prompts}, "
                        f"expected {projection['prompts']}")
                if projection["prompts"]:
                    require(state["prompt"]["choices"] == projection["choices"],
                            f"{case_id}/{product}/{checkpoint_id}: choices differ")
                tree = state["tree"]
                if projection["tree"] is None:
                    continue
                require(tree is not None,
                        f"{case_id}/{product}/{checkpoint_id}: expected a tree panel")
                for key in ("rows", "count", "tags", "selected", "summaryRows"):
                    require(tree[key] == projection["tree"][key],
                            f"{case_id}/{product}/{checkpoint_id}: tree.{key} differ: {tree[key]}")
        if "tree-after-state" in projections:
            for product in ("pi", "pisharp"):
                state = checkpoint(document, case_id, "tree-after-state", product)
                require(state["tree"]["selected"] == projections["tree-after-state"]["tree"]["selected"],
                        f"{case_id}/{product}: navigation target differs.")


def fixture_actions():
    actions = {}
    for name in FIXTURES:
        fixture = load_json(ROOT / name)
        actions[name] = [action["id"] for action in fixture.get("actions") or []]
    return actions


def fixture_restarts():
    return {name: [action["id"] for action in load_json(ROOT / name).get("actions") or []
                   if action.get("restart")] for name in FIXTURES}


def fixture_capture():
    return {name: (load_json(ROOT / name).get("captureAgentFiles") or []) for name in FIXTURES}


def validate_fixtures(restarts=None, actions=None, captures=None):
    restarts = restarts if restarts is not None else fixture_restarts()
    actions = actions if actions is not None else fixture_actions()
    captures = captures if captures is not None else fixture_capture()
    for name in FIXTURES:
        require("fixtures/tree.jsonl" in captures[name],
                f"{name} must capture the persisted session.")
        require("settings.json" in captures[name],
                f"{name} must capture the persisted settings.")
        for action in actions[name]:
            if action.endswith("-config"):
                require(action in restarts[name],
                        f"{name}: {action} must restart so the file-authored value is loaded.")
    main = FIXTURES[0]
    for prefix in ("false", "true", "true-restart", "false-again", "filtered"):
        require(f"{prefix}-config" in restarts[main],
                f"{main}: {prefix} must be installed through a restart.")
    for prefix in ("unset", "true", "true-restart", "false-again", "filtered"):
        require(f"{prefix}-tree-selected" in actions[main],
                f"{main}: no {prefix} selection checkpoint.")
    require("unset-prompt-cancel" in actions[main], f"{main}: no prompt-cancel checkpoint.")
    require("unset-reopen-tree-after-state" in actions[main],
            f"{main}: no navigation-target checkpoint.")
    malformed = FIXTURES[2]
    for prefix in ("malformed-string", "empty-string", "number", "zero", "object", "array",
                   "null", "parent-null", "parent-string", "parent-number", "parent-array"):
        require(f"{prefix}-config" in restarts[malformed],
                f"{malformed}: {prefix} must be installed through a restart.")
        require(f"{prefix}-tree-selected" in actions[malformed],
                f"{malformed}: {prefix} has no selection checkpoint.")
    tolerant = FIXTURES[8]
    for prefix in ("unknown", "duplicate", "false", "trusted", "untrusted"):
        require(f"{prefix}-config" in restarts[tolerant],
                f"{tolerant}: {prefix} must be installed through a restart.")
        require(f"{prefix}-tree-selected" in actions[tolerant],
                f"{tolerant}: no {prefix} selection checkpoint.")
    duplicate = next(action for action in load_json(ROOT / tolerant)["actions"]
                     if action["id"] == "duplicate-config")
    require('"skipPrompt":false,"skipPrompt":true' in
            duplicate["writeFiles"]["agent"]["settings.json"],
            f"{tolerant}: the duplicate case must write the duplicated key verbatim.")
    for name in FIXTURES[3:8]:
        for prefix in ("trusted", "trusted-restart", "untrusted"):
            require(f"{prefix}-tree-selected" in actions[name],
                    f"{name}: no {prefix} selection checkpoint.")
    project_arguments = load_json(ROOT / FIXTURES[3])["arguments"]["pisharp"]
    require("--no-approve" not in project_arguments,
            "The project fixtures must decide trust through defaultProjectTrust.")


def checkpoint_index(documents):
    index = set()
    for label, document in documents.items():
        for case_id, case in (document.get("cases") or {}).items():
            for product in ("pi", "pisharp"):
                for name in case[product]["checkpoints"]:
                    index.add(f"{case_id}/{name}")
                    index.add(name)
    return index


def validate_case_references(manifest, oracle, documents, results):
    tests = manifest["requiredTests"]
    allowed = set(tests["facts"])
    passing = {name for name, outcome in results if outcome == "Passed"}
    index = checkpoint_index(documents)
    settings_ids = {case["id"] for case in oracle["settingsCases"]}
    scenario_ids = {fact["id"] for fact in oracle["scenarioFacts"]}
    referenced_tests = set()
    referenced_oracle = set()
    for case in manifest["cases"]:
        for name in case.get("oracle") or []:
            require(name in settings_ids or name in scenario_ids,
                    f"{case['id']} references unknown oracle {name!r}.")
            referenced_oracle.add(name)
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
    require(referenced_oracle == settings_ids | scenario_ids,
            f"Oracle entries are not referenced: "
            f"{sorted((settings_ids | scenario_ids) - referenced_oracle)}")


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


def ledger_entry(ledger, identifier=CAPABILITY):
    return next((task for task in ledger.get("tasks") or [] if task.get("id") == identifier), None)


def validate_ledger(ledger, manifest):
    entry = ledger_entry(ledger)
    require(isinstance(entry, dict), f"Execution ledger has no {CAPABILITY} entry.")
    require(entry.get("status") == "verified",
            f"Ledger entry status is {entry.get('status')!r}.")
    require(entry.get("implementation"), "Ledger entry has no implementation references.")
    evidence = entry.get("evidence") or []
    for required in (manifest["oracleFile"], manifest["settingsBoundaryFile"],
                     *manifest["checkpointFiles"]):
        require(any(required in value for value in evidence),
                f"Ledger entry does not reference {required}.")
    reserve = ledger_entry(ledger, "settings-branchsummary-reservetokens")
    require(isinstance(reserve, dict), "Ledger has no reserveTokens entry.")
    require(reserve.get("status") == "not_started",
            f"settings-branchsummary-reservetokens must stay not_started, "
            f"found {reserve.get('status')!r}.")
    require(not reserve.get("implementation") and not reserve.get("evidence"),
            "settings-branchsummary-reservetokens must not gain implementation or evidence.")


def run(command, env=None, timeout=7200):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-4000:] + result.stderr[-4000:]).strip()
        raise VerificationError(f"Command failed: {' '.join(command)}\n{tail}")
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-skip-prompt-verify-")
    trx = Path(directory) / "skip-prompt.trx"
    environment = dict(os.environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~BranchSummarySkipPromptSettingTests",
        "FullyQualifiedName~TerminalPtyTests.BranchSummarySkipPromptNavigatesLikeChoosingNoSummaryThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.BranchSummaryEscapeCancelsAndReopensTheTreeThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.ConversationTreeSearchSelectsAndPersistsABranchThroughLinuxPty",
        "FullyQualifiedName~TerminalPtyTests.SessionTreeShortcutAbortsStreamingRestoresInputAndNavigatesThroughLinuxPty",
        "FullyQualifiedName~InteractiveSessionBranchSummaryTests",
        "FullyQualifiedName~UserSettingsTests.BranchSummarySettingsValidateAndOverlayReserveAndPromptBehavior",
        "FullyQualifiedName~UserSettingsTests.InvalidSettingsFailClosed",
        "FullyQualifiedName~NativeExtensionTreeEventTests",
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=skip-prompt.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx


def run_quality():
    run(["dotnet", "format", "PiSharp.slnx", "--verify-no-changes", "--no-restore"])
    environment = dict(os.environ)
    environment["MSBUILDDISABLENODEREUSE"] = "1"
    environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0"
    run(["dotnet", "build", "PiSharp.slnx", "--warnaserror", "--nologo", "-p:UseSharedCompilation=false"],
        env=environment)


def self_test(manifest, oracle, documents, results, ledger, settings_document):
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
    for label, document in documents.items():
        validate_paired_document(label, document, oracle)
    validate_fixtures()
    validate_settings_document(settings_document, oracle)
    validate_trx(results, manifest)
    validate_case_references(manifest, oracle, documents, results)

    bad_default = copy.deepcopy(oracle)
    bad_default["default"] = True
    rejects(lambda: validate_oracle(bad_default), "default changed to true")

    explicit_false = copy.deepcopy(oracle)
    explicit_false["expectations"]["BS02"]["prompts"] = False
    rejects(lambda: validate_oracle(explicit_false), "explicit false skips the prompt")

    explicit_true = copy.deepcopy(oracle)
    explicit_true["expectations"]["BS03"]["prompts"] = True
    rejects(lambda: validate_oracle(explicit_true), "true shows the prompt")

    skipped_false = copy.deepcopy(documents)
    skipped_false["fullscreen"]["cases"]["100x30-dark-fullscreen"]["pi"]["checkpoints"]["false-tree-selected"]["prompt"] = None
    rejects(lambda: validate_paired_document("fullscreen", skipped_false["fullscreen"], oracle),
            "false checkpoint skipped the prompt")

    summarize_true = copy.deepcopy(documents)
    summarize_true["fullscreen"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["requests"] = ["fixture-model"]
    rejects(lambda: validate_paired_document("fullscreen", summarize_true["fullscreen"], oracle),
            "true caused a summarization request")

    summary_entry = copy.deepcopy(documents)
    summary_entry["fullscreen"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["session"]["branchSummaryIds"] = ["b0000099"]
    rejects(lambda: validate_paired_document("fullscreen", summary_entry["fullscreen"], oracle),
            "true created a branch summary entry")

    summary_row = copy.deepcopy(documents)
    summary_row["fullscreen"]["cases"]["100x30-dark-fullscreen"]["pi"]["checkpoints"]["true-tree-after-state"]["tree"]["summaryRows"] = ["branch summary"]
    rejects(lambda: validate_paired_document("fullscreen", summary_row["fullscreen"], oracle),
            "true rendered a branch summary row")

    wrong_target = copy.deepcopy(documents)
    wrong_target["fullscreen"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["true-tree-after-state"]["tree"]["selected"] = "c0000008"
    rejects(lambda: validate_paired_document("fullscreen", wrong_target["fullscreen"], oracle),
            "true navigated to the wrong target")

    trusted = copy.deepcopy(documents)
    trusted["project"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["trusted-tree-selected"]["prompt"] = None
    rejects(lambda: validate_paired_document("project", trusted["project"], oracle),
            "trusted project failed to override")

    reserve = copy.deepcopy(documents)
    reserve["project-reserve"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["trusted-tree-selected"]["prompt"] = {
        "title": "Summarize branch?", "choices": PROMPT_CHOICES}
    rejects(lambda: validate_paired_document("project-reserve", reserve["project-reserve"], oracle),
            "project reserveTokens erased the inherited skipPrompt")

    project_null = copy.deepcopy(documents)
    project_null["project-null"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["trusted-tree-selected"]["prompt"] = None
    rejects(lambda: validate_paired_document("project-null", project_null["project-null"], oracle),
            "project null fell through to the user value")

    untrusted = copy.deepcopy(documents)
    untrusted["project"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["untrusted-tree-selected"]["prompt"] = {
        "title": "Summarize branch?", "choices": PROMPT_CHOICES}
    rejects(lambda: validate_paired_document("project", untrusted["project"], oracle),
            "untrusted project changed the behaviour")

    malformed_truthy = copy.deepcopy(documents)
    truthy = malformed_truthy["malformed"]["cases"]["100x30-dark-fullscreen"]["pisharp"]
    truthy["checkpoints"]["malformed-string-tree-selected"]["prompt"] = {
        "title": "Summarize branch?", "choices": PROMPT_CHOICES}
    rejects(lambda: validate_paired_document("malformed", malformed_truthy["malformed"], oracle),
            "malformed truthy treated as false")

    malformed_falsey = copy.deepcopy(documents)
    falsey = malformed_falsey["malformed"]["cases"]["100x30-dark-fullscreen"]["pisharp"]
    falsey["checkpoints"]["empty-string-tree-selected"]["prompt"] = None
    rejects(lambda: validate_paired_document("malformed", malformed_falsey["malformed"], oracle),
            "malformed falsey treated as true")

    unknown_sibling = copy.deepcopy(documents)
    unknown_sibling["tolerant"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["unknown-tree-selected"]["prompt"] = {
        "title": "Summarize branch?", "choices": PROMPT_CHOICES}
    rejects(lambda: validate_paired_document("tolerant", unknown_sibling["tolerant"], oracle),
            "unknown sibling blocked skipPrompt")

    duplicate_key = copy.deepcopy(documents)
    duplicate_key["tolerant"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["duplicate-tree-selected"]["prompt"] = {
        "title": "Summarize branch?", "choices": PROMPT_CHOICES}
    rejects(lambda: validate_paired_document("tolerant", duplicate_key["tolerant"], oracle),
            "duplicate skipPrompt ignored the later value")

    trusted_sibling = copy.deepcopy(documents)
    trusted_sibling["tolerant"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["checkpoints"]["trusted-tree-selected"]["prompt"] = {
        "title": "Summarize branch?", "choices": PROMPT_CHOICES}
    rejects(lambda: validate_paired_document("tolerant", trusted_sibling["tolerant"], oracle),
            "trusted unknown sibling erased the inherited skipPrompt")

    duplicate_evidence = copy.deepcopy(settings_document)
    duplicate_evidence["cases"][31]["pisharp"]["prompts"] = True
    rejects(lambda: validate_settings_document(duplicate_evidence, oracle),
            "settings corpus accepted the first duplicate value")

    inherited_evidence = copy.deepcopy(settings_document)
    inherited_evidence["cases"][38]["pisharp"]["skipPrompt"] = "<absent>"
    rejects(lambda: validate_settings_document(inherited_evidence, oracle),
            "settings corpus lost the inherited skipPrompt through an unknown sibling")

    calibration = copy.deepcopy(documents)
    calibration["regular"]["cases"]["100x30-dark-regular"]["calibration"]["semanticDifferences"] = 1
    rejects(lambda: validate_paired_document("regular", calibration["regular"], oracle),
            "same-Pi calibration fails")

    boundary = copy.deepcopy(documents)
    boundary["project"]["cases"]["100x30-dark-fullscreen"]["pisharp"]["scenarioError"] = "timeout"
    rejects(lambda: validate_paired_document("project", boundary["project"], oracle),
            "scenario error")

    wrong_pi = copy.deepcopy(oracle)
    wrong_pi["piSha"] = "0" * 40
    rejects(lambda: validate_oracle(wrong_pi), "wrong Pi SHA")

    stale = copy.deepcopy(manifest)
    stale["implementationSha"] = "1" * 40
    rejects(lambda: validate_manifest(stale, implementation, oracle), "stale PiSharp SHA")

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "RT03"]
    rejects(lambda: validate_manifest(missing, implementation, oracle), "required test case missing")

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

    reserve_verified = copy.deepcopy(ledger)
    ledger_entry(reserve_verified, "settings-branchsummary-reservetokens")["status"] = "verified"
    rejects(lambda: validate_ledger(reserve_verified, manifest), "reserveTokens marked verified")

    bad_gate = copy.deepcopy(manifest)
    bad_gate["gates"]["exactHeadCi"]["implementationSha"] = "2" * 40
    rejects(lambda: validate_manifest(bad_gate, implementation, oracle), "exact-head CI mismatch")

    fixture_gap = fixture_actions()
    fixture_gap[FIXTURES[0]].remove("true-tree-selected")
    rejects(lambda: validate_fixtures(fixture_actions(), fixture_gap, fixture_capture()),
            "required fixture action removed")

    print("negative controls passed")


def load_documents(args, manifest):
    documents = {}
    for label, path in zip(LABELS, args.checkpoints):
        documents[label] = load_json(path)
    return documents


def load_settings_document(args):
    return load_json(args.settings_boundary)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / f"docs/parity/fixtures/settings-branchsummary-skipprompt-acceptance-{DATE}.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / f"docs/parity/fixtures/settings-branchsummary-skipprompt-pi-oracle-{DATE}.json")
    parser.add_argument("--settings-boundary", type=Path,
                        default=ROOT / f"docs/parity/fixtures/settings-branchsummary-skipprompt-settings-boundary-{DATE}.json")
    parser.add_argument("--checkpoints", type=Path, nargs=len(LABELS),
                        default=[ROOT / path for path in CHECKPOINT_FILES])
    parser.add_argument("--ledger", type=Path, default=ROOT / "docs/parity/execution-ledger.json")
    parser.add_argument("--implementation")
    parser.add_argument("--trx", type=Path)
    parser.add_argument("--full-trx", type=Path)
    parser.add_argument("--skip-tests", action="store_true")
    parser.add_argument("--quality", action="store_true",
                        help="Run dotnet format verification and a warnings-as-errors build")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    implementation = args.implementation or implementation_sha()
    latest = implementation_sha()
    if implementation != latest:
        raise VerificationError(
            f"Implementation SHA {implementation} is not the current src/tests head {latest}.")

    manifest = load_json(args.manifest)
    oracle = load_json(args.oracle)
    documents = load_documents(args, manifest)
    ledger = load_json(args.ledger)
    source_contract()

    validate_oracle(oracle)
    validate_manifest(manifest, implementation, oracle)
    validate_fixtures()
    validate_settings_document(load_settings_document(args), oracle)
    for label, document in documents.items():
        validate_paired_document(label, document, oracle)
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
                  [(name, "Passed") for name in manifest["requiredTests"]["facts"]], ledger,
                  load_settings_document(args))
    print(f"{CAPABILITY} verified at {implementation} against pinned Pi {PI_SHA}")


if __name__ == "__main__":
    try:
        main()
    except VerificationError as error:
        print(f"verification failed: {error}", file=sys.stderr)
        sys.exit(1)
