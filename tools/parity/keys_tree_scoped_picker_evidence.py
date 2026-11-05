#!/usr/bin/env python3
"""Extract compact keys-tree-scoped-picker checkpoints from paired PTY evidence.

Raw render streams stay in the ignored artifacts tree; this script keeps only the
semantic observations the acceptance verifier compares: tree rows, selection,
fold/filter/search state, scoped-model enabled order and selection, editor text,
clipboard/status markers, persisted settings and appended label entries.
"""
import argparse
import gzip
import json
import re
from pathlib import Path

CASES = (
    "100x30-dark-fullscreen",
    "100x30-dark-regular",
    "140x40-dark-fullscreen",
    "140x40-dark-regular",
)

TREE_TITLES = ("Session Tree",)
KNOWN_LABELS = ("second answer label updated", "second answer label", "first label")
SCOPED_TITLES = ("Model Configuration", "Scoped Models")
BOX_LINE = re.compile(r"^[\s|│┌┐└┘├┤┬┴┼─━═-]+$")
COUNT_LINE = re.compile(r"\((\d+)/(\d+)\)")
SEARCH_LINE = re.compile(r"Type to search:(?:\s+(.*))?")
SCOPED_ROW = re.compile(
    r"^\s*(?P<selected>[>→])?\s*(?P<check>✓)?\s*"
    r"(?P<id>fixture-model|alpha|beta|gamma)\s+\[fixture\]")
SCOPED_COUNT = re.compile(r"all enabled|(\d+)/(\d+) enabled")
UNSAVED = "(unsaved)"

TREE_TOKENS = (
    ("label: second answer label updated", ("L17",)),
    ("label: second answer label", ("L16",)),
    ("label: first label", ("L6",)),
    ("user: first prompt", ("U2",)),
    ("assistant: first answer", ("A3",)),
    ("user: branch question", ("B4",)),
    ("assistant: branch answer", ("B5",)),
    ("user: second prompt", ("C7",)),
    ("assistant: second answer", ("C8",)),
    ("[read: README.md]", ("R10",)),
    ("[compaction: 12k tokens]", ("K11",)),
    ("[note]: custom note text", ("Y12",)),
    ("[bash]: echo fixture-output", ("Z13",)),
    ("[model: beta]", ("M1",)),
    ("[thinking: off]", ("T1", "T15")),
)



def load_assertions(path):
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        return json.load(stream)


def viewport_lines(frame):
    return ["".join(cell.get("chars", " ") for cell in line.get("cells", [])).rstrip()
            for line in frame["state"].get("viewport", [])]


def editor_lines(lines):
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith("─")]
    if len(separators) < 2:
        return []
    return [line.strip() for line in lines[separators[-2] + 1:separators[-1]] if line.strip()]


def clean_line(line):
    return line.strip().strip("|").strip()


def tree_token(line, occurrences):
    for token, keys in TREE_TOKENS:
        if token in line:
            index = occurrences.get(token, 0)
            occurrences[token] = index + 1
            return keys[min(index, len(keys) - 1)]
    return None


def panel_bounds(lines, title):
    for start, line in enumerate(lines):
        if title in line:
            break
    else:
        return None
    borders = [index for index in range(start + 1, len(lines)) if BOX_LINE.match(lines[index])]
    end = (borders[-1] + 3) if borders else len(lines)
    return start, min(end, len(lines))


def tree_state(lines):
    if any("Label (empty to remove):" in line for line in lines):
        prompt = next(index for index, line in enumerate(lines)
                      if "Label (empty to remove):" in line)
        prefill = clean_line(lines[prompt + 1]) if prompt + 1 < len(lines) else ""
        if prefill.startswith("> "):
            prefill = prefill[2:]
        return {"mode": "label-edit", "prefill": prefill}
    bounds = panel_bounds(lines, "Session Tree")
    if bounds is None:
        return None
    start, end = bounds
    rows = []
    selected = None
    folded = []
    count = None
    search = None
    tags = []
    occurrences = {}
    rendered_labels = set()
    for raw in lines[start:end]:
        line = clean_line(raw)
        match = COUNT_LINE.search(line)
        if match:
            count = f"{match.group(1)}/{match.group(2)}"
            tags = re.findall(r"\[(no-tools|user|labeled|all|\+label time)\]", line)
        match = SEARCH_LINE.search(line)
        if match:
            search = (match.group(1) or "").strip()
        for label in sorted(KNOWN_LABELS, key=len, reverse=True):
            if label in line:
                rendered_labels.add(label)
                break
        token = tree_token(line, occurrences)
        if token is not None:
            rows.append(token)
            if "› " in line:
                selected = token
            if "⊞" in line:
                folded.append(token)
    return {"mode": "tree", "rows": rows, "selected": selected, "folded": folded,
            "count": count, "tags": tags, "search": search,
            "renderedLabels": sorted(rendered_labels)}


def scoped_state(lines):
    start = None
    for index, line in enumerate(lines):
        if any(title in line for title in SCOPED_TITLES):
            start = index
            break
    if start is None:
        return None
    borders = [index for index in range(start + 1, len(lines)) if BOX_LINE.match(lines[index])]
    end = (borders[-1] + 3) if borders else len(lines)
    rows = []
    enabled = []
    selected = None
    count = None
    for raw in lines[start:min(end, len(lines))]:
        line = clean_line(raw)
        match = SCOPED_ROW.match(line)
        if match:
            model = match.group("id")
            rows.append(model)
            if match.group("check"):
                enabled.append(model)
            if match.group("selected"):
                selected = model
        match = SCOPED_COUNT.search(line)
        if match:
            count = "all enabled" if match.group(0) == "all enabled" else f"{match.group(1)}/{match.group(2)}"
    text = "\n".join(lines)
    return {"rows": rows, "enabled": enabled, "selected": selected, "count": count,
            "unsaved": UNSAVED in text}


def checkpoint(frame):
    lines = viewport_lines(frame)
    text = "\n".join(lines)
    tree = tree_state(lines)
    scoped = scoped_state(lines)
    editor = editor_lines(lines) if tree is None and scoped is None else []
    if editor[:1] == ["Summarize branch?"]:
        # The prompt body is shared; the footer hints are surface-specific.
        editor = editor[:4]
    return {
        "tree": tree,
        "scoped": scoped,
        "editor": editor,
        "activeScreen": frame["state"].get("activeScreen"),
    }


def requests(assertion):
    result = []
    for request in assertion.get("http", []):
        body = request.get("body") or {}
        result.append(body.get("model") or request.get("method") or "")
    return result


def captured_settings(assertion):
    raw = (assertion.get("capturedAgentFiles") or {}).get("settings.json")
    if not raw:
        return None
    try:
        settings = json.loads(raw)
    except ValueError:
        return None
    return {"shellCommandPrefix": settings.get("shellCommandPrefix"),
            "enabledModels": settings.get("enabledModels")}


def captured_labels(assertion):
    """Read the label append trail from whichever session store the product owns."""
    labels = []
    native = assertion.get("capturedSessionContents") or {}
    if native:
        # PiSharp commits labels to its native session document.
        for raw in native.values():
            try:
                document = json.loads(raw)
            except ValueError:
                continue
            for entry in document.get("Entries") or []:
                kind = entry.get("Type") or entry.get("type")
                if kind != "label":
                    continue
                payload = entry.get("Payload") or entry
                label = {"targetId": payload.get("targetId"), "label": payload.get("label")}
                # One restart can leave both the imported and resumed native documents.
                if label not in labels:
                    labels.append(label)
        return labels
    raw = (assertion.get("capturedAgentFiles") or {}).get("fixtures/tree.jsonl")
    if not raw:
        return []
    for line in raw.splitlines():
        try:
            entry = json.loads(line)
        except ValueError:
            continue
        if entry.get("type") == "label":
            labels.append({"targetId": entry.get("targetId"), "label": entry.get("label")})
    return labels


def clipboard_by_action(path, product):
    if not path.is_file():
        return {}
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        evidence = json.load(stream)
    writes = {}
    for item in evidence.get(product, {}).get("clipboardByFrame", []):
        writes[item["id"]] = [entry["text"] for entry in item.get("writes", [])]
    return writes


def product_evidence(assertion, clipboard):
    checkpoints = {}
    for frame in assertion.get("frames", []):
        checkpoints[frame["id"]] = checkpoint(frame)
    return {
        "checkpoints": checkpoints,
        "clipboard": clipboard,
        "requests": requests(assertion),
        "settings": captured_settings(assertion),
        "labels": captured_labels(assertion),
        "sessionFiles": assertion.get("capturedSessionFiles", []),
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


SEMANTIC_KEYS = ("checkpoints", "requests", "settings", "labels", "clipboard")


def comparison(pi, pisharp, summary):
    compared = {key: (pi[key], pisharp[key]) for key in SEMANTIC_KEYS}
    differences = []
    for key in SEMANTIC_KEYS:
        differences.extend(semantic_differences(pi[key], pisharp[key], f"$.{key}"))
    return {
        "semanticDifferences": len(differences),
        "semanticFirstDifferences": differences[:20],
        "httpMatch": pi["requests"] == pisharp["requests"],
        "scenarioErrors": {name: value["scenarioError"] for name, value in
                           (("pi", pi), ("pisharp", pisharp)) if value["scenarioError"]},
        "runReport": {"match": summary.get("match"),
                      "differences": summary.get("differences"),
                      "rawMatch": summary.get("rawMatch")},
    }


def extract_case(directory, case_id):
    path = directory / f"{case_id}.assertions.json.gz"
    if not path.is_file():
        raise SystemExit(f"missing assertion evidence: {path}")
    evidence = load_assertions(path)
    client_path = directory / f"{case_id}.json.gz"
    pi = product_evidence(evidence["pi"], clipboard_by_action(client_path, "pi"))
    pisharp = product_evidence(evidence["pisharp"], clipboard_by_action(client_path, "pisharp"))
    return evidence, pi, pisharp


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True,
                        help="Directory with <case>.assertions.json.gz and report.json")
    parser.add_argument("--calibration-input", type=Path,
                        help="Same-Pi calibration output directory")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--label", default="paired", help="Evidence label written into the document")
    parser.add_argument("--case", action="append", choices=CASES,
                        help="Only extract the named case (repeatable); defaults to every case")
    args = parser.parse_args()
    cases = tuple(args.case) if args.case else CASES

    report_path = args.input / "report.json"
    report = json.loads(report_path.read_text()) if report_path.is_file() else {"cases": []}
    report_by_case = {case["id"]: case for case in report.get("cases", [])}
    calibration_by_case = {}
    if args.calibration_input:
        calibration_report = json.loads((args.calibration_input / "report.json").read_text())
        calibration_by_case = {case["id"]: case for case in calibration_report.get("cases", [])}

    document = {"capability": "keys-tree-scoped-picker",
                "piSha": report.get("piSha"),
                "pisharpSha": report.get("pisharpSha"),
                "label": args.label,
                "cases": {}}
    for case_id in cases:
        evidence, pi, pisharp = extract_case(args.input, case_id)
        case = {
            "dimensions": evidence["dimensions"], "theme": evidence["theme"], "mode": evidence["mode"],
            "pi": pi, "pisharp": pisharp,
            "paired": comparison(pi, pisharp, report_by_case.get(case_id, {})),
        }
        if args.calibration_input:
            calibration_evidence, calibration_pi, calibration_pisharp = extract_case(
                args.calibration_input, case_id)
            case["calibration"] = comparison(calibration_pi, calibration_pisharp,
                                             calibration_by_case.get(case_id, {}))
            case["calibrationEvidence"] = {
                "dimensions": calibration_evidence["dimensions"],
                "theme": calibration_evidence["theme"],
                "mode": calibration_evidence["mode"],
            }
        document["cases"][case_id] = case
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n")
    total = sum(case["paired"]["semanticDifferences"] for case in document["cases"].values())
    calibration_total = sum(case.get("calibration", {}).get("semanticDifferences", 0)
                            for case in document["cases"].values())
    print(f"wrote {args.output}: {len(document['cases'])} cases, {total} paired differences, "
          f"{calibration_total} calibration differences")


if __name__ == "__main__":
    main()
