"""Assert current-Pi skill prompt and expansion behavior from paired PTY captures."""
import argparse
import gzip
import json
import re
from pathlib import Path


HINTS = {
    "read": "Use the read tool to load a skill's file",
    "bash": "Use bash to load a skill's file",
    "indirect": "Load a skill's file when the task matches its description.",
    "none": None,
}


def text_content(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(item.get("text", "") for item in content
                       if isinstance(item, dict) and item.get("type") == "text")
    return ""


def chat_requests(product):
    return [request for request in product.get("http", [])
            if request.get("method") == "POST" and request.get("path", "").endswith("/chat/completions")]


def request_system_prompt(request):
    messages = [message for message in request.get("body", {}).get("messages", [])
                if message.get("role") == "system"]
    if not messages:
        raise AssertionError("provider request has no system message")
    return text_content(messages[-1].get("content"))


def skill_block(prompt):
    match = re.search(r"<skills>.*?</skills>", prompt, re.DOTALL)
    return match.group(0) if match else None


def user_skill_messages(request):
    messages = []
    for message in request.get("body", {}).get("messages", []):
        if message.get("role") != "user":
            continue
        text = text_content(message.get("content"))
        if text.startswith("<skill name="):
            messages.append(text)
    return messages


def verify_mode(mode, paired_root, calibration_report_path):
    paired_root = Path(paired_root)
    report = json.loads((paired_root / "report.json").read_text())
    calibration = json.loads(Path(calibration_report_path).read_text())
    case = report["cases"][0]
    calibration_case = calibration["cases"][0]
    if not calibration.get("match") or not calibration_case.get("terminalMatch"):
        raise AssertionError("same-Pi calibration did not match")

    with gzip.open(paired_root / case["evidence"], "rt", encoding="utf-8") as stream:
        capture = json.load(stream)
    products = {name: capture[name] for name in ("pi", "pisharp")}
    requests = {name: chat_requests(product) for name, product in products.items()}
    if any(product.get("scenarioError") for product in products.values()):
        raise AssertionError({name: product.get("scenarioError") for name, product in products.items()})
    if len(requests["pi"]) != len(requests["pisharp"]) or not requests["pi"]:
        raise AssertionError("Pi and PiSharp provider request counts differ or are empty")

    if mode == "reload":
        if len(requests["pi"]) != 2:
            raise AssertionError(f"expected two reload requests, found {len(requests['pi'])}")
        prompt_pairs = list(zip(
            [request_system_prompt(request) for request in requests["pi"]],
            [request_system_prompt(request) for request in requests["pisharp"]],
            strict=True))
        blocks = []
        for index, pair in enumerate(prompt_pairs):
            left, right = (skill_block(prompt) for prompt in pair)
            if left is None or left != right:
                raise AssertionError(f"Pi and PiSharp skill advertisement differs on request {index + 1}")
            blocks.append(left)
        if "<name>initial-legacy-skill</name>" not in blocks[0] or "<name>reloaded-legacy-skill</name>" in blocks[0]:
            raise AssertionError("initial request did not advertise only the initial legacy skill")
        if "<name>reloaded-legacy-skill</name>" not in blocks[1] or "<name>initial-legacy-skill</name>" in blocks[1]:
            raise AssertionError("post-reload request did not replace the advertised skill metadata")
        pi_expanded = user_skill_messages(requests["pi"][-1])
        pisharp_expanded = user_skill_messages(requests["pisharp"][-1])
        if len(pi_expanded) != 2 or pi_expanded != pisharp_expanded:
            raise AssertionError("reload request did not preserve both expanded skill messages")
        for name, marker in (("initial-legacy-skill", "Initial legacy skill body marker."),
                             ("reloaded-legacy-skill", "Reloaded legacy skill body marker.")):
            message = next((item for item in pi_expanded if f'name="{name}"' in item), "")
            if marker not in message or not message.endswith("\n\n" +
                    ("first-argument" if name == "initial-legacy-skill" else "second-argument")):
                raise AssertionError(f"{name} expansion body or trimmed argument differs")
        behavior = dict(mode=mode, advertisementMatchedPerRequest=True,
                        advertisementUpdatedAfterReload=True, expandedMessagesMatched=True,
                        chatRequestCount=2)
    else:
        prompt_lists = {name: [request_system_prompt(request) for request in product_requests]
                        for name, product_requests in requests.items()}
        blocks = {name: [skill_block(prompt) for prompt in prompts]
                  for name, prompts in prompt_lists.items()}
        if mode == "none":
            if any(block is not None for product_blocks in blocks.values() for block in product_blocks):
                raise AssertionError("a skill advertisement appeared without a reader")
        else:
            if any(block is None for product_blocks in blocks.values() for block in product_blocks):
                raise AssertionError("skill advertisement is missing from a provider request")
            if blocks["pi"] != blocks["pisharp"] or len(set(blocks["pi"])) != 1:
                raise AssertionError("Pi and PiSharp skill advertisements differ between requests")
            hint = HINTS[mode]
            if any(hint not in block for product_blocks in blocks.values() for block in product_blocks):
                raise AssertionError(f"the skill reader hint does not match mode {mode}")

        pi_expanded = user_skill_messages(requests["pi"][-1])
        pisharp_expanded = user_skill_messages(requests["pisharp"][-1])
        if not pi_expanded or pi_expanded != pisharp_expanded:
            raise AssertionError("Pi and PiSharp expanded skill messages differ")
        expected_names = ["shared", "project-only", "user-only", "temp-only", "unsafe&skill"]
        if "hidden-" in report["fixture"]:
            expected_names = ["shared"]
            argument = "argument-marker"
        else:
            argument = "argument<&> marker"
        actual_names = [re.search(r'^<skill name="([^"]+)"', text).group(1) for text in pi_expanded]
        if actual_names != expected_names:
            raise AssertionError(f"expanded skills are {actual_names}, expected {expected_names}")
        expansion_metadata = {
            "shared": ("/tmp/pisharp-terminal-fixture-workspace/resource-project/.pi/custom-skills/shared/SKILL.md",
                       "/tmp/pisharp-terminal-fixture-workspace/resource-project/.pi/custom-skills/shared",
                       "PROJECT-SHARED-BODY"),
            "project-only": ("/tmp/pisharp-terminal-fixture-workspace/resource-project/.pi/custom-skills/project-only/SKILL.md",
                             "/tmp/pisharp-terminal-fixture-workspace/resource-project/.pi/custom-skills/project-only",
                             "PROJECT-ONLY-BODY"),
            "user-only": ("/tmp/pisharp-terminal-fixture-agent/custom-skills/user-only/SKILL.md",
                          "/tmp/pisharp-terminal-fixture-agent/custom-skills/user-only", "USER-ONLY-BODY"),
            "temp-only": ("/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/temp-only/SKILL.md",
                          "/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/temp-only",
                          "TEMP-ONLY-BODY"),
            "unsafe&skill": (
                "/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/unsafe&skill/SKILL.md",
                "/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/unsafe&skill",
                "UNSAFE-BODY"),
        }
        for name, text in zip(actual_names, pi_expanded, strict=True):
            path, directory, body = expansion_metadata[name]
            expected = (f'<skill name="{name}" location="{path}">\n'
                        f"References are relative to {directory}.\n\n{body}\n</skill>\n\n"
                        + argument)
            if text != expected:
                raise AssertionError(f"{name} expansion envelope, body whitespace, path or trimmed argument differs")
        if mode == "read":
            escaped = ("<name>unsafe&amp;skill</name>",
                       "<description>Unsafe &lt;skill&gt; &amp; description</description>",
                       "<location>/tmp/pisharp-terminal-fixture-workspace/resource-project/temporary-skills/"
                       "unsafe&amp;skill/SKILL.md</location>")
            if any(value not in blocks["pi"][0] for value in escaped):
                raise AssertionError("XML-sensitive skill metadata was not escaped in the advertisement")
        behavior = dict(mode=mode, advertisementMatched=(mode == "none" or blocks["pi"] == blocks["pisharp"]),
                        readerHint=HINTS[mode], expandedMessagesMatched=True,
                        expandedSkills=actual_names, chatRequestCount=len(requests["pi"]))

    return dict(piSha=report["piSha"], pisharpSha=report["pisharpSha"], fixture=report["fixture"],
                behavior=behavior, samePiCalibration=True,
                fullTerminalMatch=case["terminalMatch"], fullHttpMatch=case["httpMatch"],
                rawMatch=case["rawMatch"], renderDifferences=case["renderDifferences"],
                controlBoundaryDifferenceCount=case["controlBoundaryDifferenceCount"],
                scenarioErrors=case["scenarioErrors"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=["read", "bash", "indirect", "none", "reload"], required=True)
    parser.add_argument("--paired", type=Path, required=True)
    parser.add_argument("--calibration-report", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = verify_mode(args.mode, args.paired, args.calibration_report)
    rendered = json.dumps(result, indent=2) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered)
    print(rendered, end="")


if __name__ == "__main__":
    main()
