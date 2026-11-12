#!/usr/bin/env python3
"""Assemble the committed `branchSummary.skipPrompt` evidence and the pinned oracle.

The script refuses to emit evidence when either side of a differential disagrees, and it
cross-checks the hand-frozen runtime decision table against the paired PTY observations.
Run it after the harness fixtures, the settings differential and the compact extracts.
"""
import argparse
import json
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ARTIFACTS = ROOT / "artifacts/parity/branch-summary-skip-prompt"
FIXTURES = ROOT / "docs/parity/fixtures"
DATE = "2026-11-12"
CAPABILITY = "settings-branchsummary-skipprompt"
PI_SHA = "b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6"

# (documentation label, paired run directory, emitted checkpoint file)
PAIRED = [
    ("fullscreen", "terminal-branch-summary-skip-prompt-paired",
     f"settings-branchsummary-skipprompt-checkpoints-fullscreen-{DATE}.json"),
    ("regular", "terminal-branch-summary-skip-prompt-regular-paired",
     f"settings-branchsummary-skipprompt-checkpoints-regular-{DATE}.json"),
    ("malformed", "terminal-branch-summary-skip-prompt-malformed-paired",
     f"settings-branchsummary-skipprompt-checkpoints-malformed-{DATE}.json"),
    ("project", "terminal-branch-summary-skip-prompt-project-paired",
     f"settings-branchsummary-skipprompt-checkpoints-project-{DATE}.json"),
    ("project-null", "terminal-branch-summary-skip-prompt-project-null-paired",
     f"settings-branchsummary-skipprompt-checkpoints-project-null-{DATE}.json"),
    ("project-reserve", "terminal-branch-summary-skip-prompt-project-reserve-paired",
     f"settings-branchsummary-skipprompt-checkpoints-project-reserve-{DATE}.json"),
    ("project-malformed", "terminal-branch-summary-skip-prompt-project-malformed-paired",
     f"settings-branchsummary-skipprompt-checkpoints-project-malformed-{DATE}.json"),
    ("project-malformed-truthy", "terminal-branch-summary-skip-prompt-project-malformed-truthy-paired",
     f"settings-branchsummary-skipprompt-checkpoints-project-malformed-truthy-{DATE}.json"),
]

# Runtime decision table frozen from pinned Pi's getter and `showTreeSelector` flow.
# (label, checkpoint, prompts, expected tree tags or None)
DECISIONS = {
    "fullscreen": [
        ("unset-tree-selected", True, None),
        ("unset-prompt-cancel", False, None),
        ("unset-reopen-tree-selected", True, None),
        ("unset-reopen-navigate", False, None),
        ("false-tree-selected", True, None),
        ("true-tree-selected", False, None),
        ("true-restart-tree-selected", False, None),
        ("false-again-tree-selected", True, None),
        ("filtered-tree-open", False, ["all"]),
        ("filtered-tree-selected", False, None),
    ],
    "malformed": [
        ("malformed-string-tree-selected", False, None),
        ("empty-string-tree-selected", True, None),
        ("number-tree-selected", False, None),
        ("zero-tree-selected", True, None),
        ("object-tree-selected", False, None),
        ("array-tree-selected", False, None),
        ("null-tree-selected", True, None),
        ("parent-null-tree-selected", True, None),
        ("parent-string-tree-selected", True, None),
        ("parent-number-tree-selected", True, None),
        ("parent-array-tree-selected", True, None),
    ],
    "project": [
        ("trusted-tree-selected", True, None),
        ("trusted-restart-tree-selected", True, None),
        ("untrusted-tree-selected", False, None),
    ],
    "project-null": [
        ("trusted-tree-selected", True, None),
        ("trusted-restart-tree-selected", True, None),
        ("untrusted-tree-selected", False, None),
    ],
    "project-reserve": [
        ("trusted-tree-selected", False, None),
        ("trusted-restart-tree-selected", False, None),
        ("untrusted-tree-selected", False, None),
    ],
    "project-malformed": [
        ("trusted-tree-selected", True, None),
        ("trusted-restart-tree-selected", True, None),
        ("untrusted-tree-selected", False, None),
    ],
    "project-malformed-truthy": [
        ("trusted-tree-selected", False, None),
        ("trusted-restart-tree-selected", False, None),
        ("untrusted-tree-selected", False, None),
    ],
}

PROMPT_CHOICES = ["No summary", "Summarize", "Summarize with custom prompt"]
NAVIGATION_CHECKPOINTS = ("true-tree-after-state", "untrusted-tree-after-state")


def load(path):
    return json.loads(Path(path).read_text())


def find_checkpoint(document, checkpoint):
    case = next(iter(document["cases"].values()))
    return case["pi"]["checkpoints"][checkpoint]


def build_settings_cases(differential):
    cases = []
    for case in differential["cases"]:
        pi, pisharp = case["pi"], case["pisharp"]
        if pi != pisharp:
            raise SystemExit(f"{case['id']}: settings differential disagrees: {pi} != {pisharp}")
        cases.append({
            "id": case["id"],
            "expect": {key: pi[key] for key in
                       ("loaded", "prompts", "skipPrompt", "parentPresent", "reserve")},
        })
    return cases


def build_projections(label, document):
    case = next(iter(document["cases"].values()))
    pi = case["pi"]
    if pi != case["pisharp"]:
        raise SystemExit(f"{label}: paired evidence is not equal; extract again")
    if pi["requests"]:
        raise SystemExit(f"{label}: unexpected provider request {pi['requests']}")
    if pi["session"] and pi["session"]["branchSummaryIds"]:
        raise SystemExit(f"{label}: branch summary entry present in the persisted session")
    for checkpoint_id, state in pi["checkpoints"].items():
        if state["errors"]:
            raise SystemExit(f"{label}/{checkpoint_id}: error text {state['errors']}")
        if checkpoint_id == "startup":
            continue
        tree = state["tree"]
        if tree and tree["summaryRows"]:
            raise SystemExit(f"{label}/{checkpoint_id}: branch summary row {tree['summaryRows']}")
    projections = {}
    for checkpoint_id, prompts, tags in DECISIONS[label]:
        state = pi["checkpoints"].get(checkpoint_id)
        if state is None:
            raise SystemExit(f"{label}: missing checkpoint {checkpoint_id}")
        observed_prompts = state["prompt"] is not None
        if observed_prompts != prompts:
            raise SystemExit(f"{label}/{checkpoint_id}: pinned Pi prompts={observed_prompts}, "
                             f"decision table says {prompts}")
        if observed_prompts and state["prompt"]["choices"] != PROMPT_CHOICES:
            raise SystemExit(f"{label}/{checkpoint_id}: choices {state['prompt']['choices']}")
        tree = state["tree"]
        if tags is not None and (tree is None or tree["tags"] != tags):
            raise SystemExit(f"{label}/{checkpoint_id}: tags {tree and tree['tags']}, expected {tags}")
        projections[checkpoint_id] = {
            "prompts": prompts,
            "choices": PROMPT_CHOICES if prompts else None,
            "tree": None if tree is None else {
                "rows": tree["rows"], "count": tree["count"], "tags": tree["tags"],
                "selected": tree["selected"], "summaryRows": tree["summaryRows"]},
        }
    for checkpoint_id in NAVIGATION_CHECKPOINTS:
        if checkpoint_id in pi["checkpoints"]:
            tree = pi["checkpoints"][checkpoint_id]["tree"]
            if tree["selected"] != "a0000003":
                raise SystemExit(f"{label}/{checkpoint_id}: selected {tree['selected']}, "
                                 "expected the seeded first answer")
    return projections


def build_oracle(differential, documents):
    settings_cases = build_settings_cases(differential)
    expectations = {case["id"]: case["expect"] for case in settings_cases}
    scenarios = []
    for label, _, _ in PAIRED:
        for checkpoint_id, prompts, _ in DECISIONS[label]:
            scenarios.append({
                "id": f"{label}:{checkpoint_id}",
                "fixture": label,
                "checkpoint": checkpoint_id,
                "prompts": prompts,
                "choices": PROMPT_CHOICES if prompts else None,
            })
    return {
        "schema": 1,
        "capability": CAPABILITY,
        "piSha": PI_SHA,
        "default": False,
        "schemaContract": {
            "description": "When true, skips the \"Summarize branch?\" prompt and defaults to no summary.",
            "getter": "this.settings.branchSummary?.skipPrompt ?? SETTINGS_DEFAULTS.branchSummary.skipPrompt",
            "promptGate": "if (!this.settingsManager.getBranchSummarySkipPrompt()) { show \"Summarize branch?\" }",
            "navigation": "navigateTree(entryId, { summarize: false }) when the prompt is skipped",
        },
        "sources": [
            {"file": "packages/coding-agent/src/core/settings-defaults.ts", "lines": "14-17",
             "symbol": "SETTINGS_DEFAULTS.branchSummary",
             "observation": "reserveTokens is 16384 and skipPrompt is false."},
            {"file": "packages/coding-agent/src/core/settings-schema.ts", "lines": "31-42",
             "symbol": "BranchSummarySettingsSchema",
             "observation": "skipPrompt is an optional boolean described as skipping the "
                            "'Summarize branch?' prompt and defaulting to no summary."},
            {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "58-82",
             "symbol": "isMergeableObject/deepMergeObjects",
             "observation": "Scopes merge by property presence; only undefined is skipped, arrays and "
                            "other non-objects are not mergeable and replace the lower value."},
            {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "366-409",
             "symbol": "loadFromStorage/tryLoadFromStorage",
             "observation": "Settings files are JSON.parse'd and migrated only; stored values are never "
                            "validated against the schema, so malformed shapes load."},
            {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "425-450",
             "symbol": "deepMergeSettings",
             "observation": "The effective settings are the user settings deep-merged with the trusted "
                            "project settings; an untrusted project contributes {}."},
            {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "882-891",
             "symbol": "getBranchSummarySettings/getBranchSummarySkipPrompt",
             "observation": "Both getters return the merged value unless it is nullish, so a stored "
                            "string, number, object or array is returned unchanged."},
            {"file": "packages/coding-agent/src/modes/interactive/interactive-mode.ts",
             "lines": "5602-5622", "symbol": "showTreeSelector summarization gate",
             "observation": "The summary selector is skipped when the getter is falsey; wantsSummary "
                            "stays false and navigateTree receives summarize: false."},
            {"file": "packages/coding-agent/src/modes/interactive/components/settings-selector.ts",
             "lines": "485-700", "symbol": "main settings items",
             "observation": "No branchSummary.skipPrompt row exists: `grep -n skipPrompt` finds nothing "
                            "in the settings selector, so the setting is file-only."},
            {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "629-651",
             "symbol": "save/persistScopedSettings",
             "observation": "Writes only enqueue the global scope, so a file-authored skipPrompt entry "
                            "is never rewritten by the tree or summarization flows."},
        ],
        "settingsCases": settings_cases,
        "scenarioFacts": scenarios,
        "projections": {label: build_projections(label, documents[label]) for label in documents},
        "navigationCheckpoints": list(NAVIGATION_CHECKPOINTS),
        "safetyProjections": [],
        "expectations": expectations,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", type=Path,
                        default=ARTIFACTS / "differential-after.json")
    parser.add_argument("--output", type=Path,
                        default=FIXTURES / f"settings-branchsummary-skipprompt-pi-oracle-{DATE}.json")
    parser.add_argument("--settings-output", type=Path,
                        default=FIXTURES / f"settings-branchsummary-skipprompt-settings-boundary-{DATE}.json")
    parser.add_argument("--copy-checkpoints", action="store_true")
    args = parser.parse_args()

    differential = load(args.settings)
    if differential["piSha"] != PI_SHA:
        raise SystemExit(f"settings differential Pi SHA is {differential['piSha']}")
    if differential["differences"]:
        raise SystemExit(f"settings differential has {len(differential['differences'])} differences")

    documents = {}
    for label, directory, target in PAIRED:
        path = ARTIFACTS / directory
        checkpoint_path = ARTIFACTS / f"{label}-checkpoints.json"
        subprocess.run(["python3", str(ROOT / "tools/parity/branch_summary_skip_prompt_evidence.py"),
                        "--input", str(path), "--output", str(checkpoint_path)], check=True)
        document = load(checkpoint_path)
        case = next(iter(document["cases"].values()))
        if case["paired"]["semanticDifferences"]:
            raise SystemExit(f"{label}: {case['paired']['semanticDifferences']} semantic differences")
        if case.get("calibration", {}).get("semanticDifferences"):
            raise SystemExit(f"{label}: calibration differences")
        documents[label] = document
        if args.copy_checkpoints:
            (FIXTURES / target).write_text(json.dumps(document, indent=2) + "\n")
            print(f"wrote {FIXTURES / target}")

    oracle = build_oracle(differential, documents)
    args.output.write_text(json.dumps(oracle, indent=2) + "\n")
    print(f"wrote {args.output}")
    if args.settings_output:
        args.settings_output.write_text(json.dumps(differential, indent=2) + "\n")
        print(f"wrote {args.settings_output}")


if __name__ == "__main__":
    main()
