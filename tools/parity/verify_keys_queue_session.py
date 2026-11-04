#!/usr/bin/env python3
"""Verify the keys-queue-session acceptance manifest and committed evidence."""
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
REQUIRED_CASES = [f"Q{index:02d}" for index in range(1, 17)]
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
CASE_IDS = ("100x30-dark-fullscreen", "100x30-dark-regular",
            "140x40-dark-fullscreen", "140x40-dark-regular")
TEMP_PRIVATE_MODES = ("1000", "1002", "1003", "1004", "1006", "1049", "2004", "2026")
EXPECTED_REQUESTS = ["prompt one", "prompt two", "prompt three", "prompt four",
                     "first prompt", "steer one\n\nfollow one\n\ndraft text"]
SEARCH_ACTIONS = ("search", "rename-select", "noninvasive-query", "reopen-renamed-search",
                  "reopen-deleted-search")


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
    require(manifest.get("capability") == "keys-queue-session",
            "Manifest capability must be keys-queue-session.")
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
        require(comparison.get("required") is True,
                f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get("status") == "match",
                f"Unresolved required comparison {comparison.get('id')}: {comparison.get('status')!r}.")
    for finding in manifest.get("findings") or []:
        require(finding.get("required") is False,
                f"Finding {finding.get('id')} expands the required contract.")
    tests = manifest.get("requiredTests") or {}
    require(tests.get("theories") and tests.get("facts") and tests.get("ptyEvidence"),
            "Manifest must list required theories, facts and PTY evidence ids.")
    require((manifest.get("terminalFixtures") or []) ==
            ["tools/parity/terminal/fixtures/terminal-keys-queue-session.json"],
            "Manifest must name the keys-queue-session terminal fixture.")


def validate_oracle(oracle, manifest, pi_checkout=None):
    require(oracle.get("capability") == "keys-queue-session",
            "Oracle capability must be keys-queue-session.")
    require(oracle.get("piSha") == manifest.get("piSha"),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get("scenarios") or {}
    require(len(scenarios) == manifest.get("oracleScenarioCount"),
            f"Oracle has {len(scenarios)} scenarios, manifest claims {manifest.get('oracleScenarioCount')}.")
    require(set(scenarios) == set(REQUIRED_CASES),
            f"Oracle scenarios must be {REQUIRED_CASES}, found {sorted(scenarios)}.")
    for name, scenario in scenarios.items():
        require(scenario.get("source"), f"Oracle scenario {name!r} has no source references.")
        expected = scenario.get("expected") or {}
        require(expected, f"Oracle scenario {name!r} has no expected observation.")
        require("bindings" in expected or "bindingsWindows" in expected,
                f"Oracle scenario {name!r} has no executable binding expectation.")
    pty = oracle.get("ptyScenarios") or {}
    require(set(pty) == set(CASE_IDS), f"Oracle PTY scenarios must cover {CASE_IDS}.")
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
    require(fixture.get("id") == "terminal-keys-queue-session", "Terminal fixture id is wrong.")
    require(fixture.get("persistSessions") is True, "Terminal fixture must persist isolated sessions.")
    actions = fixture.get("actions") or []
    ids = [action.get("id") for action in actions]
    for expected in ("create-main", "first-prompt", "steer-enter", "follow-up-alt-enter",
                     "dequeue-ready", "submit-restored", "picker-open", "picker-path-on",
                     "picker-sort-recent", "picker-named", "picker-scope-all", "picker-rename",
                     "picker-delete-confirm", "picker-noninvasive-query", "quit"):
        require(expected in ids, f"Terminal fixture is missing action {expected!r}.")
    require({"fullscreen", "regular"} <= set(fixture.get("modes") or []),
            "Terminal fixture must exercise fullscreen and regular modes.")
    require(len(fixture.get("dimensions") or []) >= 2,
            "Terminal fixture must exercise at least two dimensions.")
    require(len(fixture.get("assertionFrames") or []) >= 24,
            "Terminal fixture must name at least 24 checkpoints.")


def normalize_checkpoint(value):
    value = copy.deepcopy(value)
    action = value.get("action", "")
    if "2026" in value.get("temporaryModes", []):
        value["temporaryModes"] = [mode for mode in value["temporaryModes"] if mode != "2026"]
    if not value.get("cursorVisible", True):
        value.pop("editorRow", None)
        value.pop("cursorColumn", None)
    if action.startswith("release-") or action in ("submit-restored", "follow-up-alt-enter"):
        value.pop("editor", None)
    if "picker" in value:
        picker = value["picker"]
        if picker.get("confirmation") or picker.get("statuses"):
            picker["sort"] = None
            picker["nameFilter"] = None
            picker["pathVisible"] = None
        picker["statuses"] = []
        if action == "picker-delete-clear":
            picker["selected"] = None
        if any(marker in action for marker in SEARCH_ACTIONS):
            picker["rows"] = []
            picker["selected"] = None
    return value


def checkpoint_ids(checkpoints, case_id):
    case = checkpoints["cases"][case_id]
    return set(case["pi"]["checkpoints"]) | set(case["pisharp"]["checkpoints"])


def checkpoint_index(checkpoints):
    index = {}
    by_case = {}
    for case_id, case in (checkpoints.get("cases") or {}).items():
        prefix = f"terminal-keys-queue-session/{case_id}"
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


def validate_checkpoints(checkpoints, manifest):
    require(checkpoints.get("piSha") == manifest.get("piSha"),
            f"Checkpoint pin is {checkpoints.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    cases = checkpoints.get("cases") or {}
    require(set(cases) == set(CASE_IDS),
            f"Checkpoint cases must be {list(CASE_IDS)}, found {sorted(cases)}.")
    dimensions = {case_id.split("-")[0] for case_id in cases}
    modes = {case_id.split("-")[-1] for case_id in cases}
    require(len(dimensions) >= 2, f"Paired evidence only used dimensions {sorted(dimensions)}.")
    require({"fullscreen", "regular"} <= modes, f"Paired evidence modes are {sorted(modes)}.")
    for case_id, case in cases.items():
        calibration = case.get("calibration") or {}
        require(not calibration.get("scenarioErrors"),
                f"{case_id} calibration scenario errors: {calibration.get('scenarioErrors')}")
        require(calibration.get("semanticDifferences") == 0,
                f"{case_id} calibration has {calibration.get('semanticDifferences')} semantic differences.")
        require(calibration.get("httpMatch") is True, f"{case_id} calibration HTTP traces differ.")
        paired = case.get("paired") or {}
        require(not paired.get("scenarioErrors"),
                f"{case_id} paired scenario errors: {paired.get('scenarioErrors')}")
        require(paired.get("httpMatch") is True, f"{case_id} provider HTTP traces differ.")
        require(paired.get("semanticDifferences") == 0,
                f"{case_id} paired semantic differences: {paired.get('semanticFirstDifferences')}")
        pi = case["pi"]["checkpoints"]
        pisharp = case["pisharp"]["checkpoints"]
        require(set(pi) == set(pisharp), f"{case_id} products captured different checkpoint ids.")
        for checkpoint_id in pi:
            left = normalize_checkpoint(pi[checkpoint_id])
            right = normalize_checkpoint(pisharp[checkpoint_id])
            require(left == right, f"{case_id}/{checkpoint_id} semantic checkpoint differs.")
        require(case["pi"]["requests"] == EXPECTED_REQUESTS,
                f"{case_id} pinned-Pi requests are {case['pi']['requests']!r}.")
        require(case["pisharp"]["requests"] == EXPECTED_REQUESTS,
                f"{case_id} PiSharp requests are {case['pisharp']['requests']!r}.")
        require(case["pi"]["requestFacts"] == case["pisharp"]["requestFacts"],
                f"{case_id} provider request facts differ.")
        facts = case["pi"]["requestFacts"]
        require(len(facts) == len(EXPECTED_REQUESTS),
                f"{case_id} provider request count is {len(facts)}.")
        require(facts[4]["users"] == ["first prompt"], f"{case_id} first queued request text differs.")
        require(facts[5]["users"] == ["steer one\n\nfollow one\n\ndraft text"],
                f"{case_id} restored request text differs.")
        require(facts[5]["history"][-1] == "user:steer one\n\nfollow one\n\ndraft text",
                f"{case_id} restored request ordering differs.")
        for product in ("pi", "pisharp"):
            files = case[product]["sessionFiles"]
            require(len(files) == 3, f"{case_id}/{product} has {len(files)} session files after deletion, expected 3.")
        steer = pi["steer-enter"]["queue"]
        require(steer["steering"] == ["steer one"] and steer["followUp"] == [],
                f"{case_id} steering checkpoint differs: {steer!r}.")
        follow = pi["follow-up-alt-enter"]["queue"]
        require(follow["steering"] == ["steer one"] and follow["followUp"] == ["follow one"],
                f"{case_id} follow-up checkpoint differs: {follow!r}.")
        restored = pi["dequeue-ready"]["editor"]
        require(restored == ["steer one", "follow one", "draft text"],
                f"{case_id} restored editor differs: {restored!r}.")
        picker = pi["picker-open"]["picker"]
        require(picker["scope"] == "current" and picker["sort"] == "Threaded" and
                picker["nameFilter"] == "All" and picker["pathVisible"] is False,
                f"{case_id} initial picker state differs: {picker!r}.")
        require(pi["picker-path-on"]["picker"]["pathVisible"] is True,
                f"{case_id} path toggle did not turn on.")
        require(pi["picker-sort-recent"]["picker"]["sort"] == "Recent" and
                pi["picker-sort-fuzzy"]["picker"]["sort"] == "Fuzzy" and
                pi["picker-sort-threaded"]["picker"]["sort"] == "Threaded",
                f"{case_id} sort cycle differs.")
        require(pi["picker-named"]["picker"]["nameFilter"] == "Named",
                f"{case_id} named filter did not activate.")
        require(pi["picker-scope-all"]["picker"]["scope"] == "all",
                f"{case_id} Tab did not switch to all scope.")
        require(pi["picker-confirm-open"]["picker"]["confirmation"] is True,
                f"{case_id} deletion confirmation did not open.")
        require(pi["picker-confirm-cancel"]["picker"]["confirmation"] is False,
                f"{case_id} deletion confirmation did not cancel.")
        deleted_rows = pi["picker-delete-clear"]["picker"]["rows"]
        require("beta" not in deleted_rows and "alpha renamed" in deleted_rows and "main" in deleted_rows,
                f"{case_id} deletion removed the wrong sessions: {deleted_rows!r}.")
        reopened = pi["picker-reopen-renamed-search"]["picker"]
        require("alpha renamed" in reopened["rows"],
                f"{case_id} reopened picker lost the renamed session.")
        require(reopened["selected"] == "alpha renamed",
                f"{case_id} reopened picker did not select the renamed session.")
        quit_checkpoint = normalize_checkpoint(pi["quit"])
        require(quit_checkpoint["activeScreen"] == "normal", f"{case_id} did not restore the normal screen.")
        require(quit_checkpoint["temporaryModes"] == [], f"{case_id} left terminal modes active.")
        require(quit_checkpoint["cursorVisible"] is True, f"{case_id} did not restore a visible cursor.")
        require(normalize_checkpoint(pisharp["quit"]) == quit_checkpoint,
                f"{case_id} PiSharp quit checkpoint differs.")
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
            require(name in scenario_names,
                    f"{case['id']} references a nonexistent oracle scenario: {name!r}.")
        for method in case.get("tests") or []:
            require(method in allowed_tests,
                    f"{case['id']} references a test outside requiredTests: {method!r}.")
            require(any(name == method or name.startswith(method + "(") for name in passing),
                    f"{case['id']} references a test that did not pass: {method!r}.")
            referenced_tests.add(method)
        for checkpoint in case.get("terminalCheckpoints") or []:
            require(resolve_checkpoint(checkpoint, index, by_case),
                    f"{case['id']} references a missing terminal checkpoint: {checkpoint!r}.")
    require(referenced_tests == allowed_tests - set(tests["theories"]),
            f"Required facts are not referenced by any case: "
            f"{sorted(allowed_tests - set(tests['theories']) - referenced_tests)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in (case.get("oracle") or [])}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced by any case: {sorted(scenario_names - referenced_scenarios)}")
    referenced_evidence = set(tests["ptyEvidence"])
    require(referenced_evidence <= set(by_case),
            f"PTY evidence ids are not checkpoint cases: {sorted(referenced_evidence - set(by_case))}")
    q16 = next(case for case in manifest["cases"] if case["id"] == "Q16")
    distinct = set(q16.get("terminalCheckpoints") or [])
    require(len(distinct) >= 24, f"Q16 names {len(distinct)} checkpoint observations, expected at least 24.")
    for dimension in ("100x30", "140x40"):
        require(any(entry.startswith(f"terminal-keys-queue-session/{dimension}-") for entry in distinct),
                f"Q16 has no {dimension} checkpoint.")


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
    directory = tempfile.mkdtemp(prefix="pisharp-queue-session-verify-")
    trx = Path(directory) / "queue-session.trx"
    environment = dict(os.environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~KeysQueueSessionTests",
        "FullyQualifiedName~KeysQueueSessionParityTests",
        "FullyQualifiedName~KeysQueueSessionPtyTests",
        "FullyQualifiedName~InteractivePromptExecutionInputTests.ActiveInputInterception",
        "FullyQualifiedName~InteractivePromptExecutionInputTests.ExtensionCommandsExecuteImmediately",
        "FullyQualifiedName~TerminalPtyTests.SessionDiscoverySortResumeAndCloneWorkThroughLinuxPty",
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=queue-session.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx


def validate_all(manifest, oracle, checkpoints, results, index, by_case, pi=None):
    validate_manifest(manifest, manifest["implementationSha"])
    validate_oracle(oracle, manifest, pi)
    validate_terminal_fixture(manifest)
    validate_checkpoints(checkpoints, manifest)
    validate_oracle_pty_scenarios(oracle, index, by_case)
    validate_case_references(manifest, oracle, results, index, by_case)


def validate_oracle_pty_scenarios(oracle, index, by_case):
    for case_id, references in oracle["ptyScenarios"].items():
        require(references, f"Oracle PTY scenario {case_id} has no checkpoint references.")
        for reference in references:
            require(resolve_checkpoint(reference, index, by_case),
                    f"Oracle PTY scenario {case_id} references a missing checkpoint {reference!r}.")


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
        (f"{manifest['requiredTests']['theories'][0]}(name: \"Q{index:02d}\")", "Passed")
        for index in range(1, 17)]

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "Q16"]
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
    bad_oracle["scenarios"]["Q01"].pop("expected")
    rejects(lambda: validate_oracle(bad_oracle, manifest), "incomplete oracle scenario")

    bad_sources = copy.deepcopy(oracle)
    bad_sources["sources"] = []
    rejects(lambda: validate_oracle_sources(bad_sources), "missing oracle source inventory")

    bad_case_ref = copy.deepcopy(manifest)
    bad_case_ref["cases"][0]["terminalCheckpoints"] = [
        "terminal-keys-queue-session/does-not-exist/startup"]
    rejects(lambda: validate_case_references(bad_case_ref, oracle, passing, index, by_case),
            "nonexistent checkpoint reference")

    bad_oracle_ref = copy.deepcopy(manifest)
    bad_oracle_ref["cases"][0]["oracle"] = ["nonexistent oracle scenario"]
    rejects(lambda: validate_case_references(bad_oracle_ref, oracle, passing, index, by_case),
            "nonexistent oracle reference")

    bad_test_ref = copy.deepcopy(manifest)
    bad_test_ref["cases"][0]["tests"] = ["PiSharp.Tests.KeysQueueSessionTests.DoesNotExist"]
    rejects(lambda: validate_case_references(bad_test_ref, oracle, passing, index, by_case),
            "nonexistent test reference")

    def mutate_checkpoint(case_id, checkpoint, mutate, label):
        bad = copy.deepcopy(checkpoints)
        mutate(bad["cases"][case_id]["pisharp"]["checkpoints"][checkpoint])
        rejects(lambda: validate_checkpoints(bad, manifest), label)

    mutate_checkpoint(CASE_IDS[0], "steer-enter",
                      lambda value: value["queue"].__setitem__("steering", ["wrong"]),
                      "incorrect steering classification")
    mutate_checkpoint(CASE_IDS[0], "follow-up-alt-enter",
                      lambda value: value["queue"].__setitem__("followUp", []),
                      "lost follow-up message")
    mutate_checkpoint(CASE_IDS[0], "dequeue-ready",
                      lambda value: value.__setitem__("editor", ["steer one", "draft text"]),
                      "incorrect restored draft")
    mutate_checkpoint(CASE_IDS[0], "picker-open",
                      lambda value: value["picker"].__setitem__("scope", "all"),
                      "wrong picker scope")
    mutate_checkpoint(CASE_IDS[0], "picker-sort-recent",
                      lambda value: value["picker"].__setitem__("sort", "Threaded"),
                      "wrong picker sort")
    mutate_checkpoint(CASE_IDS[0], "picker-named",
                      lambda value: value["picker"].__setitem__("nameFilter", "All"),
                      "wrong named filter")
    mutate_checkpoint(CASE_IDS[0], "picker-delete-clear",
                      lambda value: value["picker"].__setitem__("rows", ["gamma", "beta", "main"]),
                      "deleted the wrong session")
    mutate_checkpoint(CASE_IDS[0], "quit",
                      lambda value: value.__setitem__("activeScreen", "alternate"),
                      "terminal mode not restored")
    mutate_checkpoint(CASE_IDS[0], "quit",
                      lambda value: value.__setitem__("temporaryModes", ["1049"]),
                      "leaked terminal mode")
    mutate_checkpoint(CASE_IDS[0], "picker-path-on",
                      lambda value: value["picker"].__setitem__("pathVisible", False),
                      "shortcut leaked into the wrong context")
    mutate_checkpoint(CASE_IDS[0], "picker-confirm-open",
                      lambda value: value["picker"].__setitem__("confirmation", False),
                      "deletion without confirmation")
    bad_rename = copy.deepcopy(checkpoints)
    bad_rename["cases"][CASE_IDS[0]]["pi"]["checkpoints"]["picker-reopen-renamed-search"]["picker"]["rows"] = ["gamma"]
    rejects(lambda: validate_checkpoints(bad_rename, manifest), "rename did not persist")

    bad_requests = copy.deepcopy(checkpoints)
    bad_requests["cases"][CASE_IDS[0]]["pisharp"]["requests"] = ["wrong request"]
    rejects(lambda: validate_checkpoints(bad_requests, manifest), "unexpected provider request")

    bad_order = copy.deepcopy(checkpoints)
    bad_order["cases"][CASE_IDS[0]]["pisharp"]["requestFacts"].reverse()
    rejects(lambda: validate_checkpoints(bad_order, manifest), "incorrect provider request ordering")

    bad_files = copy.deepcopy(checkpoints)
    bad_files["cases"][CASE_IDS[0]]["pisharp"]["sessionFiles"].append("extra.session.json")
    rejects(lambda: validate_checkpoints(bad_files, manifest), "deletion did not remove the session")

    bad_calibration = copy.deepcopy(checkpoints)
    bad_calibration["cases"][CASE_IDS[0]]["calibration"]["semanticDifferences"] = 1
    rejects(lambda: validate_checkpoints(bad_calibration, manifest), "failed same-Pi calibration")

    bad_calibration_error = copy.deepcopy(checkpoints)
    bad_calibration_error["cases"][CASE_IDS[0]]["calibration"]["scenarioErrors"] = {"pi": "timeout"}
    rejects(lambda: validate_checkpoints(bad_calibration_error, manifest), "calibration scenario error")

    bad_pty = copy.deepcopy(checkpoints)
    del bad_pty["cases"][CASE_IDS[0]]
    rejects(lambda: validate_checkpoints(bad_pty, manifest), "missing PTY evidence")

    bad_conditional = copy.deepcopy(checkpoints)
    bad_conditional["cases"][CASE_IDS[0]]["pi"]["checkpoints"]["picker-noninvasive-query-edit"][
        "picker"]["confirmation"] = True
    rejects(lambda: validate_checkpoints(bad_conditional, manifest), "Ctrl+Backspace query behaviour")

    failed_trx = [(name, "Failed" if name.startswith(manifest["requiredTests"]["facts"][0]) else outcome)
                  for name, outcome in trx_results_value]
    rejects(lambda: validate_trx(failed_trx, manifest), "failed focused test")

    skipped_trx = [(name, "NotExecuted" if name.startswith(manifest["requiredTests"]["theories"][0])
                    else outcome) for name, outcome in trx_results_value]
    rejects(lambda: validate_trx(skipped_trx, manifest), "skipped pinned-Pi theory")

    missing_theory = [(name, outcome) for name, outcome in trx_results_value
                      if not name.startswith(manifest["requiredTests"]["theories"][0])]
    rejects(lambda: validate_trx(missing_theory, manifest), "missing pinned-Pi theory cases")

    failed_full = trx_results_value + [("PiSharp.Tests.SomeTest", "Failed")]
    rejects(lambda: validate_full_trx(failed_full), "failed full suite")

    skipped_full = trx_results_value + [("PiSharp.Tests.SomeTest", "Skipped")]
    rejects(lambda: validate_full_trx(skipped_full), "skipped full suite")

    print("negative controls passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-queue-session-acceptance-2026-11-04.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-queue-session-pi-oracle-2026-11-04.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-queue-session-checkpoints-2026-11-04.json")
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
    print(f"keys-queue-session verified at {implementation}: {len(manifest['cases'])} cases, "
          f"{len(oracle['scenarios'])} pinned-Pi scenarios, {len(checkpoints['cases'])} paired cases, "
          f"{len(index)} captured checkpoints, dimensions {sorted(dimensions)}, modes {sorted(modes)}.")


if __name__ == "__main__":
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
