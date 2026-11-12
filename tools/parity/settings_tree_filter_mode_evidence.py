#!/usr/bin/env python3
"""Extract compact treeFilterMode checkpoints from paired PTY evidence.

Raw render streams stay in the ignored artifacts tree. This script keeps the
semantic observations the acceptance verifier compares: the tree panel rows with
their gutter geometry, selection, count and filter-status suffix, the settings
row/label/description/value sequence, the persisted settings document and any
scenario error.
"""
import argparse
import gzip
import json
import re
from pathlib import Path

# Tree row text -> pinned-Pi entry ids. Longest text first so labels embedded in a
# row prefix cannot shadow a longer entry label.
ENTRY_TOKENS = (
    ("[context omit: y0000012]", ["e0000018"]),
    ("[title: fixture session]", ["s0000017"]),
    ("[label: second answer label]", ["L0000016"]),
    ("[label: first label]", ["L0000006"]),
    ("[compaction: 12k tokens]", ["k0000011"]),
    ("[read: README.md:10-29]", ["r0000010"]),
    ("[bash]: echo fixture-output", ["z0000013"]),
    ("[note]: custom note text", ["y0000012"]),
    ("assistant: branch answer", ["b0000005"]),
    ("assistant: second answer", ["c0000008"]),
    ("assistant: first answer", ["a0000003"]),
    ("user: branch question", ["b0000004"]),
    ("user: second prompt", ["c0000007"]),
    ("user: first prompt", ["u0000002"]),
    ("[model: beta]", ["m0000001"]),
    ("[thinking: off]", ["t0000001", "t0000015"]),
)
COUNT = re.compile(r"\((\d+)/(\d+)\)")
TAGS = re.compile(r"\[(no-tools|user|labeled|all|\+label time)\]")
SEARCH = re.compile(r"Type to search:(?:\s+(.*))?")
SETTING_VALUES = ("default", "no-tools", "user-only", "labeled-only", "all")
ERRORS = re.compile(r"Invalid settings|unsupported property|Agent error")


def viewport_lines(frame):
    return ["".join(cell.get("chars", " ") for cell in line["cells"]).rstrip()
            for line in frame["state"].get("viewport", [])]


def normalize(line):
    """Remove PiSharp's overlay frame so both products' panel content compares."""
    text = line
    if text.startswith("| "):
        text = text[2:]
    if text.endswith(" |"):
        text = text[:-2]
    return text.rstrip()


def rows(lines):
    found = []
    occurrences = {}
    for line in lines:
        text = normalize(line)
        for token, ids in ENTRY_TOKENS:
            if token not in text:
                continue
            index = text.index(token)
            prefix = text[:index]
            occurrence = occurrences.get(token, 0)
            occurrences[token] = occurrence + 1
            found.append({
                "entry": ids[min(occurrence, len(ids) - 1)],
                "prefix": prefix,
                "selected": "\u203a" in prefix,
                "suffix": text[index + len(token):].rstrip(),
            })
            break
    return found


def tree_state(lines):
    if not any("Session Tree" in line for line in lines):
        return None
    count = None
    tags = []
    search = None
    empty = False
    for line in lines:
        text = normalize(line).strip()
        match = COUNT.search(text)
        if match and count is None:
            count = f"{match.group(1)}/{match.group(2)}"
            tags = TAGS.findall(text)
        match = SEARCH.search(text)
        if match is not None:
            search = (match.group(1) or "").strip()
        if "No entries found" in text:
            empty = True
    visible = rows(lines)
    return {
        "rows": [row["entry"] for row in visible],
        "gutter": [row["prefix"] for row in visible],
        "suffixes": [row["suffix"] for row in visible],
        "selected": next((row["entry"] for row in visible if row["selected"]), None),
        "count": count,
        "tags": tags,
        "search": search,
        "empty": empty,
    }


def settings_state(lines):
    row = None
    value = None
    description = None
    offered = []
    for line in lines:
        text = normalize(line).strip()
        if text.startswith("→"):
            text = text[1:].strip()
        if text.startswith("Tree filter mode"):
            row = text
            remainder = text[len("Tree filter mode"):].strip()
            value = remainder.split()[0] if remainder else None
        if "Default filter when opening /tree" in text:
            description = description or text
        if text in SETTING_VALUES and text not in offered:
            offered.append(text)
    if row is None and description is None and not offered:
        return None
    return {"row": row, "value": value, "description": description, "values": offered}


def checkpoint(frame):
    lines = viewport_lines(frame)
    text = "\n".join(lines)
    return {
        "tree": tree_state(lines),
        "settings": settings_state(lines),
        "activeScreen": frame["state"].get("activeScreen"),
        "errors": sorted({match.group(0) for match in ERRORS.finditer(text)}),
    }


def requests(assertion):
    return [((request.get("body") or {}).get("model") or request.get("method") or "")
            for request in assertion.get("http") or []]


def persisted_settings(assertion):
    raw = (assertion.get("capturedAgentFiles") or {}).get("settings.json")
    if not raw:
        return None
    try:
        document = json.loads(raw)
    except ValueError:
        return None
    return {
        "treeFilterMode": document.get("treeFilterMode"),
        "treeFilterModePresent": "treeFilterMode" in document,
        "quietStartup": document.get("quietStartup"),
        "shellCommandPrefix": document.get("shellCommandPrefix"),
        "theme": document.get("theme"),
        "tuiMode": document.get("tuiMode"),
    }


def product_evidence(assertion):
    return {
        "checkpoints": {frame["id"]: checkpoint(frame) for frame in assertion.get("frames") or []},
        "requests": requests(assertion),
        "settings": persisted_settings(assertion),
        "scenarioError": assertion.get("scenarioError"),
    }


def semantic_differences(left, right, path="$"):
    if type(left) is not type(right):
        return [{"path": path, "pi": left, "pisharp": right}]
    if isinstance(left, dict):
        result = []
        for key in sorted(left.keys() | right.keys()):
            if key not in left or key not in right:
                result.append({"path": f"{path}.{key}", "pi": left.get(key), "pisharp": right.get(key)})
            else:
                result.extend(semantic_differences(left[key], right[key], f"{path}.{key}"))
        return result
    if isinstance(left, list):
        result = []
        if len(left) != len(right):
            result.append({"path": f"{path}.length", "pi": len(left), "pisharp": len(right)})
        for index, (first, second) in enumerate(zip(left, right)):
            result.extend(semantic_differences(first, second, f"{path}[{index}]"))
        return result
    return [] if left == right else [{"path": path, "pi": left, "pisharp": right}]


SEMANTIC_KEYS = ("checkpoints", "requests", "settings")


def comparison(pi, pisharp, summary=None):
    differences = []
    for key in SEMANTIC_KEYS:
        differences.extend(semantic_differences(pi[key], pisharp[key], f"$.{key}"))
    return {
        "semanticDifferences": len(differences),
        "semanticFirstDifferences": differences[:20],
        "scenarioErrors": {name: value["scenarioError"] for name, value in
                           (("pi", pi), ("pisharp", pisharp)) if value["scenarioError"]},
        "runReport": {"match": (summary or {}).get("match"),
                      "differences": (summary or {}).get("differences")},
    }


def load_assertions(path):
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        return json.load(stream)


def extract_case(directory, case_id):
    path = directory / f"{case_id}.assertions.json.gz"
    if not path.is_file():
        raise SystemExit(f"missing assertion evidence: {path}")
    evidence = load_assertions(path)
    return evidence, product_evidence(evidence["pi"]), product_evidence(evidence["pisharp"])


def report_case(directory, case_id):
    path = directory / "report.json"
    if not path.is_file():
        return {}
    report = json.loads(path.read_text())
    return next((case for case in report.get("cases") or [] if case.get("id") == case_id), {})


def report_cases(directory):
    report = json.loads((Path(directory) / "report.json").read_text())
    return [case["id"] for case in report.get("cases") or []]


def extract_paired(directory, calibration_directory, cases, pisharp_sha):
    if not cases:
        cases = report_cases(directory)
    document = {"schema": 1, "capability": "settings-treefiltermode", "label": "paired",
                "cases": {}}
    for case_id in cases:
        evidence, pi, pisharp = extract_case(directory, case_id)
        case = {
            "dimensions": evidence.get("dimensions"),
            "theme": evidence.get("theme"),
            "mode": evidence.get("mode"),
            "pi": pi,
            "pisharp": pisharp,
            "paired": comparison(pi, pisharp, report_case(directory, case_id)),
        }
        if calibration_directory is not None:
            calibration, calibration_pi, calibration_second = extract_case(calibration_directory, case_id)
            case["calibration"] = comparison(calibration_pi, calibration_second,
                                             report_case(calibration_directory, case_id))
            case["calibrationEvidence"] = {"dimensions": calibration.get("dimensions"),
                                           "theme": calibration.get("theme"),
                                           "mode": calibration.get("mode")}
        document["cases"][case_id] = case
    if cases:
        report = json.loads((directory / "report.json").read_text())
        document["piSha"] = report.get("piSha")
        document["pisharpSha"] = pisharp_sha or report.get("pisharpSha")
        document["artifact"] = (report.get("provenance") or {}).get("pisharp", {}).get("artifact")
    return document


def extract_settings(directory):
    """Pinned-Pi settings-selector journey: both sides are independent Pi runs."""
    case_id = report_cases(directory)[0]
    evidence, pi, second = extract_case(directory, case_id)
    report = json.loads((directory / "report.json").read_text())
    return {
        "schema": 1,
        "capability": "settings-treefiltermode",
        "label": "settings-selector",
        "piSha": report.get("piSha"),
        "dimensions": evidence.get("dimensions"),
        "theme": evidence.get("theme"),
        "mode": evidence.get("mode"),
        "pi": pi,
        "calibration": comparison(pi, second, report_case(directory, case_id)),
    }


ORACLE_SOURCES = [
    {"file": "packages/coding-agent/src/core/settings-defaults.ts", "lines": "46",
     "symbol": "SETTINGS_DEFAULTS.treeFilterMode",
     "observation": "The shared runtime default is the string 'default'."},
    {"file": "packages/coding-agent/src/core/settings-schema.ts", "lines": "345-357",
     "symbol": "treeFilterMode",
     "observation": "Optional union of default, no-tools, user-only, labeled-only and all, "
                    "described as 'Default filter when opening /tree', defaulted from SETTINGS_DEFAULTS."},
    {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "1366-1375",
     "symbol": "getTreeFilterMode/setTreeFilterMode",
     "observation": "The getter reads the merged settings and returns the default unless the stored "
                    "value is one of the five literals; the setter writes global settings and saves."},
    {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "377-408",
     "symbol": "loadFromStorage/tryLoadFromStorage",
     "observation": "Settings files are JSON.parse'd and migrated only; values are never validated "
                    "against the schema, so an unrecognized value loads and resolves to the default."},
    {"file": "packages/coding-agent/src/modes/interactive/components/settings-selector.ts", "lines": "596-601",
     "symbol": "tree-filter-mode item",
     "observation": "Label 'Tree filter mode', description 'Default filter when opening /tree', "
                    "currentValue from config and values in pinned order."},
    {"file": "packages/coding-agent/src/modes/interactive/components/settings-selector.ts", "lines": "955-958",
     "symbol": "onTreeFilterModeChange",
     "observation": "A chosen value is forwarded to the interactive-mode callback."},
    {"file": "packages/coding-agent/src/modes/interactive/interactive-mode.ts", "lines": "5080-5082",
     "symbol": "onTreeFilterModeChange",
     "observation": "The callback persists the choice through settingsManager.setTreeFilterMode."},
    {"file": "packages/coding-agent/src/modes/interactive/interactive-mode.ts", "lines": "5575-5579",
     "symbol": "showTreeSelector",
     "observation": "Every /tree open reads getTreeFilterMode and passes initialFilterMode to the selector; "
                    "/tree, app.session.tree, configured double-Escape and the summary-cancel reopen share it."},
    {"file": "packages/coding-agent/src/modes/interactive/components/tree-selector.ts", "lines": "129-142",
     "symbol": "TreeList constructor",
     "observation": "filterMode is seeded from initialFilterMode before applyFilter runs, so the first "
                    "render is already filtered."},
    {"file": "packages/coding-agent/src/modes/interactive/components/tree-selector.ts", "lines": "331-420",
     "symbol": "TreeList.applyFilter",
     "observation": "default hides label/context_edit/custom/model_change/thinking_level_change/session_info; "
                    "no-tools adds toolResult removal; user-only keeps user messages; labeled-only keeps "
                    "labelled nodes; all keeps everything except usage entries and text-free assistant "
                    "messages that are not the current leaf."},
    {"file": "packages/coding-agent/src/modes/interactive/components/tree-selector.ts", "lines": "648-664",
     "symbol": "TreeList.getStatusLabels",
     "observation": "Non-default filters append ' [no-tools]', ' [user]', ' [labeled]' or ' [all]'; "
                    "default appends nothing."},
    {"file": "packages/tui/src/components/settings-list.ts", "lines": "284-288",
     "symbol": "SettingsList value cycling",
     "observation": "Enter/Space cycles a value item through its values array in order and reports the "
                    "new value; there is no reset-to-unset entry."},
]

ORACLE_SCENARIOS = [
    ("TF01", "No treeFilterMode configured", "Effective mode is default", ["TF01"], "default"),
    ("TF02", "Explicit treeFilterMode default", "/tree opens in default mode", ["TF02"], "default"),
    ("TF03", "Explicit treeFilterMode no-tools", "/tree opens in no-tools mode", ["TF03"], "no-tools"),
    ("TF04", "Explicit treeFilterMode user-only", "/tree opens in user-only mode", ["TF04"], "user-only"),
    ("TF05", "Explicit treeFilterMode labeled-only", "/tree opens in labeled-only mode", ["TF05"], "labeled-only"),
    ("TF06", "Explicit treeFilterMode all", "/tree opens in all mode", ["TF06"], "all"),
    ("TF07", "Unrecognized configured value", "Loads without error and behaves as default", ["TF07"], "invalid"),
    ("TF08", "Settings picker presentation", "Label, description, current value, option order and reset "
             "semantics match pinned Pi", ["TF08"], "settings"),
    ("TF09", "Persistence", "The chosen mode survives a restart", ["TF09"], "default"),
    ("TF10", "Reset", "Removing the configured value restores default", ["TF10"], "default"),
    ("TF11", "Repeated opens", "A temporary selector filter change does not persist", ["TF11"], "default"),
    ("TF12", "Change applies to the next tree", "The next /tree open uses the new value without a restart",
     ["TF12"], "default"),
    ("TF13", "No unrelated settings mutation", "Other settings survive the write", ["TF13"], "default"),
]


def projections(document):
    result = {}
    for case_id, case in document["cases"].items():
        checkpoints = case["pi"]["checkpoints"]

        def project(name):
            tree = checkpoints[name]["tree"]
            return {"rows": tree["rows"], "count": tree["count"], "tags": tree["tags"],
                    "selected": tree["selected"]}

        result[case_id] = {
            "default": project("default-open"),
            "unset": project("unset-open"),
            "no-tools": project("no-tools-open"),
            "user-only": project("user-only-open"),
            "labeled-only": project("labeled-only-open"),
            "all": project("all-open"),
            "invalid": project("invalid-open"),
            "reset": project("reset-open"),
            "temporary-cycle": project("temporary-cycle"),
        }
    return result


def report_cases_from_document(document):
    return list(document["cases"])


PROJECT_CHECKPOINTS = {
    "trusted-malformed": "trusted-malformed-open",
    "trusted-absent": "trusted-absent-open",
    "untrusted": "untrusted-open",
}


def project_projections(document):
    result = {}
    for case_id, case in document["cases"].items():
        checkpoints = case["pi"]["checkpoints"]
        result[case_id] = {}
        for label, name in PROJECT_CHECKPOINTS.items():
            tree = checkpoints[name]["tree"]
            result[case_id][label] = {"rows": tree["rows"], "count": tree["count"],
                                      "tags": tree["tags"], "selected": tree["selected"]}
    return result


def build_oracle(*documents, project_documents=()):
    merged = {"cases": {}}
    for document in documents:
        merged["cases"].update(document["cases"])
    document = merged
    return {
        "schema": 1,
        "capability": "settings-treefiltermode",
        "piSha": documents[0]["piSha"],
        "values": list(SETTING_VALUES),
        "default": "default",
        "label": "Tree filter mode",
        "description": "Default filter when opening /tree",
        "sources": ORACLE_SOURCES,
        "scenarios": [{"id": case_id, "requirement": requirement, "expected": expected,
                       "oracle": oracle, "fixture": fixture}
                      for case_id, requirement, expected, oracle, fixture in ORACLE_SCENARIOS],
        "projections": projections(document),
        "projectProjections": project_projections({
            "cases": {f"{case_id}": case for document in project_documents
                      for case_id, case in document["cases"].items()}}),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--calibration-input", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--settings-input", type=Path,
                        help="Pinned-Pi settings-selector calibration output directory")
    parser.add_argument("--settings-output", type=Path)
    parser.add_argument("--pisharp-sha")
    parser.add_argument("--oracle-extra-input", type=Path, action="append",
                        help="Additional paired output directory merged into the oracle projections")
    parser.add_argument("--oracle-project-input", type=Path,
                        help="Layered project-precedence paired directory for oracle project projections")
    parser.add_argument("--oracle-output", type=Path,
                        help="Write the pinned-Pi oracle with projections derived from this evidence")
    parser.add_argument("--case", action="append",
                        help="Only extract the named case (repeatable); defaults to the report cases")
    args = parser.parse_args()
    cases = tuple(args.case) if args.case else None
    document = extract_paired(args.input, args.calibration_input, cases, args.pisharp_sha)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n")
    if args.oracle_output is not None:
        oracle_documents = [document]
        for directory in args.oracle_extra_input or []:
            oracle_documents.append(extract_paired(directory, None, None, args.pisharp_sha))
        args.oracle_output.parent.mkdir(parents=True, exist_ok=True)
        project_documents = []
        if args.oracle_project_input is not None:
            project_documents.append(
                extract_paired(args.oracle_project_input, None, None, args.pisharp_sha))
        args.oracle_output.write_text(
            json.dumps(build_oracle(*oracle_documents, project_documents=project_documents),
                       indent=2) + "\n")
        print(f"wrote {args.oracle_output}")
    differences = sum(case["paired"]["semanticDifferences"] for case in document["cases"].values())
    calibration = sum(case.get("calibration", {}).get("semanticDifferences", 0)
                      for case in document["cases"].values())
    print(f"wrote {args.output}: {len(document['cases'])} cases, {differences} paired differences, "
          f"{calibration} calibration differences")
    if args.settings_input is not None:
        settings_document = extract_settings(args.settings_input)
        target = args.settings_output or args.output.with_name("settings-selector.json")
        target.write_text(json.dumps(settings_document, indent=2) + "\n")
        print(f"wrote {target}: settings-selector calibration differences "
              f"{settings_document['calibration']['semanticDifferences']}")


if __name__ == "__main__":
    main()
