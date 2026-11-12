#!/usr/bin/env python3
"""Generate the deterministic `branchSummary.skipPrompt` PTY fixtures.

Both product CLIs receive the same seeded session tree, and every configuration change
rewrites the harness-owned settings.json *and* the pristine seeded session before a
restart. The session is rewritten because tree navigation persists a new head into the
`--session` file; restoring it keeps each case's tree identical.

`tuiMode` is always written because the rewrite must keep both products in the same
buffer mode after the restart.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'tools/parity/terminal/fixtures/terminal-keys-tree-scoped-picker.json'
OUTPUT = ROOT / 'tools/parity/terminal/fixtures'

BASE_SETTINGS = {
    "quietStartup": True,
    "shellCommandPrefix": "preserved",
    "theme": "dark",
}

ESCAPE = "\u001b"

ARGUMENTS = {
    "pi": [
        "--session", "{agent}/fixtures/tree.jsonl", "--offline", "--no-extensions",
        "--no-skills", "--no-prompt-templates", "--no-tools", "--no-context-files",
    ],
    "pisharp": [
        "--session", "{agent}/fixtures/tree.jsonl", "--offline", "--no-extensions",
        "--no-skills", "--no-prompt-templates", "--no-tools", "--no-context-files",
        "--no-approve",
    ],
}


def session_jsonl():
    source = json.loads(SOURCE.read_text())
    return source["files"]["pi"]["fixtures/tree.jsonl"]


def files():
    source = json.loads(SOURCE.read_text())
    models = source["files"]["pi"]["models.json"]
    return {
        product: {"fixtures/tree.jsonl": session_jsonl(), "models.json": models}
        for product in ("pi", "pisharp")
    }


def install(prefix, tui_mode, branch_summary=None, extra=None, parent=None):
    settings = dict(BASE_SETTINGS, tuiMode=tui_mode)
    if extra:
        settings.update(extra)
    if parent is not None:
        settings["branchSummary"] = parent
    elif branch_summary is not None:
        settings["branchSummary"] = branch_summary
    return {
        "id": f"{prefix}-config",
        "writeFiles": {"agent": {"settings.json": settings, "fixtures/tree.jsonl": session_jsonl()}},
        "restart": True,
        "expect": "beta",
    }


def open_tree(prefix):
    return {"id": f"{prefix}-tree-open", "send": "/tree\r", "expect": "Session Tree"}


def search_target(prefix):
    return {"id": f"{prefix}-tree-search", "send": "first answer",
            "expect": "Type to search: first answer"}


def select_with_prompt(prefix):
    """Enter on the selected entry; the default configuration asks about summarization."""
    return [
        {"id": f"{prefix}-tree-selected", "send": "\r", "expect": "Summarize branch?"},
        {"id": f"{prefix}-prompt", "allowStaticFrame": True},
    ]


def choose_no_summary(prefix):
    return {"id": f"{prefix}-navigate", "send": "\r", "expect": "first answer",
            "notExpect": "second prompt"}


def cancel_prompt(prefix):
    """Escape reopens the tree at the same selection, then Escape dismisses it."""
    return [
        {"id": f"{prefix}-prompt-cancel", "send": ESCAPE, "expect": "Session Tree",
         "notExpect": "Summarize branch?"},
        {"id": f"{prefix}-tree-reopened", "allowStaticFrame": True},
        {"id": f"{prefix}-tree-reopened-close", "send": ESCAPE, "expect": "beta"},
    ]


def skip_prompt(prefix):
    """One Enter selects the entry and commits immediately, with no summarization question."""
    return [
        {"id": f"{prefix}-tree-selected", "send": "\r", "expect": "first answer",
         "notExpect": "Summarize branch?"},
        {"id": f"{prefix}-navigate", "allowStaticFrame": True},
    ]


def tree_after(prefix):
    """Reopen the tree after a navigation so the selected head and row set stay observable."""
    return [
        {"id": f"{prefix}-tree-after", "send": "/tree\r", "expect": "Session Tree"},
        {"id": f"{prefix}-tree-after-state", "allowStaticFrame": True},
        {"id": f"{prefix}-tree-after-close", "send": ESCAPE, "expect": "beta"},
    ]


def prompt_path(prefix):
    """The full default path: open, search, prompt, cancel back to the tree."""
    return [open_tree(prefix), search_target(prefix), *select_with_prompt(prefix),
            *cancel_prompt(prefix)]


def skip_path(prefix):
    return [open_tree(prefix), search_target(prefix), *skip_prompt(prefix), *tree_after(prefix)]


def main_scenario(tui_mode):
    """BS01-BS03, prompt cancellation, restart persistence and filter composition."""
    steps = [
        {"id": "startup", "expect": "beta"},
        # BS01: no configured value behaves as the default and asks about summarization.
        *prompt_path("unset"),
        open_tree("unset-reopen"), search_target("unset-reopen"),
        *select_with_prompt("unset-reopen"), choose_no_summary("unset-reopen"),
        *tree_after("unset-reopen"),
        # BS02: explicit false still asks.
        install("false", tui_mode, {"skipPrompt": False}),
        *prompt_path("false"),
        # BS03: explicit true skips the question and commits the selection.
        install("true", tui_mode, {"skipPrompt": True}),
        *skip_path("true"),
        # BS03: the same behaviour must survive a restart with the same configuration.
        install("true-restart", tui_mode, {"skipPrompt": True}),
        *skip_path("true-restart"),
        # Returning to false restores the question without any other change.
        install("false-again", tui_mode, {"skipPrompt": False}),
        *prompt_path("false-again"),
        # A non-default tree filter still composes with the skipped prompt.
        install("filtered", tui_mode, {"skipPrompt": True}, {"treeFilterMode": "all"}),
        {"id": "filtered-tree-open", "send": "/tree\r", "expect": "[all]"},
        search_target("filtered"), *skip_prompt("filtered"),
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    return scenario("terminal-branch-summary-skip-prompt", steps,
                    [{"columns": 100, "rows": 30}, {"columns": 140, "rows": 40}],
                    extra_settings={"tuiMode": tui_mode}, modes=[tui_mode])


MALFORMED = (
    ("malformed-string", '"false"', True),
    ("empty-string", '""', False),
    ("number", "1", True),
    ("zero", "0", False),
    ("object", "{}", True),
    ("array", "[]", True),
    ("null", "null", False),
)

MALFORMED_PARENTS = (
    ("parent-null", "null"),
    ("parent-string", '"bad"'),
    ("parent-number", "42"),
    ("parent-array", "[]"),
)


def malformed_scenario(tui_mode):
    """BS23-BS31: stored values outside the schema, and non-object parents."""
    steps = [{"id": "startup", "expect": "beta"}]
    for name, literal, skips in MALFORMED:
        steps.append(install(name, tui_mode, {"skipPrompt": json.loads(literal)}))
        steps.append({"id": f"{name}-tree-open", "send": "/tree\r", "expect": "Session Tree",
                      "notExpect": "Invalid settings"})
        steps.append(search_target(name))
        steps.extend(skip_prompt(name) if skips else select_with_prompt(name) + cancel_prompt(name))
    for name, literal in MALFORMED_PARENTS:
        steps.append(install(name, tui_mode, parent=json.loads(literal)))
        steps.append({"id": f"{name}-tree-open", "send": "/tree\r", "expect": "Session Tree",
                      "notExpect": "Invalid settings"})
        steps.append(search_target(name))
        steps.extend(select_with_prompt(name))
        steps.extend(cancel_prompt(name))
    steps.append({"id": "quit", "send": "\u0015/quit\r", "exit": True})
    return scenario("terminal-branch-summary-skip-prompt-malformed", steps,
                    [{"columns": 100, "rows": 30}])


def project_scenario(identifier, project_summary, skips_when_trusted):
    """BS04/BS05/BS07-BS09: trusted project precedence, trust exclusion and project shapes."""

    def configure(prefix, trust):
        return install(prefix, "fullscreen", {"skipPrompt": True}, {"defaultProjectTrust": trust})

    steps = [{"id": "startup", "expect": "beta"}]
    for prefix, trust in (("trusted", "always"), ("trusted-restart", "always"),
                          ("untrusted", "never")):
        steps.append(configure(prefix, trust))
        if skips_when_trusted or prefix == "untrusted":
            steps.extend(skip_path(prefix))
        else:
            steps.extend(prompt_path(prefix))
    steps.append({"id": "quit", "send": "\u0015/quit\r", "exit": True})
    arguments = {
        "pi": list(ARGUMENTS["pi"]),
        # Trust comes from defaultProjectTrust, so no explicit trust override is passed.
        "pisharp": [argument for argument in ARGUMENTS["pisharp"] if argument != "--no-approve"],
    }
    return scenario(identifier, steps, [{"columns": 100, "rows": 30}],
                    project_files={".pi/settings.json": json.dumps(
                        {"branchSummary": project_summary})},
                    extra_settings={"defaultProjectTrust": "always"},
                    arguments=arguments)


# Fixture name -> (identifier, project branchSummary, whether the trusted value skips the prompt).
PROJECT_FIXTURES = {
    "terminal-branch-summary-skip-prompt-project.json":
        ("terminal-branch-summary-skip-prompt-project", {"skipPrompt": False}, False),
    "terminal-branch-summary-skip-prompt-project-null.json":
        ("terminal-branch-summary-skip-prompt-project-null", {"skipPrompt": None}, False),
    "terminal-branch-summary-skip-prompt-project-reserve.json":
        ("terminal-branch-summary-skip-prompt-project-reserve", {"reserveTokens": 1234}, True),
    "terminal-branch-summary-skip-prompt-project-malformed.json":
        ("terminal-branch-summary-skip-prompt-project-malformed", {"skipPrompt": 0}, False),
    "terminal-branch-summary-skip-prompt-project-malformed-truthy.json":
        ("terminal-branch-summary-skip-prompt-project-malformed-truthy", {"skipPrompt": "false"}, True),
}


def scenario(identifier, steps, dimensions, project_files=None, extra_settings=None,
             arguments=None, modes=None):
    settings = dict(BASE_SETTINGS, tuiMode="fullscreen")
    settings.update(extra_settings or {})
    return {
        "id": identifier,
        "settings": settings,
        **({"projectFiles": project_files} if project_files else {}),
        "dimensions": dimensions,
        "themes": ["dark"],
        "modes": modes or ["fullscreen"],
        "terminal": {"keyboardProtocol": "kitty"},
        "files": files(),
        "actions": steps,
        "assertionFrames": [step["id"] for step in steps if not step.get("exit")],
        "captureAgentFiles": ["settings.json", "fixtures/tree.jsonl"],
        "captureWorkspaceFiles": [".pi/settings.json"] if project_files else [],
        "arguments": arguments or ARGUMENTS,
    }


def main():
    fixtures = {
        "terminal-branch-summary-skip-prompt.json": main_scenario("fullscreen"),
        "terminal-branch-summary-skip-prompt-regular.json": main_scenario("regular"),
        "terminal-branch-summary-skip-prompt-malformed.json": malformed_scenario("fullscreen"),
    }
    for name, (identifier, project_summary, skips) in PROJECT_FIXTURES.items():
        fixtures[name] = project_scenario(identifier, project_summary, skips)
    for name, content in fixtures.items():
        (OUTPUT / name).write_text(json.dumps(content, indent=2) + "\n")
        print(f"wrote {OUTPUT / name}")


if __name__ == "__main__":
    main()
