#!/usr/bin/env python3
"""Verify the keys-app-control acceptance manifest and all executable evidence."""
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
REQUIRED_CASES = [f"A{index:02d}" for index in range(1, 17)]
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
EXPECTED_REQUESTS = {
    "tree": ["first app control prompt"],
    "fork": ["first app control prompt"],
    "none": ["first app control prompt"],
    "bash": [],
    "suspend": [],
    "abort-stream": ["Send a response that can be aborted."],
}
EXIT_CHECKPOINT_SUFFIXES = ("quit", "exit")
TEMP_PRIVATE_MODES = ("1000", "1002", "1003", "1004", "1006", "1049", "2004", "2026")


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
    require(manifest.get("capability") == "keys-app-control", "Manifest capability must be keys-app-control.")
    require(manifest.get("piSha") == PI_SHA, f"Manifest Pi SHA is {manifest.get('piSha')!r}, expected {PI_SHA}.")
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


def checkpoint_index(checkpoints):
    index = {}
    by_fixture = {}
    for key, case in checkpoints.get("cases", {}).items():
        fixture, case_id = key.split("/", 1)
        for checkpoint_id, value in case["pi"]["checkpoints"].items():
            index[f"{fixture}/{case_id}/{checkpoint_id}"] = value
            by_fixture.setdefault(f"{fixture}/{checkpoint_id}", []).append(
                f"{fixture}/{case_id}/{checkpoint_id}")
    return index, by_fixture


def resolve_checkpoint(reference, index, by_fixture):
    if reference in index:
        return [reference]
    matches = by_fixture.get(reference, [])
    require(matches, f"Nonexistent terminal checkpoint reference: {reference!r}.")
    return sorted(matches)


def validate_oracle(oracle, manifest, pi_checkout=None):
    require(oracle.get("capability") == "keys-app-control", "Oracle capability must be keys-app-control.")
    require(oracle.get("piSha") == manifest.get("piSha"),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get("scenarios") or {}
    require(len(scenarios) == manifest.get("oracleScenarioCount"),
            f"Oracle has {len(scenarios)} scenarios, manifest claims {manifest.get('oracleScenarioCount')}.")
    for name, scenario in scenarios.items():
        require(scenario.get("source"), f"Oracle scenario {name!r} has no source references.")
        require(scenario.get("expected"), f"Oracle scenario {name!r} has no expected observation.")
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


def validate_checkpoints(checkpoints, manifest):
    require(checkpoints.get("piSha") == manifest.get("piSha"),
            f"Checkpoint pin is {checkpoints.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    cases = checkpoints.get("cases") or {}
    require(cases, "Checkpoint evidence has no cases.")
    index, by_fixture = checkpoint_index(checkpoints)
    dimensions = set()
    modes = set()
    for key, case in cases.items():
        fixture, case_id = key.split("/", 1)
        dimensions.add(case_id.split("-")[0])
        modes.add(case_id.split("-")[-1])
        calibration = case.get("calibration") or {}
        require(not calibration.get("scenarioErrors"),
                f"{key} calibration scenario errors: {calibration.get('scenarioErrors')}")
        require(calibration.get("differences") == 0,
                f"{key} calibration has {calibration.get('differences')} differences.")
        paired = case.get("paired") or {}
        require(not paired.get("scenarioErrors"), f"{key} paired scenario errors: {paired.get('scenarioErrors')}")
        require(paired.get("httpMatch") is True, f"{key} provider HTTP traces differ.")
        pi = case["pi"]["checkpoints"]
        pisharp = case["pisharp"]["checkpoints"]
        require(set(pi) == set(pisharp), f"{key} products captured different checkpoint ids.")
        for checkpoint_id in pi:
            left, right = pi[checkpoint_id], pisharp[checkpoint_id]
            is_exit = checkpoint_id.endswith(EXIT_CHECKPOINT_SUFFIXES)
            require(left["editor"] == right["editor"],
                    f"{key}/{checkpoint_id} editor text differs: {left['editor']!r} != {right['editor']!r}.")
            if not is_exit:
                require(left["cursor"] == right["cursor"],
                        f"{key}/{checkpoint_id} cursor differs: {left['cursor']} != {right['cursor']}.")
            else:
                require(left["activeScreen"] == "normal" and right["activeScreen"] == "normal",
                        f"{key}/{checkpoint_id} did not restore the normal screen.")
                require(left["cursor"].get("visible") is True and right["cursor"].get("visible") is True,
                        f"{key}/{checkpoint_id} did not restore a visible cursor.")
                require(not left["temporaryModes"] and not right["temporaryModes"],
                        f"{key}/{checkpoint_id} left temporary terminal modes active.")
        expected = EXPECTED_REQUESTS[fixture]
        require(case["pi"]["requests"] == expected, f"{key} Pi requests are {case['pi']['requests']!r}, expected {expected!r}.")
        require(case["pisharp"]["requests"] == expected,
                f"{key} PiSharp requests are {case['pisharp']['requests']!r}, expected {expected!r}.")
    require(len(dimensions) >= 2, f"Paired evidence only used dimensions {sorted(dimensions)}.")
    require({"fullscreen", "regular"} <= modes, f"Paired evidence modes are {sorted(modes)}.")
    return index, by_fixture, dimensions, modes


def validate_case_references(manifest, oracle, results, evidence_ids, index, by_fixture):
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
        for evidence_id in case.get("pty") or []:
            require(evidence_id in tests["ptyEvidence"],
                    f"{case['id']} references a PTY record outside requiredTests: {evidence_id!r}.")
            require(evidence_id in evidence_ids, f"{case['id']} references a missing PTY record: {evidence_id!r}.")
        for checkpoint in case.get("terminalCheckpoints") or []:
            resolved = resolve_checkpoint(checkpoint, index, by_fixture)
            require(resolved, f"{case['id']} references a missing terminal checkpoint: {checkpoint!r}.")
    require(referenced_tests == allowed_tests,
            f"Required tests are not referenced by any case: {sorted(allowed_tests - referenced_tests)}")
    referenced_evidence = {evidence_id for case in manifest["cases"] for evidence_id in (case.get("pty") or [])}
    require(referenced_evidence == set(tests["ptyEvidence"]),
            f"PTY evidence is not referenced by any case: {sorted(set(tests['ptyEvidence']) - referenced_evidence)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in (case.get("oracle") or [])}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced by any case: {sorted(scenario_names - referenced_scenarios)}")
    a16 = next(case for case in manifest["cases"] if case["id"] == "A16")
    distinct = {entry for entry in a16.get("terminalCheckpoints") or [] if entry.count("/") == 2}
    require(len(distinct) >= 20,
            f"A16 names {len(distinct)} dimension/case/checkpoint observations, expected at least 20.")
    require(any(entry.startswith("100x30-") for entry in distinct), "A16 has no 100x30 checkpoint.")
    require(any(entry.startswith("80x24-") for entry in distinct), "A16 has no 80x24 checkpoint.")
    require(any(entry.startswith("120x40-") for entry in distinct), "A16 has no 120x40 checkpoint.")


def validate_oracle_pty_scenarios(oracle, index, by_fixture):
    for case_id, references in oracle["ptyScenarios"].items():
        for reference in references:
            require(resolve_checkpoint(reference, index, by_fixture),
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


def validate_evidence(path, manifest, implementation):
    try:
        lines = [line for line in Path(path).read_text(encoding="utf-8").splitlines() if line.strip()]
    except OSError as error:
        raise VerificationError(f"Cannot read PTY evidence {path}: {error}") from error
    require(lines, "PTY evidence file is empty; the real Linux PTY cases did not run.")
    records = [json.loads(line) for line in lines]
    expected = manifest["requiredTests"]["ptyEvidence"]
    ids = [record.get("id") for record in records]
    require(sorted(ids) == sorted(expected), f"PTY evidence ids are {sorted(ids)}, expected {sorted(expected)}.")
    for record in records:
        require(record.get("implementation") == implementation,
                f"PTY evidence {record.get('id')} has stale implementation {record.get('implementation')!r}.")
        require(record.get("platform") == "linux", f"PTY evidence {record.get('id')} did not run on Linux.")
        details = record.get("details") or {}
        require(details.get("exitCode") == 0, f"PTY evidence {record.get('id')} recorded a nonzero exit.")
    by_id = {record["id"]: record["details"] for record in records}
    idle = by_id["pty-app-idle-controls"]
    require(idle.get("termiosRestored") is True and idle.get("escapePreservedDraft") is True and
            idle.get("ctrlDForwardDeleted") is True and idle.get("ctrlCCleared") is True,
            "Idle-control PTY evidence is incomplete.")
    for setting in ("tree", "fork", "none"):
        details = by_id[f"pty-app-double-escape-{setting}"]
        require(details.get("setting") == setting, f"Double-Escape evidence has the wrong setting for {setting}.")
        require(details.get("selectorOpened") is (setting != "none"),
                f"Double-Escape {setting} selector outcome is wrong.")
        require(details.get("editorRestored") is True, f"Double-Escape {setting} did not restore the editor.")
    bash = by_id["pty-app-bash-cancel"]
    require(bash.get("cancelledBeforeCompletion") is True and bash.get("survivingChildren") == 0 and
            bash.get("promptResponsive") is True and bash.get("subsequentBashExecuted") is True,
            "Bash cancellation PTY evidence is incomplete.")
    for path in ("quit", "ctrl-d", "ctrl-c", "bash-cancel"):
        details = by_id[f"pty-app-shutdown-{path}"]
        require(details.get("path") == path and details.get("termiosRestored") is True,
                f"Shutdown evidence for {path} did not restore the terminal.")
        require(details.get("bracketedPasteActivated", 0) >= 1 and
                details.get("bracketedPasteRestored", 0) >= 1,
                f"Shutdown evidence for {path} lacks bracketed-paste restoration.")
    stream = by_id["pty-app-stream-cancel"]
    require(stream.get("requests") == 2, "Streaming cancellation evidence did not record exactly two requests.")
    require(stream.get("restoredDraft") == "steer text\n\nfollow text\n\ncurrent draft",
            "Streaming cancellation evidence restored the wrong queue/draft order.")
    require(stream.get("aborted") is True and stream.get("lateStreamCorruptedDraft") is False,
            "Streaming cancellation evidence records an incorrect abort/late-stream outcome.")
    for mode in ("fullscreen", "regular"):
        details = by_id[f"pty-app-suspend-resume-{mode}"]
        require(details.get("resizedDuringSuspension") is True and details.get("termiosRestored") is True and
                details.get("draftPreserved") is True and details.get("inputAfterResume") == "x",
                f"Suspend/resume evidence is incomplete for {mode}.")
    return set(ids)


def run(command, env=None, timeout=3600):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-4000:] + result.stderr[-4000:]).strip()
        raise VerificationError(f"Command failed: {' '.join(command)}\n{tail}")
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-app-control-verify-")
    trx = Path(directory) / "app-control.trx"
    evidence = Path(directory) / "pty-evidence.jsonl"
    environment = dict(os.environ)
    environment["PISHARP_APP_CONTROL_EVIDENCE"] = str(evidence)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    filter_value = "|".join([
        "FullyQualifiedName~AppControlTests",
        "FullyQualifiedName~AppControlParityTests",
        "FullyQualifiedName~AppControlPtyTests",
        "FullyQualifiedName~TerminalSuspendPtyTests",
        "FullyQualifiedName~TerminalPtyTests.DoubleCtrlCExitsInteractiveProcess",
        "FullyQualifiedName~TerminalPtyTests.FullscreenTranscriptNavigationAndSearchThroughLinuxPty",
        "FullyQualifiedName~TerminalInputTests.ActiveEditorRoutesEnterAltEnterAndEscapeWithPendingRestoration",
        "FullyQualifiedName~BashProcessLifetimeTests.CancellationKillsChildAndDetachedGrandchildBeforeSettling",
        "FullyQualifiedName~ExtensionSessionStateTests.SessionLifecycleCallbacksAreOrderedFailureIsolatedAndScopedToTheRun"
    ])
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter", filter_value,
         "--logger", "trx;LogFileName=app-control.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx, evidence


def validate_all(manifest, oracle, checkpoints, results, evidence_ids, index, by_fixture, pi=None):
    validate_manifest(manifest, manifest["implementationSha"])
    validate_oracle(oracle, manifest, pi)
    validate_checkpoints(checkpoints, manifest)
    validate_oracle_pty_scenarios(oracle, index, by_fixture)
    validate_case_references(manifest, oracle, results, evidence_ids, index, by_fixture)


def write_manifest(path, manifest):
    Path(path).write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def self_test(manifest, oracle, checkpoints, evidence_records, trx_results_value):
    def rejects(call, label):
        try:
            call()
        except VerificationError as error:
            print(f"negative control rejected: {label}: {error}")
            return
        raise VerificationError(f"Negative control accepted an invalid case: {label}")

    implementation = manifest["implementationSha"]
    validate_manifest(manifest, implementation)

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "A16"]
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

    bad_oracle = copy.deepcopy(oracle)
    bad_oracle["scenarios"]["a01-idle-escape-nonempty"].pop("expected")
    rejects(lambda: validate_oracle(bad_oracle, manifest), "incomplete oracle scenario")

    index, by_fixture = checkpoint_index(checkpoints)
    bad_case_ref = copy.deepcopy(manifest)
    bad_case_ref["cases"][0]["terminalCheckpoints"] = ["tree/does-not-exist"]
    rejects(lambda: validate_case_references(
        bad_case_ref, oracle,
        [(method, "Passed") for method in bad_case_ref["requiredTests"]["facts"]] +
        [(bad_case_ref["requiredTests"]["theories"][0], "Passed")],
        evidence_records, index, by_fixture), "nonexistent checkpoint reference")

    bad_oracle_ref = copy.deepcopy(manifest)
    bad_oracle_ref["cases"][0]["oracle"] = ["nonexistent oracle scenario"]
    rejects(lambda: validate_case_references(
        bad_oracle_ref, oracle,
        [(method, "Passed") for method in bad_oracle_ref["requiredTests"]["facts"]] +
        [(bad_oracle_ref["requiredTests"]["theories"][0], "Passed")],
        evidence_records, index, by_fixture), "nonexistent oracle reference")

    bad_test_ref = copy.deepcopy(manifest)
    bad_test_ref["cases"][0]["tests"] = ["PiSharp.Tests.AppControlTests.DoesNotExist"]
    rejects(lambda: validate_case_references(
        bad_test_ref, oracle,
        [(method, "Passed") for method in bad_test_ref["requiredTests"]["facts"]] +
        [(bad_test_ref["requiredTests"]["theories"][0], "Passed")],
        evidence_records, index, by_fixture), "nonexistent test reference")

    bad_pty_ref = copy.deepcopy(manifest)
    bad_pty_ref["cases"][0]["pty"] = ["pty-nonexistent"]
    rejects(lambda: validate_case_references(
        bad_pty_ref, oracle,
        [(method, "Passed") for method in bad_pty_ref["requiredTests"]["facts"]] +
        [(bad_pty_ref["requiredTests"]["theories"][0], "Passed")],
        evidence_records, index, by_fixture), "nonexistent PTY reference")

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

    bad_calibration = copy.deepcopy(checkpoints)
    next(iter(bad_calibration["cases"].values()))["calibration"]["scenarioErrors"] = {"pi": "timeout"}
    rejects(lambda: validate_checkpoints(bad_calibration, manifest), "unresolved calibration error")

    bad_requests = copy.deepcopy(checkpoints)
    tree_case = next(case for key, case in bad_requests["cases"].items() if key.startswith("tree/"))
    tree_case["pisharp"]["requests"] = ["wrong request"]
    rejects(lambda: validate_checkpoints(bad_requests, manifest), "mismatched provider requests")

    leaked = copy.deepcopy(checkpoints)
    exit_case = next(case for key, case in leaked["cases"].items() if key.startswith("tree/"))
    exit_case["pisharp"]["checkpoints"]["app-quit"]["temporaryModes"] = ["1049"]
    rejects(lambda: validate_checkpoints(leaked, manifest), "leaked terminal mode")

    bad_evidence = copy.deepcopy(evidence_records)
    del bad_evidence["pty-app-bash-cancel"]
    rejects(lambda: validate_evidence_records(bad_evidence, manifest, implementation), "missing PTY record")

    stale_evidence = dict(evidence_records)
    stale_evidence["pty-app-idle-controls"] = dict(stale_evidence["pty-app-idle-controls"])
    stale_evidence["pty-app-idle-controls"]["implementation"] = "0" * 40
    rejects(lambda: validate_evidence_records(stale_evidence, manifest, implementation), "stale PTY evidence")

    surviving = copy.deepcopy(evidence_records)
    surviving["pty-app-bash-cancel"] = copy.deepcopy(surviving["pty-app-bash-cancel"])
    surviving["pty-app-bash-cancel"]["details"]["survivingChildren"] = 1
    rejects(lambda: validate_evidence_records(surviving, manifest, implementation), "surviving child process")

    bad_queue = copy.deepcopy(evidence_records)
    bad_queue["pty-app-stream-cancel"] = copy.deepcopy(bad_queue["pty-app-stream-cancel"])
    bad_queue["pty-app-stream-cancel"]["details"]["restoredDraft"] = "follow text\n\nsteer text"
    rejects(lambda: validate_evidence_records(bad_queue, manifest, implementation), "incorrect queue restoration")

    wrong_action = copy.deepcopy(evidence_records)
    wrong_action["pty-app-double-escape-tree"] = copy.deepcopy(wrong_action["pty-app-double-escape-tree"])
    wrong_action["pty-app-double-escape-tree"]["details"]["selectorOpened"] = False
    rejects(lambda: validate_evidence_records(wrong_action, manifest, implementation), "wrong double-Escape action")

    missing_restore = copy.deepcopy(evidence_records)
    missing_restore["pty-app-shutdown-ctrl-d"] = copy.deepcopy(missing_restore["pty-app-shutdown-ctrl-d"])
    missing_restore["pty-app-shutdown-ctrl-d"]["details"]["termiosRestored"] = False
    rejects(lambda: validate_evidence_records(missing_restore, manifest, implementation), "missing terminal restoration")

    failed_trx = [(name, "Failed" if name.startswith(manifest["requiredTests"]["facts"][0]) else outcome)
                  for name, outcome in trx_results_value]
    rejects(lambda: validate_trx(failed_trx, manifest), "failed focused test")

    skipped_trx = [(name, "NotExecuted" if name.startswith(manifest["requiredTests"]["theories"][0]) else outcome)
                   for name, outcome in trx_results_value]
    rejects(lambda: validate_trx(skipped_trx, manifest), "skipped focused test")

    print("negative controls passed")


def validate_evidence_records(records, manifest, implementation):
    with tempfile.TemporaryDirectory(prefix="pisharp-app-control-evidence-") as directory:
        path = Path(directory) / "evidence.jsonl"
        path.write_text("\n".join(json.dumps(value) for value in records.values()) + "\n", encoding="utf-8")
        return validate_evidence(path, manifest, implementation)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-app-control-acceptance-2026-11-03.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-app-control-pi-oracle-2026-11-03.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-app-control-checkpoints-2026-11-03.json")
    parser.add_argument("--implementation")
    parser.add_argument("--pi", type=Path)
    parser.add_argument("--trx", type=Path)
    parser.add_argument("--evidence", type=Path)
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
        trx = args.trx
        evidence = args.evidence
        if trx is None or evidence is None:
            trx, evidence = run_focused_tests(implementation)
        results = trx_results(trx)
        records = {record["id"]: record for record in
                   (json.loads(line) for line in Path(evidence).read_text(encoding="utf-8").splitlines()
                    if line.strip())}
        self_test(manifest, oracle, checkpoints, records, results)
        return
    validate_manifest(manifest, implementation)
    validate_oracle(oracle, manifest, args.pi)
    if args.pi:
        run(["python3", "tools/parity/pi_reference.py", "verify", "--pi", str(args.pi)])
    index, by_fixture, dimensions, modes = validate_checkpoints(checkpoints, manifest)
    validate_oracle_pty_scenarios(oracle, index, by_fixture)
    if args.full_trx:
        validate_full_trx(trx_results(args.full_trx))
    if args.trx and args.evidence:
        trx, evidence = args.trx, args.evidence
    elif args.skip_tests:
        raise VerificationError("--skip-tests requires --trx and --evidence; required executable evidence is missing.")
    else:
        trx, evidence = run_focused_tests(implementation)
    results = trx_results(trx)
    validate_trx(results, manifest)
    evidence_ids = validate_evidence(evidence, manifest, implementation)
    validate_case_references(manifest, oracle, results, evidence_ids, index, by_fixture)
    print(f"keys-app-control verified at {implementation}: 16 cases, {len(oracle['scenarios'])} pinned-Pi "
          f"scenarios, {len(checkpoints['cases'])} paired cases, {len(index)} captured checkpoints, "
          f"dimensions {sorted(dimensions)}.")


if __name__ == "__main__":
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
