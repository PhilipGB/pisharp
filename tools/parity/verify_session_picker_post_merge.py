#!/usr/bin/env python3
"""Verify the F01-F03 session-picker post-merge acceptance manifest."""
import argparse
import copy
import json
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from verify_keys_queue_session import (VerificationError, load_json, require, run, trx_results,
                                       validate_full_trx, validate_oracle_sources)
from session_picker_findings_evidence import normalize

ROOT = Path(__file__).resolve().parent.parent.parent
REQUIRED_CASES = ["F01", "F02", "F03"]
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"
BASELINE_SHA = "a4b51240c0426508ac4fe5c5861b7e914dd7307f"
MAIN_FIXTURE = "terminal-session-picker-findings"
DISABLED_FIXTURE = "terminal-session-picker-disabled-confirm"
EXPECTED_ROWS = [{"name": "duplicate two", "prefix": ""}, {"name": "tree parent", "prefix": ""},
                 {"name": "tree child", "prefix": "└─"}, {"name": "orphan child", "prefix": ""},
                 {"name": "duplicate one", "prefix": ""}]
EXPECTED_REMAINING_ROWS = [{"name": "tree parent", "prefix": ""},
                           {"name": "tree child", "prefix": "└─"},
                           {"name": "orphan child", "prefix": ""},
                           {"name": "duplicate one", "prefix": ""}]
EXPECTED_SESSION_FILES = ["child", "duplicate-one", "orphan", "parent"]


def validate_manifest(manifest, implementation):
    require(manifest.get("schema") == 1, "Manifest schema must be 1.")
    require(manifest.get("capability") == "session-picker-post-merge-findings",
            "Manifest capability is wrong.")
    require(manifest.get("piSha") == PI_SHA, f"Manifest Pi SHA is {manifest.get('piSha')!r}.")
    recorded = manifest.get("implementationSha")
    require(len(recorded or "") == 40, "Manifest must record a 40-character implementation SHA.")
    require(recorded == implementation,
            f"Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.")
    cases = manifest.get("cases") or []
    ids = [case.get("id") for case in cases]
    require(ids == REQUIRED_CASES, f"Manifest cases must be {REQUIRED_CASES}, found {ids}.")
    for case in cases:
        require(case.get("required") is True, f"{case['id']} must be required.")
        require(case.get("status") == "pass", f"{case['id']} is {case.get('status')!r}.")
        require(case.get("oracle") and case.get("tests") and case.get("terminalCheckpoints"),
                f"{case['id']} has no complete executable evidence.")
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
    require(tests.get("facts") and tests.get("ptyEvidence"),
            "Manifest must list required facts and PTY evidence ids.")
    baseline = manifest.get("baselineReproduction") or {}
    require(baseline.get("baselineSha") == BASELINE_SHA,
            f"Baseline reproduction SHA is {baseline.get('baselineSha')!r}.")
    require(baseline.get("fixture") == "tools/parity/session_picker_findings_baseline.py",
            "Baseline reproduction fixture is missing.")
    require(baseline.get("result") == "3 failed, 0 passed",
            f"Baseline reproduction result is {baseline.get('result')!r}.")
    require(baseline.get("tests") == ["F01SameIdSessionsDoNotSharePickerIdentity",
                                      "F02ThreadedModeGroupsChildrenUnderTheirParent",
                                      "F03DisabledConfirmationBindingDoesNotAdvertiseEnter"],
            "Baseline reproduction test inventory is wrong.")
    require(manifest.get("terminalFixtures") == [
        "tools/parity/terminal/fixtures/terminal-session-picker-findings.json",
        "tools/parity/terminal/fixtures/terminal-session-picker-disabled-confirm.json"],
        "Manifest must name both session-picker finding fixtures.")


def validate_oracle(oracle, manifest, pi_checkout=None):
    require(oracle.get("capability") == "session-picker-post-merge-findings",
            "Oracle capability is wrong.")
    require(oracle.get("piSha") == manifest.get("piSha"),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get("scenarios") or {}
    require(list(scenarios) == REQUIRED_CASES,
            f"Oracle scenarios must be {REQUIRED_CASES}, found {list(scenarios)}.")
    for name, scenario in scenarios.items():
        require(scenario.get("source"), f"Oracle scenario {name} has no source references.")
        expected = scenario.get("expected") or {}
        require(expected, f"Oracle scenario {name} has no expected observation.")
    require(scenarios["F01"]["expected"].get("identity") == "canonical-session-path",
            "F01 oracle does not require canonical path identity.")
    require(scenarios["F02"]["expected"].get("rootOrder") == "subtree-latest-activity-desc",
            "F02 oracle does not require subtree activity ordering.")
    require(scenarios["F03"]["expected"].get("disabledConfirmKey") == "",
            "F03 oracle does not require an empty disabled confirm key.")
    validate_oracle_sources(oracle, pi_checkout)


def validate_terminal_fixtures(manifest):
    main = load_json(ROOT / manifest["terminalFixtures"][0])
    require(main.get("id") == MAIN_FIXTURE, "Main terminal fixture id is wrong.")
    require(main.get("persistSessions") is True, "Main fixture must persist isolated sessions.")
    require(main.get("dimensions") == [{"columns": 140, "rows": 40}],
            "Main fixture must use the 140x40 paired dimensions.")
    require(main.get("modes") == ["fullscreen"], "Main fixture must run fullscreen.")
    action_ids = [action.get("id") for action in main.get("actions") or []]
    for expected in ("picker-open", "picker-tree", "picker-delete-confirm",
                     "picker-delete-applied", "picker-delete-settled", "quit"):
        require(expected in action_ids, f"Main fixture is missing {expected!r}.")
    require(set(main.get("assertionFrames") or []) <= set(action_ids),
            "Main fixture assertion frames reference unknown actions.")
    disabled = load_json(ROOT / manifest["terminalFixtures"][1])
    require(disabled.get("id") == DISABLED_FIXTURE, "Disabled-confirm fixture id is wrong.")
    require(disabled.get("persistSessions") is True, "Disabled fixture must persist sessions.")
    require(disabled.get("dimensions") == [{"columns": 140, "rows": 40}],
            "Disabled fixture must use the 140x40 paired dimensions.")
    require(disabled.get("modes") == ["regular"], "Disabled fixture must run regular mode.")
    action_ids = [action.get("id") for action in disabled.get("actions") or []]
    for expected in ("picker-confirm-open", "picker-confirm-enter", "picker-confirm-cancel", "quit"):
        require(expected in action_ids, f"Disabled fixture is missing {expected!r}.")
    keybindings = disabled.get("files", {}).get("pi", {}).get("keybindings.json")
    require(keybindings and "\"tui.select.confirm\": []" in keybindings,
            "Disabled fixture must disable the confirm binding.")


def checkpoint_index(checkpoints):
    index = {}
    by_case = {}
    for fixture_id, case in (checkpoints.get("cases") or {}).items():
        prefix = f"{fixture_id}/{case['case']}"
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


def require_checkpoint(case, fixture_id, checkpoint_id, product="pi"):
    require(fixture_id in case, f"Missing paired case {fixture_id}.")
    checkpoints = case[fixture_id][product]["checkpoints"]
    require(checkpoint_id in checkpoints,
            f"{product} checkpoint {fixture_id}/{checkpoint_id} is missing.")
    return checkpoints[checkpoint_id]


def validate_checkpoints(checkpoints, manifest):
    require(checkpoints.get("piSha") == manifest.get("piSha"),
            f"Checkpoint pin is {checkpoints.get('piSha')!r}.")
    cases = checkpoints.get("cases") or {}
    require(set(cases) == {MAIN_FIXTURE, DISABLED_FIXTURE},
            f"Checkpoint cases are {sorted(cases)}.")
    for fixture_id, case in cases.items():
        calibration = case.get("calibration") or {}
        require(not calibration.get("scenarioErrors"),
                f"{fixture_id} calibration errors: {calibration.get('scenarioErrors')}")
        require(calibration.get("httpMatch") is True, f"{fixture_id} calibration HTTP differs.")
        require(calibration.get("semanticDifferences") == 0,
                f"{fixture_id} calibration differences: {calibration.get('semanticFirstDifferences')}")
        paired = case.get("paired") or {}
        require(not paired.get("scenarioErrors"),
                f"{fixture_id} paired errors: {paired.get('scenarioErrors')}")
        require(paired.get("httpMatch") is True, f"{fixture_id} paired HTTP differs.")
        require(paired.get("semanticDifferences") == 0,
                f"{fixture_id} paired differences: {paired.get('semanticFirstDifferences')}")
        pi = case["pi"]["checkpoints"]
        pisharp = case["pisharp"]["checkpoints"]
        require(set(pi) == set(pisharp), f"{fixture_id} products captured different checkpoints.")
        for checkpoint_id in pi:
            require(normalize(pi[checkpoint_id]) == normalize(pisharp[checkpoint_id]),
                    f"{fixture_id}/{checkpoint_id} semantic checkpoint differs.")
        require(case["pi"]["sessionFiles"] == case["pisharp"]["sessionFiles"],
                f"{fixture_id} session files differ.")

    main = cases[MAIN_FIXTURE]
    for product in ("pi", "pisharp"):
        opened = require_checkpoint(cases, MAIN_FIXTURE, "picker-open", product)["picker"]
        require(opened["rows"] == EXPECTED_ROWS,
                f"main/{product} threaded rows are {opened['rows']!r}.")
        require(opened["selected"] == "duplicate two",
                f"main/{product} initial selection is {opened['selected']!r}.")
        confirm = require_checkpoint(cases, MAIN_FIXTURE, "picker-delete-confirm", product)["picker"]
        require(confirm["confirmation"] is True and confirm["confirmationKeys"] ==
                {"confirm": "enter", "cancel": "escape/ctrl+c"},
                f"main/{product} confirmation checkpoint is {confirm!r}.")
        applied = require_checkpoint(cases, MAIN_FIXTURE, "picker-delete-applied", product)["picker"]
        require(applied["rows"] == EXPECTED_REMAINING_ROWS,
                f"main/{product} deletion left {applied['rows']!r}.")
        require(applied["statuses"] == ["Session deleted"],
                f"main/{product} deletion status is {applied['statuses']!r}.")
        settled = require_checkpoint(cases, MAIN_FIXTURE, "picker-delete-settled", product)["picker"]
        require(settled["rows"] == EXPECTED_REMAINING_ROWS,
                f"main/{product} settled rows are {settled['rows']!r}.")
        require(main[product]["sessionFiles"] == EXPECTED_SESSION_FILES,
                f"main/{product} session files are {main[product]['sessionFiles']!r}.")
        quit_state = require_checkpoint(cases, MAIN_FIXTURE, "quit", product)
        require(quit_state["activeScreen"] == "normal" and
                quit_state["temporaryModes"] == [] and quit_state["cursorVisible"] is True,
                f"main/{product} did not restore the terminal: {quit_state!r}.")

    disabled = cases[DISABLED_FIXTURE]
    for product in ("pi", "pisharp"):
        opened = require_checkpoint(cases, DISABLED_FIXTURE, "picker-confirm-open", product)["picker"]
        require(opened["confirmation"] is True, f"disabled/{product} confirmation did not open.")
        require(opened["confirmationKeys"] == {"confirm": "", "cancel": "escape/ctrl+c"},
                f"disabled/{product} confirmation keys are {opened['confirmationKeys']!r}.")
        entered = require_checkpoint(cases, DISABLED_FIXTURE, "picker-confirm-enter", product)["picker"]
        require(entered["confirmation"] is True and entered["confirmationKeys"]["confirm"] == "",
                f"disabled/{product} Enter changed confirmation: {entered!r}.")
        cancelled = require_checkpoint(cases, DISABLED_FIXTURE, "picker-confirm-cancel", product)["picker"]
        require(cancelled["confirmation"] is False,
                f"disabled/{product} Escape did not cancel confirmation.")
        require(disabled[product]["sessionFiles"] == ["victim"],
                f"disabled/{product} session files are {disabled[product]['sessionFiles']!r}.")
        quit_state = require_checkpoint(cases, DISABLED_FIXTURE, "quit", product)
        require(quit_state["activeScreen"] == "normal" and
                quit_state["temporaryModes"] == [] and quit_state["cursorVisible"] is True,
                f"disabled/{product} did not restore the terminal: {quit_state!r}.")
    return checkpoint_index(checkpoints)


def validate_case_references(manifest, oracle, results, index, by_case):
    allowed_tests = set(manifest["requiredTests"]["facts"])
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
            require(method in passing,
                    f"{case['id']} references a test that did not pass: {method!r}.")
            referenced_tests.add(method)
        for checkpoint in case.get("terminalCheckpoints") or []:
            resolve_checkpoint(checkpoint, index, by_case)
    require(referenced_tests == allowed_tests,
            f"Required tests are not referenced by any case: {sorted(allowed_tests - referenced_tests)}")
    referenced_scenarios = {name for case in manifest["cases"] for name in (case.get("oracle") or [])}
    require(referenced_scenarios == scenario_names,
            f"Oracle scenarios are not referenced: {sorted(scenario_names - referenced_scenarios)}")


def validate_trx(results, manifest):
    skipped = {"NotExecuted", "Skipped", "Inconclusive"}
    for method in manifest["requiredTests"]["facts"]:
        matching = [(name, outcome) for name, outcome in results if name == method]
        require(matching, f"Required test did not run: {method}")
        for name, outcome in matching:
            require(outcome not in skipped, f"Required test was skipped: {name} ({outcome}).")
            require(outcome == "Passed", f"Required test failed: {name} ({outcome}).")


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix="pisharp-picker-findings-verify-")
    trx = Path(directory) / "picker-findings.trx"
    environment = dict(__import__("os").environ)
    environment["PISHARP_IMPLEMENTATION_SHA"] = implementation
    run(["dotnet", "test", "tests/PiSharp.Tests/PiSharp.Tests.csproj", "--filter",
         "FullyQualifiedName~SessionPickerFindingsTests|FullyQualifiedName~SessionPickerFindingsParityTests|"
         "FullyQualifiedName~KeysQueueSessionTests|FullyQualifiedName~KeysQueueSessionPtyTests",
         "--logger", "trx;LogFileName=picker-findings.trx", "--results-directory", directory,
         "--nologo", "-v", "q"], env=environment)
    return trx


def validate_baseline(manifest, baseline):
    baseline = Path(baseline)
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=baseline, text=True).strip()
    require(head == manifest["baselineReproduction"]["baselineSha"],
            f"Baseline worktree is {head}, expected {manifest['baselineReproduction']['baselineSha']}.")
    output = run([sys.executable, "tools/parity/session_picker_findings_baseline.py",
                  "--baseline", str(baseline)])
    require("F01, F02 and F03 failed as expected" in output,
            "Baseline reproduction did not fail F01-F03 as expected.")


def self_test(manifest, oracle, checkpoints, results):
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

    missing = copy.deepcopy(manifest)
    missing["cases"] = [case for case in missing["cases"] if case["id"] != "F03"]
    rejects(lambda: validate_manifest(missing, implementation), "missing frozen case")
    duplicate = copy.deepcopy(manifest)
    duplicate["cases"].append(copy.deepcopy(duplicate["cases"][0]))
    rejects(lambda: validate_manifest(duplicate, implementation), "duplicate case")
    failed = copy.deepcopy(manifest)
    failed["cases"][0]["status"] = "failed"
    rejects(lambda: validate_manifest(failed, implementation), "failed case")
    stale = copy.deepcopy(manifest)
    stale["implementationSha"] = "1" * 40
    rejects(lambda: validate_manifest(stale, implementation), "stale implementation SHA")
    wrong_pi = copy.deepcopy(manifest)
    wrong_pi["piSha"] = "2" * 40
    rejects(lambda: validate_manifest(wrong_pi, implementation), "wrong Pi SHA")
    unresolved = copy.deepcopy(manifest)
    unresolved["requiredComparisons"][0]["status"] = "differs"
    rejects(lambda: validate_manifest(unresolved, implementation), "unresolved comparison")
    bad_baseline = copy.deepcopy(manifest)
    bad_baseline["baselineReproduction"]["result"] = "passed"
    rejects(lambda: validate_manifest(bad_baseline, implementation), "baseline unexpectedly passing")

    bad_oracle = copy.deepcopy(oracle)
    bad_oracle["scenarios"]["F01"].pop("expected")
    rejects(lambda: validate_oracle(bad_oracle, manifest), "incomplete oracle scenario")
    bad_sources = copy.deepcopy(oracle)
    bad_sources["sources"] = []
    rejects(lambda: validate_oracle_sources(bad_sources), "missing oracle source inventory")

    def mutate(fixture_id, checkpoint, mutate_value, label):
        bad = copy.deepcopy(checkpoints)
        mutate_value(bad["cases"][fixture_id]["pisharp"]["checkpoints"][checkpoint])
        rejects(lambda: validate_checkpoints(bad, manifest), label)

    mutate(MAIN_FIXTURE, "picker-open",
           lambda value: value["picker"]["rows"][2].__setitem__("prefix", ""),
           "tree prefix missing")
    mutate(MAIN_FIXTURE, "picker-open",
           lambda value: value["picker"]["rows"].reverse(), "wrong tree order")
    mutate(MAIN_FIXTURE, "picker-delete-applied",
           lambda value: value["picker"]["rows"].append({"name": "duplicate two", "prefix": ""}),
           "deleted the wrong same-ID session")
    mutate(MAIN_FIXTURE, "picker-delete-confirm",
           lambda value: value["picker"].__setitem__("confirmationKeys", {"confirm": "Enter", "cancel": ""}),
           "confirmation keys advertised incorrectly")
    mutate(DISABLED_FIXTURE, "picker-confirm-open",
           lambda value: value["picker"]["confirmationKeys"].__setitem__("confirm", "enter"),
           "disabled confirm key advertised")
    mutate(DISABLED_FIXTURE, "picker-confirm-enter",
           lambda value: value["picker"].__setitem__("confirmation", False),
           "disabled Enter confirmed")
    mutate(DISABLED_FIXTURE, "picker-confirm-cancel",
           lambda value: value["picker"].__setitem__("confirmation", True),
           "disabled cancel remained open")
    bad_calibration = copy.deepcopy(checkpoints)
    bad_calibration["cases"][MAIN_FIXTURE]["calibration"]["semanticDifferences"] = 1
    rejects(lambda: validate_checkpoints(bad_calibration, manifest), "failed same-Pi calibration")
    bad_files = copy.deepcopy(checkpoints)
    bad_files["cases"][MAIN_FIXTURE]["pisharp"]["sessionFiles"].append("duplicate-two")
    rejects(lambda: validate_checkpoints(bad_files, manifest), "session file identity mismatch")
    bad_pty = copy.deepcopy(checkpoints)
    del bad_pty["cases"][DISABLED_FIXTURE]
    rejects(lambda: validate_checkpoints(bad_pty, manifest), "missing paired PTY evidence")

    bad_case_ref = copy.deepcopy(manifest)
    bad_case_ref["cases"][0]["terminalCheckpoints"] = [
        "terminal-session-picker-findings/does-not-exist/startup"]
    rejects(lambda: validate_case_references(bad_case_ref, oracle, results, index, by_case),
            "nonexistent checkpoint reference")
    bad_test_ref = copy.deepcopy(manifest)
    bad_test_ref["cases"][0]["tests"] = ["PiSharp.Tests.SessionPickerFindingsTests.DoesNotExist"]
    rejects(lambda: validate_case_references(bad_test_ref, oracle, results, index, by_case),
            "nonexistent test reference")
    bad_oracle_ref = copy.deepcopy(manifest)
    bad_oracle_ref["cases"][0]["oracle"] = ["F04"]
    rejects(lambda: validate_case_references(bad_oracle_ref, oracle, results, index, by_case),
            "nonexistent oracle reference")

    failed_trx = [(name, "Failed" if name == manifest["requiredTests"]["facts"][0] else outcome)
                  for name, outcome in results]
    rejects(lambda: validate_trx(failed_trx, manifest), "failed required test")
    skipped_trx = [(name, "Skipped") for name, _ in results]
    rejects(lambda: validate_trx(skipped_trx, manifest), "skipped required test")
    failed_full = results + [("PiSharp.Tests.SomeTest", "Failed")]
    rejects(lambda: validate_full_trx(failed_full), "failed full suite")
    skipped_full = results + [("PiSharp.Tests.SomeTest", "Skipped")]
    rejects(lambda: validate_full_trx(skipped_full), "skipped full suite")
    print("negative controls passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path,
                        default=ROOT / "docs/parity/fixtures/session-picker-post-merge-acceptance-2026-11-04.json")
    parser.add_argument("--oracle", type=Path,
                        default=ROOT / "docs/parity/fixtures/session-picker-post-merge-oracle-2026-11-04.json")
    parser.add_argument("--checkpoints", type=Path,
                        default=ROOT / "docs/parity/fixtures/session-picker-post-merge-checkpoints-2026-11-04.json")
    parser.add_argument("--implementation")
    parser.add_argument("--pi", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--trx", type=Path)
    parser.add_argument("--full-trx", type=Path)
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
    validate_terminal_fixtures(manifest)
    if args.pi:
        run(["python3", "tools/parity/pi_reference.py", "verify", "--pi", str(args.pi)])
    index, by_case = validate_checkpoints(checkpoints, manifest)
    if args.baseline:
        validate_baseline(manifest, args.baseline)
    if args.full_trx:
        validate_full_trx(trx_results(args.full_trx))
    trx = args.trx if args.trx else run_focused_tests(implementation)
    results = trx_results(trx)
    validate_trx(results, manifest)
    validate_case_references(manifest, oracle, results, index, by_case)
    print(f"session-picker-post-merge-findings verified at {implementation}: "
          f"{len(manifest['cases'])} findings, {len(index)} paired checkpoints, "
          f"baseline {manifest['baselineReproduction']['baselineSha']} failed as expected.")


if __name__ == "__main__":
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(f"verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
