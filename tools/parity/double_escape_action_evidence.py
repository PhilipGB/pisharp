#!/usr/bin/env python3
"""Extract compact `doubleEscapeAction` checkpoints from paired PTY evidence.

Raw render streams stay in the ignored artifacts tree. This script keeps the
semantic observations the acceptance verifier compares: which overlay a frame
shows, the tree or fork panel content, the pinned settings row and the persisted
settings document.
"""
import argparse
import gzip
import json
import re
from pathlib import Path

# Fork previews are truncated differently by each product, so the comparison uses the
# pinned metadata line and the selected row instead of the rendered preview text.
FORK_META = re.compile(r"Message (\d+) of (\d+)")
COUNT = re.compile(r"\((\d+)/(\d+)\)")
TAGS = re.compile(r"\[(no-tools|user|labeled|all|\+label time)\]")
SETTING_VALUES = ("tree", "fork", "none")
ERRORS = re.compile(r"Invalid settings|must be an? (string|settings)|Agent error|Unhandled exception")
TREE_TITLE = "Session Tree"
FORK_TITLE = "Fork from"
SETTINGS_TITLE = "Settings · User settings"
SETTINGS_ROW = "Double-escape action"
SETTINGS_DESCRIPTION = "Action when pressing Escape twice with empty editor"
EDITOR_READY = "editor ready"


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


def overlay(lines):
    text = "\n".join(lines)
    if TREE_TITLE in text:
        return "tree"
    if FORK_TITLE in text:
        return "fork"
    if SETTINGS_TITLE in text:
        return "settings"
    if SETTINGS_ROW in text:
        return "settings-values"
    return "none"


def tree_state(lines):
    if not any(TREE_TITLE in line for line in lines):
        return None
    count = None
    tags = []
    selected = None
    for line in lines:
        text = normalize(line).strip()
        match = COUNT.search(text)
        if match and count is None:
            count = f"{match.group(1)}/{match.group(2)}"
            tags = TAGS.findall(text)
        if "\u203a" in text and selected is None:
            selected = text.split("\u203a", 1)[1].strip()
    return {"count": count, "tags": tags, "selected": selected}


MARKERS = ("\u203a", ">")


def _marked(text):
    stripped = text.lstrip(" |")
    return stripped.startswith(MARKERS)


def _preview(text):
    text = text.strip().strip("|").strip()
    for marker in MARKERS:
        if text.startswith(marker):
            text = text[len(marker):].strip()
    if " \u00b7 " in text:
        parts = [part.strip() for part in text.split(" \u00b7 ")]
        # PiSharp renders "<id> \u00b7 <preview> \u00b7 Message n of m".
        if len(parts) >= 2:
            return parts[1]
    return text


def fork_state(lines):
    """Project the fork panel onto its message count, selected index and selected preview.

    The two products draw the list differently (Pi uses a marker line plus a metadata line,
    PiSharp inlines id, preview and metadata), so the projection keeps only what both encode.
    """
    if not any(FORK_TITLE in line for line in lines):
        return None
    normalized = [normalize(line) for line in lines]
    total = None
    selected_index = None
    selected_preview = None
    for index, text in enumerate(normalized):
        match = FORK_META.search(text)
        if not match:
            continue
        total = int(match.group(2))
        previous = [normalized[position] for position in range(max(0, index - 2), index)]
        candidates = [text] + previous
        if any(_marked(candidate) for candidate in candidates):
            selected_index = int(match.group(1))
            marked = next((candidate for candidate in candidates if _marked(candidate)), text)
            selected_preview = _preview(marked)
    return {"total": total, "selectedIndex": selected_index, "selectedPreview": selected_preview}


def settings_state(lines):
    row = None
    value = None
    offered = []
    description = None
    for line in lines:
        text = normalize(line).strip()
        if text.startswith("\u2192"):
            text = text[1:].strip()
        if text.startswith(SETTINGS_ROW):
            row = True
            remainder = text[len(SETTINGS_ROW):].strip()
            value = remainder.split()[0] if remainder else None
        if text == SETTINGS_DESCRIPTION:
            description = True
        if text in SETTING_VALUES and text not in offered:
            offered.append(text)
    if row is None and not offered:
        return None
    return {"row": row, "value": value, "description": description, "values": offered}


def settings_row_value(lines):
    """Value shown on the double-escape row, excluding the value-submenu title."""
    for line in lines:
        text = normalize(line).strip().strip("|").strip()
        if text.startswith("\u2192") or text.startswith(">"):
            text = text[1:].strip()
        if not text.startswith(SETTINGS_ROW) or "\u00b7" in text:
            continue
        remainder = text[len(SETTINGS_ROW):].strip()
        if not remainder:
            return ""
        return re.split(r"\s{2,}", remainder)[0].strip()
    return None


def checkpoint(frame):
    lines = viewport_lines(frame)
    text = "\n".join(lines)
    return {
        "overlay": overlay(lines),
        "tree": tree_state(lines),
        "fork": fork_state(lines),
        "settings": settings_state(lines),
        "settingsRow": None,
        "settingsRow": settings_row_value(lines),
        "crashed": "text.slice is not a function" in text or "uncaughtException" in text,
        "submenu": "Inherit / default" in text,
        "settingsOpen": "Type to search" in text or "Type to filter" in text or
                        any(SETTINGS_ROW in line for line in lines),
        "editorReady": EDITOR_READY in text,
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
        "doubleEscapeAction": document.get("doubleEscapeAction"),
        "doubleEscapeActionPresent": "doubleEscapeAction" in document,
        "quietStartup": document.get("quietStartup"),
        "shellCommandPrefix": document.get("shellCommandPrefix"),
        "theme": document.get("theme"),
        "tuiMode": document.get("tuiMode"),
        "treeFilterMode": document.get("treeFilterMode"),
    }


def persisted_project(assertion):
    raw = (assertion.get("capturedWorkspaceFiles") or {}).get(".pi/settings.json")
    if not raw:
        return None
    try:
        return json.loads(raw)
    except ValueError:
        return None


def product_evidence(assertion):
    return {
        "checkpoints": {frame["id"]: checkpoint(frame) for frame in assertion.get("frames") or []},
        "requests": requests(assertion),
        "settings": persisted_settings(assertion),
        "project": persisted_project(assertion),
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


SEMANTIC_KEYS = ("checkpoints", "requests", "settings", "project")


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


def report_cases(directory):
    report = json.loads((Path(directory) / "report.json").read_text())
    return [case["id"] for case in report.get("cases") or []]


def report_case(directory, case_id):
    path = Path(directory) / "report.json"
    if not path.is_file():
        return {}
    report = json.loads(path.read_text())
    return next((case for case in report.get("cases") or [] if case.get("id") == case_id), {})


def extract_case(directory, case_id):
    path = Path(directory) / f"{case_id}.assertions.json.gz"
    if not path.is_file():
        raise SystemExit(f"missing assertion evidence: {path}")
    evidence = load_assertions(path)
    return evidence, product_evidence(evidence["pi"]), product_evidence(evidence["pisharp"])


def extract_paired(directory, calibration_directory, cases, pisharp_sha):
    if not cases:
        cases = report_cases(directory)
    document = {"schema": 1, "capability": "settings-doubleescapeaction", "label": "paired",
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
    """Pinned-Pi settings-selector journey.

    PiSharp opens a value submenu where Pi cycles in place, so this document is taken from a
    same-Pi calibration run; PiSharp's row, value order, reset and live application are covered by
    TerminalSettingsPickerTests and DoubleEscapeActionPtyTests.
    """
    case_id = report_cases(directory)[0]
    evidence, pi, second = extract_case(directory, case_id)
    report = json.loads((Path(directory) / "report.json").read_text())
    return {
        "schema": 1,
        "capability": "settings-doubleescapeaction",
        "label": "settings-selector",
        "piSha": report.get("piSha"),
        "dimensions": evidence.get("dimensions"),
        "theme": evidence.get("theme"),
        "mode": evidence.get("mode"),
        "pi": pi,
        "calibration": comparison(pi, second, report_case(directory, case_id)),
    }


MALFORMED_SETTINGS_LABELS = ("number", "boolean", "object", "array", "bogus-string", "null")


def malformed_settings_product(assertion):
    frames = {frame["id"]: checkpoint(frame) for frame in assertion.get("frames") or []}
    observed = {}
    for label in MALFORMED_SETTINGS_LABELS:
        opened = frames.get(f"{label}-open-state") or {}
        filtered = frames.get(f"{label}-filter-state") or {}
        activated = frames.get(f"{label}-activate-state") or {}
        observed[label] = {
            "settingsOpens": opened.get("settingsOpen", False),
            "crashesOnOpen": opened.get("crashed", False),
            "rowValue": filtered.get("settingsRow"),
            "crashesOnRow": filtered.get("crashed", False),
            "activatedValue": activated.get("settingsRow") if activated else None,
            "activationSubmenu": activated.get("submenu", False) if activated else None,
        }
    return {"values": observed, "scenarioError": assertion.get("scenarioError"),
            "settings": persisted_settings(assertion)}


def extract_malformed_settings(directory, pisharp_sha=None):
    """DE19 settings half: `/settings` behaviour for values the schema rejects.

    Pinned Pi passes the merged value straight into the settings list and crashes rendering numbers,
    booleans and objects; PiSharp renders the stored text and stays usable. Both sides are frozen so
    the divergence is explicit rather than an unexplained difference.
    """
    case_id = report_cases(directory)[0]
    evidence, pi, pisharp = extract_case(directory, case_id)
    report = json.loads((Path(directory) / "report.json").read_text())
    document = {
        "schema": 1,
        "capability": "settings-doubleescapeaction",
        "label": "malformed-settings",
        "piSha": report.get("piSha"),
        "pisharpSha": pisharp_sha or report.get("pisharpSha"),
        "artifact": (report.get("provenance") or {}).get("pisharp", {}).get("artifact"),
        "dimensions": evidence.get("dimensions"),
        "theme": evidence.get("theme"),
        "mode": evidence.get("mode"),
        "pi": malformed_settings_product(evidence["pi"]),
        "pisharp": malformed_settings_product(evidence["pisharp"]),
    }
    return document


ORACLE_SOURCES = [
    {"file": "packages/coding-agent/src/core/settings-defaults.ts", "lines": "45",
     "symbol": "SETTINGS_DEFAULTS.doubleEscapeAction",
     "observation": "The shared runtime default is the string 'tree'."},
    {"file": "packages/coding-agent/src/core/settings-schema.ts", "lines": "339-344",
     "symbol": "doubleEscapeAction",
     "observation": "Optional union of fork, tree and none, defaulted from SETTINGS_DEFAULTS."},
    {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "1356-1364",
     "symbol": "getDoubleEscapeAction/setDoubleEscapeAction",
     "observation": "The getter returns the merged value unless it is nullish and never validates "
                    "it against the union; the setter writes global settings and saves."},
    {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "65-82",
     "symbol": "deepMergeObjects",
     "observation": "Scopes merge by property presence; a null override replaces lower scopes."},
    {"file": "packages/coding-agent/src/core/settings-manager.ts", "lines": "377-408",
     "symbol": "loadFromStorage/tryLoadFromStorage",
     "observation": "Settings files are parsed and migrated only; values are never validated."},
    {"file": "packages/coding-agent/src/modes/interactive/components/settings-selector.ts", "lines": "586-593",
     "symbol": "double-escape-action item",
     "observation": "Label 'Double-escape action', description 'Action when pressing Escape twice "
                    "with empty editor', values tree, fork, none in that order."},
    {"file": "packages/coding-agent/src/modes/interactive/interactive-mode.ts", "lines": "5077-5079",
     "symbol": "onDoubleEscapeActionChange",
     "observation": "A chosen value is persisted through settingsManager.setDoubleEscapeAction."},
    {"file": "packages/coding-agent/src/modes/interactive/interactive-mode.ts", "lines": "3070-3096",
     "symbol": "defaultEditor.onEscape",
     "observation": "Streaming, running bash, bash-entry mode and a non-empty editor all take "
                    "precedence; 'none' skips the timer; otherwise a second press strictly less than "
                    "500 ms after the first fires and resets the timer, and any other press records "
                    "its own time."},
    {"file": "packages/coding-agent/src/modes/interactive/components/custom-editor.ts", "lines": "100-114",
     "symbol": "handleInput Escape",
     "observation": "With autocomplete active Escape cancels the menu without invoking onEscape, so "
                    "the double-press timer is untouched."},
]

ORACLE_SCENARIOS = [
    ("DE01", "No configured value", "Double Escape opens the session tree", ["DE01"], "tree"),
    ("DE02", "Explicit tree", "Double Escape opens the session tree", ["DE02"], "tree"),
    ("DE03", "Explicit fork", "Double Escape opens the fork selector", ["DE03"], "fork"),
    ("DE04", "Explicit none", "Double Escape opens nothing", ["DE04"], "none"),
    ("DE05", "Timing inside the window", "A second press under 500 ms fires exactly once",
     ["DE05", "DE08"], "tree"),
    ("DE06", "Timing at the boundary", "Exactly 500 ms does not fire and re-arms",
     ["DE06"], "tree"),
    ("DE07", "Timing outside the window", "A later press does not fire and starts a new pair",
     ["DE07"], "tree"),
    ("DE08", "Non-empty editor", "A draft suppresses the action and keeps its text", ["DE09"], "tree"),
    ("DE09", "Bash-entry mode", "Escape clears the draft instead of navigating", ["DE09"], "tree"),
    ("DE10", "Whitespace-only editor", "A trim-empty editor counts as empty", ["DE10"], "tree"),
    ("DE11", "none timer state", "Presses while none neither arm nor reset the timer",
     ["DE11"], "none"),
    ("DE12", "Live setting change", "The action is read on every press", ["DE12"], "tree"),
    ("DE13", "Settings presentation", "Label, description, value order and current value match pinned Pi",
     ["DE13"], "settings"),
    ("DE14", "Persistence", "The chosen value survives a restart", ["DE14"], "settings"),
    ("DE15", "Malformed stored values",
     "Unrecognized values load unchanged and take the fork path; null resolves to tree", ["DE15"], "fork"),
    ("DE16", "Trusted project precedence", "A trusted project value replaces the user value",
     ["DE16"], "fork"),
    ("DE17", "Untrusted project", "An untrusted project value does not affect the user value",
     ["DE17"], "none"),
    ("DE18", "Composition with treeFilterMode",
     "A tree action opens with the configured initial filter", ["DE18"], "user-only"),
]

CHECKPOINTS = {
    "unset": "unset-double-escape",
    "tree": "tree-double-escape",
    "fork": "fork-double-escape",
    "none": "none-double-escape",
    "outside": "outside-second",
    "draft": "draft-double-escape",
    "whitespace": "whitespace-double-escape",
    "bash-entry": "bash-entry-escape",
}

MALFORMED_CHECKPOINTS = {
    "bogus-string": "bogus-string-double-escape",
    "empty-string": "empty-string-double-escape",
    "number": "number-double-escape",
    "boolean": "boolean-double-escape",
    "null": "null-double-escape",
    "object": "object-double-escape",
    "array": "array-double-escape",
}

PROJECT_CHECKPOINTS = {
    "trusted": "trusted-double-escape",
    "untrusted": "untrusted-double-escape",
}


def project(pi, name):
    state = pi["checkpoints"][name]
    return {"overlay": state["overlay"], "tree": state["tree"], "fork": state["fork"]}


def projections(document):
    result = {}
    for case_id, case in document["cases"].items():
        pi = case["pi"]
        result[case_id] = {name: project(pi, frame) for name, frame in CHECKPOINTS.items()}
    return result


def malformed_projections(documents):
    result = {}
    for document in documents:
        for case_id, case in document["cases"].items():
            pi = case["pi"]
            result[case_id] = {name: project(pi, frame) for name, frame in MALFORMED_CHECKPOINTS.items()}
    return result


def layered_projections(documents):
    result = {}
    for document in documents:
        for case_id, case in document["cases"].items():
            pi = case["pi"]
            result[case_id] = {name: project(pi, frame) for name, frame in PROJECT_CHECKPOINTS.items()}
    return result


def build_oracle(*documents, regular_documents=(), malformed_documents=(), project_documents=(),
                 settings_document=None, malformed_settings_document=None):
    merged = {"cases": {}}
    for document in documents:
        merged["cases"].update(document["cases"])
    settings_case = (settings_document or {}).get("pi") or {}
    return {
        "schema": 1,
        "capability": "settings-doubleescapeaction",
        "piSha": documents[0]["piSha"],
        "values": list(SETTING_VALUES),
        "default": "tree",
        "windowMilliseconds": 500,
        "windowComparison": "<",
        "label": "Double-escape action",
        "description": SETTINGS_DESCRIPTION,
        "sources": ORACLE_SOURCES,
        "scenarios": [{"id": case_id, "requirement": requirement, "expected": expected,
                       "oracle": oracle, "fixture": fixture}
                      for case_id, requirement, expected, oracle, fixture in ORACLE_SCENARIOS],
        "projections": projections(merged),
        "regularProjections": {case_id: value
                               for document in regular_documents
                               for case_id, value in projections(document).items()},
        "malformedProjections": malformed_projections(malformed_documents),
        "layeredProjections": layered_projections(project_documents),
        "malformedSettings": malformed_settings_projection(malformed_settings_document),
        "settingsProjection": {
            "row": (settings_case.get("checkpoints") or {}).get("settings-state", {}).get("settings"),
            "fork": (settings_case.get("checkpoints") or {}).get("settings-fork-state", {}).get("settings"),
            "persisted": settings_case.get("settings"),
        },
    }


def malformed_settings_projection(document):
    if not document:
        return {}
    return {"pi": document["pi"]["values"],
            "safety": dict(document["pisharp"]["values"], declaredDivergence=True)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--calibration-input", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--settings-input", type=Path,
                        help="Pinned-Pi settings-selector calibration output directory")
    parser.add_argument("--settings-output", type=Path)
    parser.add_argument("--pisharp-sha")
    parser.add_argument("--oracle-regular-input", type=Path)
    parser.add_argument("--oracle-malformed-input", type=Path)
    parser.add_argument("--oracle-project-input", type=Path, action="append")
    parser.add_argument("--oracle-malformed-settings-input", type=Path)
    parser.add_argument("--malformed-settings-output", type=Path)
    parser.add_argument("--oracle-output", type=Path)
    parser.add_argument("--case", action="append",
                        help="Only extract the named case (repeatable); defaults to the report cases")
    args = parser.parse_args()
    cases = tuple(args.case) if args.case else None
    document = extract_paired(args.input, args.calibration_input, cases, args.pisharp_sha)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n")
    print(f"wrote {args.output}: {len(document['cases'])} cases, "
          f"{sum(case['paired']['semanticDifferences'] for case in document['cases'].values())} paired differences, "
          f"{sum(case.get('calibration', {}).get('semanticDifferences', 0) for case in document['cases'].values())} "
          "calibration differences")
    malformed_settings_document = None
    if args.oracle_malformed_settings_input is not None:
        malformed_settings_document = extract_malformed_settings(
            args.oracle_malformed_settings_input, args.pisharp_sha)
        target = args.malformed_settings_output or args.output.with_name("malformed-settings.json")
        target.write_text(json.dumps(malformed_settings_document, indent=2) + "\n")
        print(f"wrote {target}: malformed-settings observations for "
              f"{len(malformed_settings_document['pi']['values'])} stored values")
    settings_document = None
    if args.settings_input is not None:
        settings_document = extract_settings(args.settings_input)
        target = args.settings_output or args.output.with_name("settings-selector.json")
        target.write_text(json.dumps(settings_document, indent=2) + "\n")
        print(f"wrote {target}: settings-selector calibration differences "
              f"{settings_document['calibration']['semanticDifferences']}")
    if args.oracle_output is not None:
        regular = ([extract_paired(args.oracle_regular_input, None, None, args.pisharp_sha)]
                   if args.oracle_regular_input else [])
        malformed = ([extract_paired(args.oracle_malformed_input, None, None, args.pisharp_sha)]
                     if args.oracle_malformed_input else [])
        project_documents = [extract_paired(directory, None, None, args.pisharp_sha)
                             for directory in args.oracle_project_input or []]
        args.oracle_output.parent.mkdir(parents=True, exist_ok=True)
        args.oracle_output.write_text(json.dumps(
            build_oracle(document, regular_documents=regular, malformed_documents=malformed,
                         project_documents=project_documents, settings_document=settings_document,
                         malformed_settings_document=malformed_settings_document),
            indent=2) + "\n")
        print(f"wrote {args.oracle_output}")


if __name__ == "__main__":
    main()
