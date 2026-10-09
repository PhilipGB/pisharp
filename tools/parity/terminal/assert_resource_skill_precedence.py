"""Assert resource-scope behavior from a paired current-Pi terminal capture."""
import argparse
import gzip
import json
import re
from pathlib import Path


EXPECTED_SKILLS = ("project-only", "shared", "user-only", "temp-only", "unsafe&skill")
PROJECT_SKILL = "/tmp/pisharp-terminal-fixture-workspace/resource-project/.pi/custom-skills/shared/SKILL.md"
USER_SKILL = "/tmp/pisharp-terminal-fixture-agent/custom-skills/shared/SKILL.md"
TEMP_SKILL = "/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/shared/SKILL.md"
UNSAFE_SKILL = "/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/unsafe&skill/SKILL.md"


def frame_text(product, frame_id):
    frame = next((item for item in product.get("frames", []) if item.get("id") == frame_id), None)
    if frame is None:
        raise AssertionError(f"missing terminal checkpoint {frame_id}")
    lines = frame["state"]["buffers"]["alternate"]["lines"]
    return "\n".join("".join(cell.get("chars", " ") for cell in line.get("cells", [])).rstrip()
                     for line in lines)


def chat_request(product):
    requests = [request for request in product.get("http", [])
                if request.get("method") == "POST" and request.get("path") == "/v1/chat/completions"]
    if len(requests) != 1:
        raise AssertionError(f"expected one chat request, found {len(requests)}")
    return requests[0]


def user_message(request):
    messages = request.get("body", {}).get("messages", [])
    users = [message.get("content") for message in messages if message.get("role") == "user"]
    if len(users) != 1:
        raise AssertionError(f"expected one user message, found {len(users)}")
    content = users[0]
    if isinstance(content, list):
        return "\n".join(item.get("text", "") for item in content if item.get("type") == "text")
    return content


def verify(paired_directory, calibration_directory):
    paired_directory = Path(paired_directory)
    calibration_directory = Path(calibration_directory)
    paired_report = json.loads((paired_directory / "report.json").read_text())
    calibration_report = json.loads((calibration_directory / "report.json").read_text())
    paired = json.loads(gzip.open(paired_directory / "120x40-dark-fullscreen.json.gz", "rt").read())
    if not calibration_report.get("match") or not calibration_report["cases"][0].get("terminalMatch"):
        raise AssertionError("same-Pi calibration did not match after documented transient normalization")

    outcomes = {}
    messages = {}
    requests = {}
    for label in ("pi", "pisharp"):
        product = paired[label]
        if product.get("scenarioError"):
            raise AssertionError(f"{label} scenario failed: {product['scenarioError']}")

        completion = frame_text(product, "loaded-resource-slash-commands")
        absent = [name for name in EXPECTED_SKILLS if f"skill:{name}" not in completion]
        if absent:
            raise AssertionError(f"{label} did not expose expected skill commands: {absent}")

        diagnostics = re.sub(r"\s+", "", frame_text(product, "startup"))
        for path in (PROJECT_SKILL, USER_SKILL, TEMP_SKILL, UNSAFE_SKILL):
            if path.replace(" ", "") not in diagnostics:
                raise AssertionError(f"{label} startup diagnostics omit {path}")
        if "collision" not in diagnostics or "invalidcharacters" not in diagnostics:
            raise AssertionError(f"{label} startup diagnostics omit collision or invalid-name warnings")

        requests[label] = chat_request(product)
        messages[label] = user_message(requests[label])
        if "PROJECT-SHARED-BODY" not in messages[label] or "USER-SHARED-BODY" in messages[label]:
            raise AssertionError(f"{label} invoked a non-winning shared skill")
        outcomes[label] = dict(skillCommands=list(EXPECTED_SKILLS),
                               projectScopeWins=True,
                               invalidNameRemainsAvailable=True,
                               collisionAndNameWarningsVisible=True)

    if messages["pi"] != messages["pisharp"]:
        raise AssertionError("Pi and PiSharp sent different expanded skill prompts")
    request_routes = [[(request.get("method"), request.get("path")) for request in product.get("http", [])]
                      for product in (paired["pi"], paired["pisharp"])]
    if request_routes[0] != request_routes[1]:
        raise AssertionError("Pi and PiSharp sent different HTTP routes or request counts")

    report_case = paired_report["cases"][0]
    return dict(fixture=paired_report["fixture"], piSha=paired_report["piSha"],
                pisharpSha=paired_report["pisharpSha"], semanticMatch=True,
                samePiCalibration=True, outcomes=outcomes, expandedRequestMatch=True,
                requestCount=1, requestRoutesMatch=True,
                fullHttpMatch=report_case.get("httpMatch"),
                fullTerminalMatch=report_case.get("terminalMatch"),
                fullTerminalRenderDifferences=report_case.get("renderDifferences"),
                rawMatch=report_case.get("rawMatch"), rawDifference=report_case.get("rawDifference"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paired_directory", type=Path)
    parser.add_argument("calibration_directory", type=Path)
    args = parser.parse_args()
    print(json.dumps(verify(args.paired_directory, args.calibration_directory), indent=2))


if __name__ == "__main__":
    main()
