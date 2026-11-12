#!/usr/bin/env python3
"""Extract compact `branchSummary.skipPrompt` checkpoints from paired PTY evidence.

Raw render streams stay in the ignored artifacts tree. This script keeps the semantic
observations the acceptance verifier compares: which entries the tree shows and selects,
whether the "Summarize branch?" selector appeared and with which choices, which seeded
messages the transcript shows, the persisted settings, the persisted session graph and
any scenario error. Unrelated terminal chrome is not part of the projection.
"""
import argparse
import gzip
import json
import re
from pathlib import Path

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
PROMPT_TITLE = "Summarize branch?"
PROMPT_CHOICES = ("No summary", "Summarize", "Summarize with custom prompt")
COUNT = re.compile(r"\((\d+)/(\d+)\)")
TAGS = re.compile(r"\[(no-tools|user|labeled|all|\+label time)\]")
SEARCH = re.compile(r"Type to search:(?:\s+(.*))?")
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
                "selected": "\u203a" in prefix,
            })
            break
    return found


def tree_state(lines):
    if not any("Session Tree" in line for line in lines):
        return None
    count = None
    tags = []
    search = None
    summary_rows = []
    for line in lines:
        text = normalize(line).strip()
        match = COUNT.search(text)
        if match and count is None:
            count = f"{match.group(1)}/{match.group(2)}"
            tags = TAGS.findall(text)
        match = SEARCH.search(text)
        if match is not None:
            search = (match.group(1) or "").strip()
        if "branch summary" in text.lower():
            summary_rows.append(text)
    visible = rows(lines)
    return {
        "rows": [row["entry"] for row in visible],
        "selected": next((row["entry"] for row in visible if row["selected"]), None),
        "count": count,
        "tags": tags,
        "search": search,
        "summaryRows": summary_rows,
    }


def prompt_state(lines):
    text = "\n".join(normalize(line) for line in lines)
    if PROMPT_TITLE not in text:
        return None
    return {
        "title": PROMPT_TITLE,
        "choices": [choice for choice in PROMPT_CHOICES if choice in text],
    }


def checkpoint(frame):
    lines = viewport_lines(frame)
    text = "\n".join(lines)
    return {
        "tree": tree_state(lines),
        "prompt": prompt_state(lines),
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
        "branchSummary": document.get("branchSummary"),
        "branchSummaryPresent": "branchSummary" in document,
        "treeFilterMode": document.get("treeFilterMode"),
        "quietStartup": document.get("quietStartup"),
        "shellCommandPrefix": document.get("shellCommandPrefix"),
        "theme": document.get("theme"),
        "tuiMode": document.get("tuiMode"),
    }


def persisted_project(assertion):
    raw = (assertion.get("capturedWorkspaceFiles") or {}).get(".pi/settings.json")
    if raw is None:
        return None
    try:
        return json.loads(raw)
    except ValueError:
        return raw


def persisted_session(assertion):
    raw = (assertion.get("capturedAgentFiles") or {}).get("fixtures/tree.jsonl")
    if not raw:
        return None
    entries = []
    for line in raw.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            entries.append(json.loads(line))
        except ValueError:
            return {"unparsable": line}
    messages = [{"type": entry.get("type"), "id": entry.get("id")} for entry in entries]
    summary = [entry for entry in entries if entry.get("type") == "branch_summary"]
    return {
        "types": [entry.get("type") for entry in entries],
        "ids": [entry.get("id") for entry in entries],
        "lastEntryId": entries[-1].get("id") if entries else None,
        "branchSummaryIds": [entry.get("id") for entry in summary],
        "messageTexts": [
            {"role": entry.get("message", {}).get("role"), "text": "".join(
                part.get("text", "") for part in entry.get("message", {}).get("content") or []
                if isinstance(part, dict))}
            for entry in entries if entry.get("type") == "message"
        ],
        "entries": messages,
    }


def product_evidence(assertion):
    return {
        "checkpoints": {frame["id"]: checkpoint(frame) for frame in assertion.get("frames") or []},
        "requests": requests(assertion),
        "settings": persisted_settings(assertion),
        "project": persisted_project(assertion),
        "session": persisted_session(assertion),
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


SEMANTIC_KEYS = ("checkpoints", "requests", "settings", "project", "session")


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
    document = {"schema": 1, "capability": "settings-branchsummary-skipprompt", "label": "paired",
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--calibration-input", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--pisharp-sha")
    args = parser.parse_args()
    document = extract_paired(args.input, args.calibration_input, None, args.pisharp_sha)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n")
    differences = sum(case["paired"]["semanticDifferences"] for case in document["cases"].values())
    calibration = sum(case.get("calibration", {}).get("semanticDifferences", 0)
                      for case in document["cases"].values())
    print(f"wrote {args.output}: {len(document['cases'])} cases, {differences} paired differences, "
          f"{calibration} calibration differences")
    for case_id, case in document["cases"].items():
        for difference in case["paired"]["semanticFirstDifferences"][:5]:
            print(f"  {case_id} {difference['path']}: pi={difference['pi']!r} "
                  f"pisharp={difference['pisharp']!r}")


if __name__ == "__main__":
    main()
