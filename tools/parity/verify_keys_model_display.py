#!/usr/bin/env python3
"""Verify the keys-model-display acceptance manifest and committed evidence."""
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
REQUIRED_CASES = [f"M{index:02d}" for index in range(1, 17)]
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
CASE_IDS = (
    "80x24-dark-fullscreen",
    "80x24-dark-regular",
    "120x40-dark-fullscreen",
    "120x40-dark-regular",
)
EXIT_CHECKPOINT_SUFFIXES = ("quit", "exit")
TEMPORARY_PRIVATE_MODES = ("1049", "1000", "1002", "1003", "1004", "1006", "2004", "2026")


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


def validate_manifest(manifest, implementation):
    require(manifest.get("schema") == 1, "Manifest schema must be 1.")
    require(manifest.get("capability") == "keys-model-display",
            "Manifest capability must be keys-model-display.")
    require(manifest.get("piSha") == PI_SHA,
            f"Manifest Pi SHA is {manifest.get('piSha')!r}, expected {PI_SHA}.")
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
        require(any(case.get(key) for key in ("oracle", "tests", "pty", "terminalCheckpoints")),
                f"{case['id']} has no executable evidence.")
    comparisons = manifest.get("requiredComparisons") or []
    require(comparisons, "Manifest has no required comparisons.")
    for comparison in comparisons:
        require(comparison.get("required") is True, f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get("status") == "match",
                f"Unresolved required comparison {comparison.get('id')}: {comparison.get('status')!r}.")
    for finding in manifest.get("findings") or []:
        require(finding.get("required") is False, f"Finding {finding.get('id')} expands the required contract.")
    tests = manifest.get("requiredTests") or {}
    require(tests.get("theories") and tests.get("facts") and tests.get("ptyEvidence"),
            "Manifest must list required theories, facts and PTY evidence ids.")
    require((manifest.get("terminalFixtures") or []) == ["tools/parity/terminal/fixtures/terminal-keys-model-display.json"],
            "Manifest must name the keys-model-display terminal fixture.")


def validate_oracle(oracle, manifest, pi_checkout=None):
    require(oracle.get("capability") == "keys-model-display",
            "Oracle capability must be keys-model-display.")
    require(oracle.get("piSha") == manifest.get("piSha"),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get("scenarios") or {}
    require(len(scenarios) == manifest.get("oracleScenarioCount"),
            f"Oracle has {len(scenarios)} scenarios, manifest claims {manifest.get('oracleScenarioCount')}.")
    require(set(scenarios) == set(REQUIRED_CASES), f"Oracle scenarios must be {REQUIRED_CASES}, found {sorted(scenarios)}.")
    for name, scenario in scenarios.items():
        require(scenario.get("source"), f"Oracle scenario {name!r} has no source references.")
        require(scenario.get("expected"), f"Oracle scenario {name!r} has no expected observation.")
        require(any(key in scenario["expected"] for key in (
            "bindings", "bindingsUnix", "bindingsWindows", "display", "displayUnix", "displayWindows",
            "thinking", "status", "activeActions", "rebindable", "selectorScopeDefault",
            "copyOnSelectDefault", "contextPreserved", "thinkingPersisted")),
            f"Oracle scenario {name!r} has no executable expectation.")
    pty = oracle.get("ptyScenarios") or {}
    require(pty, "Oracle has no paired PTY scenarios.")
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


def checkpoint_index(checkpoints):
    index = {}
    by_case = {}
    for case_id, case in (checkpoints.get("cases") or {}).items():
        prefix = f"terminal-keys-model-display/{case_id}"
        for checkpoint_id, value in case["pi"]["checkpoints"].items():
            index[f"{prefix}/{checkpoint_id}"] = value
            by_case.setdefault(prefix, []).append(f"{prefix}/{checkpoint_id}")
    return index, by_case


def resolve_checkpoint(reference, index, by_case):
    if reference in index:
        return [reference]
    matches = by_case.get(reference, [])
    require(matches, f"Nonexistent terminal checkpoint reference: {reference!r}.")
    return sorted(matches)


def validate_terminal_fixture(manifest):
    fixture_path = ROOT / manifest["terminalFixtures"][0]
    fixture = load_json(fixture_path)
    require(fixture.get("id") == "terminal-keys-model-display", "Terminal fixture id is wrong.")
    actions = fixture.get("actions") or []
    ids = [action.get("id") for action in actions]
    for expected in ("model-cycle-forward", "model-cycle-backward", "selector-open", "selector-confirm-beta",
                     "first-prompt", "second-prompt", "thinking-cycle", "tool-expand", "thinking-toggle",
                     "copy-last-assistant", "quit"):
        require(expected in ids, f"Terminal fixture is missing action {expected!r}.")
    copy_action = next(action for action in actions if action["id"] == "copy-last-assistant")
    require(copy_action.get("expectClipboardWrite") is True, "Copy action has no clipboard fixture assertion.")
    require(copy_action.get("send") == "\u0018", "Copy action does not exercise Ctrl+X.")
    for product in ("pi", "pisharp"):
        models = (((fixture.get("files") or {}).get(product) or {}).get("models.json") or {})
        models = (((models.get("providers") or {}).get("fixture") or {}).get("models") or [])
        require(len(models) >= 3, f"Terminal fixture {product} needs at least three models.")
        require(any(model.get("reasoning") for model in models),
                f"Terminal fixture {product} has no reasoning model.")
    require({"fullscreen", "regular"} <= set(fixture.get("modes") or []),
            "Terminal fixture must exercise fullscreen and regular modes.")


def validate_checkpoints(checkpoints, manifest):
    require(checkpoints.get("piSha") == manifest.get("piSha"),
            f"Checkpoint pin is {checkpoints.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    cases = checkpoints.get("cases") or {}
    require(set(cases) == set(CASE_IDS), f"Checkpoint cases must be {list(CASE_IDS)}, found {sorted(cases)}.")
    dimensions = {case_id.split("-")[0] for case_id in cases}
    modes = {case_id.split("-")[-1] for case_id in cases}
    require(len(dimensions) >= 2, f"Paired evidence only used dimensions {sorted(dimensions)}.")
    require({"fullscreen", "regular"} <= modes, f"Paired evidence modes are {sorted(modes)}.")
    for case_id, case in cases.items():
        calibration = case.get("calibration") or {}
        require(not calibration.get("scenarioErrors"),
                f"{case_id} calibration scenario errors: {calibration.get('scenarioErrors')}")
        require(calibration.get("differences") == 0,
                f"{case_id} calibration has {calibration.get('differences')} differences.")
        require(calibration.get("httpMatch") is True, f"{case_id} calibration HTTP traces differ.")
        paired = case.get("paired") or {}
        require(not paired.get("scenarioErrors"), f"{case_id} paired scenario errors: {paired.get('scenarioErrors')}")
        require(paired.get("httpMatch") is True, f"{case_id} provider HTTP traces differ.")
        require(paired.get("clipboardMatch") is True, f"{case_id} clipboard writes differ.")
        pi = case["pi"]["checkpoints"]
        pisharp = case["pisharp"]["checkpoints"]
        require(set(pi) == set(pisharp), f"{case_id} products captured different checkpoint ids.")
        for checkpoint_id in pi:
            left, right = pi[checkpoint_id], pisharp[checkpoint_id]
            is_exit = checkpoint_id.endswith(EXIT_CHECKPOINT_SUFFIXES)
            require(left["editor"] == right["editor"],
                    f"{case_id}/{checkpoint_id} editor text differs.")
            require(left["cursor"].get("x") == right["cursor"].get("x"),
                    f"{case_id}/{checkpoint_id} cursor column differs.")
            if not is_exit:
                require(left.get("editorRow") == right.get("editorRow"),
                        f"{case_id}/{checkpoint_id} editor-relative cursor row differs.")
            require(left.get("activeScreen") == right.get("activeScreen"),
                    f"{case_id}/{checkpoint_id} active screen differs.")
            require(left.get("temporaryModes") == right.get("temporaryModes"),
                    f"{case_id}/{checkpoint_id} temporary terminal modes differ.")
            require(left.get("bracketedPasteMode") == right.get("bracketedPasteMode"),
                    f"{case_id}/{checkpoint_id} bracketed-paste mode differs.")
            require(left.get("footer") == right.get("footer"),
                    f"{case_id}/{checkpoint_id} model/thinking footer differs.")
            require(left.get("status") == right.get("status"),
                    f"{case_id}/{checkpoint_id} latest status differs.")
            if is_exit:
                require(left["activeScreen"] == "normal" and right["activeScreen"] == "normal",
                        f"{case_id}/{checkpoint_id} did not restore the normal screen.")
                require(left["cursor"].get("visible") is True and right["cursor"].get("visible") is True,
                        f"{case_id}/{checkpoint_id} did not restore a visible cursor.")
                require(not left["temporaryModes"] and not right["temporaryModes"],
                        f"{case_id}/{checkpoint_id} left temporary terminal modes active.")
        require(case["pi"]["requests"] == case["pisharp"]["requests"],
                f"{case_id} provider user requests differ: {case['pi']['requests']!r} != {case['pisharp']['requests']!r}.")
        require(case["pi"]["requestFacts"] == case["pisharp"]["requestFacts"],
                f"{case_id} provider request facts differ.")
        facts = case["pi"]["requestFacts"]
        require([fact.get("model") for fact in facts] == ["beta", "alpha"],
                f"{case_id} request models are {[fact.get('model') for fact in facts]!r}, expected beta then alpha.")
        require(all(fact.get("thinking") == "medium" for fact in facts),
                f"{case_id} requests did not use the medium thinking level.")
        require(len(facts) >= 2, f"{case_id} did not exercise context preservation across a model change.")
        second_history = facts[1]["history"]
        required_history = ["user:first prompt", "assistant:fixture reply", "user:second prompt"]
        positions = [second_history.index(item) for item in required_history if item in second_history]
        require(len(positions) == len(required_history) and positions == sorted(positions),
                f"{case_id} second provider request did not preserve prior messages in order: {second_history!r}.")
    index, by_case = checkpoint_index(checkpoints)
    return index, by_case, dimensions, modes


def validate_case_references(manifest, oracle, results, index, by_case):
    tests = manifest["requiredTests"]
    allowed_tests = set(tests["facts"]) | set(tests["theories"])
    passing = {name for name, outcome in results if outcome == "Passed"}
    scenario_names = set(oracle["scenarios"])
    referenced_tests = set()
    for case in manifest["cases"]:
        for name in case.get("oracle") or []:
            require(name in scenario_names, f"{case['id']} references a nonexistent oracle scenario: {name!r}.")
        for method in case.get("tests") or []:
            require(method in allowed_tests, f"{case['id']} references a test outside requiredTests: {method!r}.")
            require(any(name == method or name.startswith(method + "(") for name in passing),
                    f"{case['id']} references a test that did not pass: {method!r}.")
            referenced_tests.add(method)
        for checkpoint in case.get("terminalCheckpoints") or []:
            require(resolve_checkpoint(checkpoint, index, by_case),
                    f"{case['id']} references a missing terminal checkpoint: {checkpoint!r}.")
    require(referenced_tests == allowed_tests - set(tests["theories"]),
            f"Required facts are not referenced by any case: {sorted(allowed_tests - set(tests['theories']) - referenced_tests)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in (case.get("oracle") or [])}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced by any case: {sorted(scenario_names - referenced_scenarios)}")
    referenced_evidence = set(tests["ptyEvidence"])
    require(referenced_evidence <= set(by_case),
            f"PTY evidence ids are not checkpoint cases: {sorted(referenced_evidence - set(by_case))}")
    m16 = next(case for case in manifest["cases"] if case["id"] == "M16")
    distinct = set(m16.get("terminalCheckpoints") or [])
    require(len(distinct) >= 6, f"M16 names {len(distinct)} checkpoint observations, expected at least 6.")
    for dimension in ("80x24", "120x40"):
        require(any(entry.startswith(f"terminal-keys-model-display/{dimension}-") for entry in distinct),
                f"M16 has no {dimension} checkpoint.")


def validate_oracle_pty_scenarios(oracle, index, by_case):
    for case_id, references in oracle["ptyScenarios"].items():
        require(references, f"Oracle PTY scenario {case_id} has no checkpoint references.")
        for reference in references:
            require(resolve_checkpoint(reference, index, by_case),
                    f"Oracle PTY scenario {case_id} references a missing checkpoint {reference!r}.")


def trx_results(path):
    try:
        tree = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise VerificationError(f"Cannot read TRX {path}: {error}") from error
    results = []
    for element in tree.getroot().iter("{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
        results.append((element.get("testName") or "", element.get("outcome") or ""))
    require(results, f"TRX {path} contains no test results.")
    return results


def validate_trx(results, manifest):
    tests = manifest["requiredTests"]
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    for method in tests["facts"]:
        matching = [(name, outcome) for name, outcome in results
                    if name == method or name.startswith(method + "(")]
        require(matching, f"Required test did not run: {method}")
        for name, outcome in matching:
            require(outcome not in skipped, f"Required test was skipped: {name} ({outcome}).")
            require(outcome == "Passed", f"Required test failed: {name} ({outcome}).")
    theory = tests["theories"][0]
    passed = [name for name, outcome in results if name.startswith(theory) and outcome == "Passed"]
    skipped_theories = [name for name, outcome in results
                        if name.startswith(theory) and outcome in skipped]
    require(not skipped_theories, f"Pinned-Pi oracle theory cases were skipped: {skipped_theories[:3]}")
    require(len(passed) == manifest["oracleScenarioCount"],
            f"Expected {manifest['oracleScenarioCount']} passing pinned-Pi theory cases, found {len(passed)}.")


def validate_full_trx(results):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    failures = [name for name, outcome in results if outcome != "Passed" and outcome not in skipped]
    skips = [name for name, outcome in results if outcome in skipped]
    require(not failures, f"Full suite has failing tests: {failures[:5]}")
    require(not skips, f"Full suite has skipped tests: {skips[:5]}")


def run(command, env=None, timeout=3600):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-4000:] + result.stderr[-4000:]).strip()
        raise VerificationError(f"Command failed: {' '.join(command)}\n{tail}")
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-model-display-verify-")
    trx = Path(directory) / "model-display.trx"
    environment = dict(os.environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~KeysModelDisplayTests",
        "FullyQualifiedName~KeysModelDisplayParityTests",
        "FullyQualifiedName~KeysModelDisplayPtyTests",
        "FullyQualifiedName~TerminalPtyTests.ShiftTabCyclesThinking",
        "FullyQualifiedName~TerminalPtyTests.ThinkingSelectorSelectsCancels",
        "FullyQualifiedName~TerminalPtyTests.ClipboardShortcutsPasteText",
        "FullyQualifiedName~TerminalPtyTests.SlashCommandTabCycles"
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=model-display.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx


def validate_all(manifest, oracle, checkpoints, results, index, by_case, pi=None):
    validate_manifest(manifest, manifest["implementationSha"])
    validate_oracle(oracle, manifest, pi)
    validate_terminal_fixture(manifest)
    validate_checkpoints(checkpoints, manifest)
    validate_oracle_pty_scenarios(oracle, index, by_case)
    validate_case_references(manifest, oracle, results, index, by_case)


def self_test(manifest, oracle, checkpoints, trx_results_value):
    def rejects(call, label):
        try:
            call()
        except VerificationError as error:
            print(f"negative control rejected: {label}: {error}")
            return
        raise VerificationError(f"Negative control accepted an invalid case: {label}")

    implementation = manifest["implementationSha"]
    validate_manifest(manifest, implementation)
    index, by_case = checkpoint_index(checkpoints)
    passing = [(name, "Passed") for name in manifest["requiredTests"]["facts"]] + [
        (f"{manifest['requiredTests']['theories'][0]}(name: \"M{index:02d}\")", "Passed")
        for index in range(1, 17)]

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "M16"]
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

    bad_fixture = copy.deepcopy(manifest)
    bad_fixture["terminalFixtures"] = ["tools/parity/terminal/fixtures/missing.json"]
    rejects(lambda: validate_terminal_fixture(bad_fixture), "missing terminal fixture")

    bad_oracle = copy.deepcopy(oracle)
    bad_oracle["scenarios"]["M01"].pop("expected")
    rejects(lambda: validate_oracle(bad_oracle, manifest), "incomplete oracle scenario")

    bad_sources = copy.deepcopy(oracle)
    bad_sources["sources"] = []
    rejects(lambda: validate_oracle_sources(bad_sources), "missing oracle source inventory")

    bad_case_ref = copy.deepcopy(manifest)
    bad_case_ref["cases"][0]["terminalCheckpoints"] = ["terminal-keys-model-display/does-not-exist/startup"]
    rejects(lambda: validate_case_references(bad_case_ref, oracle, passing, index, by_case),
            "nonexistent checkpoint reference")

    bad_oracle_ref = copy.deepcopy(manifest)
    bad_oracle_ref["cases"][0]["oracle"] = ["nonexistent oracle scenario"]
    rejects(lambda: validate_case_references(bad_oracle_ref, oracle, passing, index, by_case),
            "nonexistent oracle reference")

    bad_test_ref = copy.deepcopy(manifest)
    bad_test_ref["cases"][0]["tests"] = ["PiSharp.Tests.KeysModelDisplayTests.DoesNotExist"]
    rejects(lambda: validate_case_references(bad_test_ref, oracle, passing, index, by_case),
            "nonexistent test reference")

    bad_checkpoint = copy.deepcopy(checkpoints)
    key = next(iter(bad_checkpoint["cases"]))
    checkpoint_id = next(iter(bad_checkpoint["cases"][key]["pisharp"]["checkpoints"]))
    bad_checkpoint["cases"][key]["pisharp"]["checkpoints"][checkpoint_id]["editor"] = ["CORRUPTED"]
    rejects(lambda: validate_checkpoints(bad_checkpoint, manifest), "incorrect editor checkpoint")

    bad_cursor = copy.deepcopy(checkpoints)
    cursor_case = next(case for case in bad_cursor["cases"].values()
                       if not next(iter(case["pisharp"]["checkpoints"])).endswith(EXIT_CHECKPOINT_SUFFIXES))
    cursor_id = next(checkpoint for checkpoint in cursor_case["pisharp"]["checkpoints"]
                     if not checkpoint.endswith(EXIT_CHECKPOINT_SUFFIXES))
    cursor_case["pisharp"]["checkpoints"][cursor_id]["cursor"]["x"] += 1
    rejects(lambda: validate_checkpoints(bad_cursor, manifest), "incorrect cursor checkpoint")

    bad_row = copy.deepcopy(checkpoints)
    row_case = next(iter(bad_row["cases"].values()))
    row_id = next(iter(row_case["pisharp"]["checkpoints"]))
    row_case["pisharp"]["checkpoints"][row_id]["editorRow"] += 1
    rejects(lambda: validate_checkpoints(bad_row, manifest), "incorrect editor-relative cursor row")

    bad_footer = copy.deepcopy(checkpoints)
    footer_case = next(iter(bad_footer["cases"].values()))
    footer_id = next(iter(footer_case["pisharp"]["checkpoints"]))
    footer_case["pisharp"]["checkpoints"][footer_id]["footer"] = "wrong • medium"
    rejects(lambda: validate_checkpoints(bad_footer, manifest), "incorrect model/thinking footer")

    bad_status = copy.deepcopy(checkpoints)
    status_case = next(case for case in status_case_values(bad_status))
    status_id = next(checkpoint for checkpoint, value in status_case["pisharp"]["checkpoints"].items()
                     if value["status"])
    status_case["pisharp"]["checkpoints"][status_id]["status"] = "Wrong status"
    rejects(lambda: validate_checkpoints(bad_status, manifest), "incorrect thinking/model/tool/copy status")

    bad_calibration = copy.deepcopy(checkpoints)
    next(iter(bad_calibration["cases"].values()))["calibration"]["differences"] = 1
    rejects(lambda: validate_checkpoints(bad_calibration, manifest), "failed calibration")

    bad_http = copy.deepcopy(checkpoints)
    next(iter(bad_http["cases"].values()))["paired"]["httpMatch"] = False
    rejects(lambda: validate_checkpoints(bad_http, manifest), "provider HTTP mismatch")

    bad_clipboard = copy.deepcopy(checkpoints)
    next(iter(bad_clipboard["cases"].values()))["paired"]["clipboardMatch"] = False
    rejects(lambda: validate_checkpoints(bad_clipboard, manifest), "clipboard mismatch")

    bad_requests = copy.deepcopy(checkpoints)
    bad_requests["cases"][CASE_IDS[0]]["pisharp"]["requests"] = ["wrong request"]
    rejects(lambda: validate_checkpoints(bad_requests, manifest), "mismatched provider requests")

    bad_model = copy.deepcopy(checkpoints)
    bad_model["cases"][CASE_IDS[0]]["pisharp"]["requestFacts"][0]["model"] = "wrong-model"
    rejects(lambda: validate_checkpoints(bad_model, manifest), "wrong cycled model at request time")

    bad_context = copy.deepcopy(checkpoints)
    bad_context["cases"][CASE_IDS[0]]["pisharp"]["requestFacts"][1]["history"] = ["user:second prompt"]
    rejects(lambda: validate_checkpoints(bad_context, manifest), "lost conversation context across model change")

    bad_thinking = copy.deepcopy(checkpoints)
    bad_thinking["cases"][CASE_IDS[0]]["pisharp"]["requestFacts"][0]["thinking"] = "low"
    rejects(lambda: validate_checkpoints(bad_thinking, manifest), "wrong thinking level in provider request")

    leaked = copy.deepcopy(checkpoints)
    leaked_case = leaked["cases"][CASE_IDS[0]]
    leaked_case["pisharp"]["checkpoints"]["quit"]["temporaryModes"] = ["1049"]
    rejects(lambda: validate_checkpoints(leaked, manifest), "leaked terminal mode at exit")

    wrong_screen = copy.deepcopy(checkpoints)
    wrong_screen["cases"][CASE_IDS[0]]["pisharp"]["checkpoints"]["quit"]["activeScreen"] = "alternate"
    rejects(lambda: validate_checkpoints(wrong_screen, manifest), "terminal not restored at exit")

    failed_trx = [(name, "Failed" if name.startswith(manifest["requiredTests"]["facts"][0]) else outcome)
                  for name, outcome in trx_results_value]
    rejects(lambda: validate_trx(failed_trx, manifest), "failed focused test")

    skipped_trx = [(name, "NotExecuted" if name.startswith(manifest["requiredTests"]["theories"][0]) else outcome)
                   for name, outcome in trx_results_value]
    rejects(lambda: validate_trx(skipped_trx, manifest), "skipped pinned-Pi theory")

    missing_theory = [(name, outcome) for name, outcome in trx_results_value
                      if not name.startswith(manifest["requiredTests"]["theories"][0])]
    rejects(lambda: validate_trx(missing_theory, manifest), "missing pinned-Pi theory cases")

    failed_full = trx_results_value + [("PiSharp.Tests.SomeTest", "Failed")]
    rejects(lambda: validate_full_trx(failed_full), "failed full suite")

    skipped_full = trx_results_value + [("PiSharp.Tests.SomeTest", "Skipped")]
    rejects(lambda: validate_full_trx(skipped_full), "skipped full suite")

    print("negative controls passed")


def status_case_values(checkpoints):
    for case in checkpoints["cases"].values():
        if any(value["status"] for value in case["pisharp"]["checkpoints"].values()):
            yield case


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-model-display-acceptance-2026-11-03.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-model-display-pi-oracle-2026-11-03.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-model-display-checkpoints-2026-11-03.json")
    parser.add_argument("--implementation")
    parser.add_argument("--pi", type=Path)
    parser.add_argument("--trx", type=Path)
    parser.add_argument("--full-trx", type=Path)
    parser.add_argument("--skip-tests", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    implementation = args.implementation or subprocess.check_output(
        ["git", "log", "-1", "--format=%H", "--", "src", "tests"], cwd=ROOT, text=True).strip()
    latest = subprocess.check_output(
        ["git", "log", "-1", "--format=%H", "--", "src", "tests"], cwd=ROOT, text=True).strip()
    if implementation != latest:
        raise VerificationError(
            f"Stale implementation SHA: requested {implementation!r}, latest src/tests commit is {latest!r}.")
    manifest = load_json(args.manifest)
    oracle = load_json(args.oracle)
    checkpoints = load_json(args.checkpoints)
    if args.self_test:
        manifest["implementationSha"] = implementation
        if args.trx is None:
            args.trx = run_focused_tests(implementation)
        results = trx_results(args.trx)
        self_test(manifest, oracle, checkpoints, results)
        return
    validate_manifest(manifest, implementation)
    validate_oracle(oracle, manifest, args.pi)
    validate_terminal_fixture(manifest)
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
    print(f"keys-model-display verified at {implementation}: 16 cases, {len(oracle['scenarios'])} pinned-Pi "
          f"scenarios, {len(checkpoints['cases'])} paired cases, {len(index)} captured checkpoints, "
          f"dimensions {sorted(dimensions)}, modes {sorted(modes)}.")


if __name__ == "__main__":
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
