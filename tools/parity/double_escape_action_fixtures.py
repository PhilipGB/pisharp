#!/usr/bin/env python3
"""Generate the deterministic `doubleEscapeAction` PTY fixtures.

Both product CLIs receive the same seeded session tree so the tree and fork
selectors have identical content. Configured values are installed by rewriting
the harness-owned settings.json and restarting, which keeps the observation
about configuration rather than about each product's differing settings UI.
`tuiMode` is written explicitly because the rewrite must keep both products in
the same buffer mode after the restart.
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

ESCAPE = "\u001b[27u"

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
    """Seeded session with three user messages so the fork selector has content."""
    source = json.loads(SOURCE.read_text())
    return source["files"]["pi"]["fixtures/tree.jsonl"]


def settings(tui_mode, action=None, extra=None):
    value = dict(BASE_SETTINGS, tuiMode=tui_mode)
    if action is not None:
        value["doubleEscapeAction"] = action
    if extra:
        value.update(extra)
    return value


def write_and_restart(prefix, tui_mode, action=None, extra=None):
    return {
        "id": f"{prefix}-config",
        "writeFiles": {"agent": {"settings.json": settings(tui_mode, action, extra)}},
        "restart": True,
        "expect": "beta",
    }


def double_escape(prefix, expectation, tui_mode):
    """Two Escapes inside the 500 ms window, then a static observation frame."""
    return [
        {"id": f"{prefix}-double-escape", "send": ESCAPE * 2, "expect": expectation,
         "notExpect": "Fork from" if expectation == "Session Tree" else "Session Tree"},
        {"id": f"{prefix}-state", "allowStaticFrame": True},
    ]


def close_selector(prefix, selector):
    return {"id": f"{prefix}-close", "send": ESCAPE, "expect": "beta", "notExpect": selector}


def files():
    source = json.loads(SOURCE.read_text())
    models = source["files"]["pi"]["models.json"]
    return {
        product: {"fixtures/tree.jsonl": session_jsonl(), "models.json": models}
        for product in ("pi", "pisharp")
    }


def scenario(identifier, tui_mode, steps, dimensions, project_files=None, extra_settings=None,
             arguments=None):
    return {
        "id": identifier,
        "settings": dict(BASE_SETTINGS, tuiMode=tui_mode, defaultProjectTrust="always",
                         **(extra_settings or {})),
        **({"projectFiles": project_files} if project_files else {}),
        "dimensions": dimensions,
        "themes": ["dark"],
        "modes": [tui_mode],
        "terminal": {"keyboardProtocol": "kitty"},
        "files": files(),
        "actions": steps,
        "assertionFrames": [step["id"] for step in steps if not step.get("exit")],
        "captureAgentFiles": ["settings.json"],
        "captureWorkspaceFiles": [".pi/settings.json"] if project_files else [],
        "arguments": arguments or ARGUMENTS,
    }


def main_scenario(tui_mode):
    """Valid values, timing, editor-state priority and the settings row."""
    steps = [
        {"id": "startup", "expect": "beta"},
        # DE01: no configured value behaves like the shared default, tree.
        *double_escape("unset", "Session Tree", tui_mode),
        close_selector("unset", "Session Tree"),
        write_and_restart("tree", tui_mode, "tree"),
        *double_escape("tree", "Session Tree", tui_mode),
        close_selector("tree", "Session Tree"),
        write_and_restart("fork", tui_mode, "fork"),
        *double_escape("fork", "Fork from", tui_mode),
        close_selector("fork", "Fork from"),
        {"id": "fork-editor-usable", "delayMs": 150, "send": "draft after fork", "expect": "draft after fork"},
        write_and_restart("none", tui_mode, "none"),
        # Neither selector may open; the checkpoint records both absences.
        {"id": "none-double-escape", "send": ESCAPE * 2, "expect": "beta",
         "notExpect": "Session Tree"},
        {"id": "none-state", "allowStaticFrame": True},
        {"id": "none-draft", "send": "draft survives none", "expect": "draft survives none"},
        write_and_restart("outside", tui_mode, "tree"),
        # DE07: the second Escape lands well after the window, so nothing opens.
        {"id": "outside-first", "send": ESCAPE, "expect": "beta", "notExpect": "Session Tree"},
        {"id": "outside-second", "delayMs": 900, "send": ESCAPE, "expect": "beta",
         "notExpect": "Session Tree"},
        {"id": "outside-state", "allowStaticFrame": True},
        # DE08: an ordinary draft never dispatches, and the draft survives.
        {"id": "draft-typed", "send": "keep this draft", "expect": "keep this draft"},
        {"id": "draft-double-escape", "send": ESCAPE * 2, "expect": "keep this draft",
         "notExpect": "Session Tree"},
        {"id": "draft-state", "allowStaticFrame": True},
        {"id": "draft-cleared", "delayMs": 150, "send": "\u0015", "expect": "beta"},
        # DE16: a whitespace-only draft counts as empty, so the action fires.
        {"id": "whitespace-typed", "delayMs": 150, "send": "   "},
        *double_escape("whitespace", "Session Tree", tui_mode),
        close_selector("whitespace", "Session Tree"),
        # DE09: Escape in Bash-entry mode clears the draft instead of navigating.
        {"id": "bash-entry-typed", "delayMs": 150, "send": "!echo hi", "expect": "!echo hi"},
        {"id": "bash-entry-escape", "send": ESCAPE, "notExpect": "!echo hi"},
        {"id": "bash-entry-state", "allowStaticFrame": True},
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    return scenario("terminal-double-escape-action", tui_mode, steps,
                    [{"columns": 100, "rows": 30}, {"columns": 140, "rows": 40}])


MALFORMED = (
    ("bogus-string", '"bogus"'),
    ("empty-string", '""'),
    ("number", "42"),
    ("boolean", "false"),
    ("null", "null"),
    ("object", "{}"),
    ("array", "[]"),
)


def malformed_scenario(tui_mode):
    """DE19: pinned Pi does not validate the stored value; the runtime decides."""
    steps = [{"id": "startup", "expect": "beta"}]
    for name, literal in MALFORMED:
        action = json.loads(literal)
        steps.append({
            "id": f"{name}-config",
            "writeFiles": {"agent": {"settings.json": json.dumps(
                dict(BASE_SETTINGS, tuiMode=tui_mode, doubleEscapeAction=action))}},
            "restart": True,
            "expect": "beta",
            # Startup must survive a value the settings schema would reject.
            "notExpect": "Invalid settings",
        })
        steps.append({"id": f"{name}-double-escape", "send": ESCAPE * 2, "expect": "beta",
                      "notExpect": "Invalid settings"})
        steps.append({"id": f"{name}-state", "allowStaticFrame": True})
        steps.append({"id": f"{name}-close-selector", "send": ESCAPE, "expect": "beta"})
    steps.append({"id": "quit", "send": "\u0015/quit\r", "exit": True})
    return scenario("terminal-double-escape-action-malformed", tui_mode, steps,
                    [{"columns": 100, "rows": 30}])


def layered_scenario(identifier, project_action, tui_mode):
    """DE17/DE18: trusted project precedence and untrusted exclusion, in one fixture.

    The project file is fixture-level because the harness re-prepares the workspace on
    every restart; only the agent trust default and the user value change per case.
    """
    def install(prefix, trust):
        return {
            "id": f"{prefix}-config",
            "writeFiles": {"agent": {"settings.json": settings(
                tui_mode, "none", extra={"defaultProjectTrust": trust})}},
            "restart": True,
            "expect": "beta",
        }

    steps = [
        {"id": "startup", "expect": "beta"},
        install("trusted", "always"),
        *double_escape("trusted", "Fork from", tui_mode),
        close_selector("trusted", "Fork from"),
        install("untrusted", "never"),
        {"id": "untrusted-double-escape", "send": ESCAPE * 2, "expect": "beta",
         "notExpect": "Session Tree"},
        {"id": "untrusted-state", "allowStaticFrame": True},
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    arguments = {
        "pi": list(ARGUMENTS["pi"]),
        "pisharp": [argument for argument in ARGUMENTS["pisharp"] if argument != "--no-approve"],
    }
    return scenario(identifier, tui_mode, steps, [{"columns": 100, "rows": 30}],
                    project_files={".pi/settings.json": json.dumps(
                        {"doubleEscapeAction": project_action})},
                    arguments=arguments)


def composition_scenario(tui_mode):
    """DE20: the tree action keeps the PR #52 treeFilterMode initial filter."""
    steps = [
        {"id": "startup", "expect": "beta"},
        write_and_restart("compose", tui_mode, "tree", extra={"treeFilterMode": "user-only"}),
        {"id": "compose-double-escape", "send": ESCAPE * 2, "expect": "[user]"},
        {"id": "compose-state", "allowStaticFrame": True},
        {"id": "compose-close", "send": ESCAPE, "expect": "beta"},
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    return scenario("terminal-double-escape-action-composition", tui_mode, steps,
                    [{"columns": 100, "rows": 30}])


def settings_scenario():
    """Pinned-Pi settings journey: the shared list cycles the three values in order.

    PiSharp opens a value submenu instead of cycling in place, so this fixture is a paired-Pi
    calibration observation of the pinned presentation, live application and persistence. It keeps a
    single settings session because every long fullscreen settings session emits a large render
    stream.
    """
    steps = [
        {"id": "startup", "expect": "beta"},
        # An unset setting behaves like the shared default.
        {"id": "default-double-escape", "send": ESCAPE * 2, "expect": "Session Tree"},
        {"id": "default-state", "allowStaticFrame": True},
        # Wait for silence so the Escape is fully consumed before the next keystrokes arrive.
        {"id": "default-close", "send": ESCAPE, "quiesce": True, "quietMs": 400},
        # The shared list cycles tree -> fork -> none in pinned order.
        # The pause keeps "/settings" from pairing with the preceding Escape as an Alt chord.
        # The pause keeps "/settings" from pairing with the preceding Escape as an Alt chord, and
        # the static frame is allowed because both products may render the list before the send is
        # observed.
        {"id": "settings-open", "delayMs": 300, "allowStaticFrame": True, "send": "/settings\r",
         "expect": "Auto-compact"},
        {"id": "settings-filter", "send": "double-escape", "expect": "Double-escape action"},
        {"id": "settings-state", "allowStaticFrame": True},
        {"id": "settings-fork", "send": "\r", "expect": "fork"},
        {"id": "settings-fork-state", "allowStaticFrame": True},
        {"id": "settings-close", "send": "\u001b", "expect": "beta"},
        # The change applies to the next Escape pair without a restart.
        {"id": "live-double-escape", "send": ESCAPE * 2, "expect": "Fork from"},
        {"id": "live-state", "allowStaticFrame": True},
        {"id": "live-close", "send": ESCAPE, "expect": "beta"},
        # The chosen value survives a restart.
        {"id": "restart", "restart": True, "expect": "beta"},
        {"id": "restart-double-escape", "send": ESCAPE * 2, "expect": "Fork from"},
        {"id": "restart-state", "allowStaticFrame": True},
        {"id": "restart-close", "send": ESCAPE, "expect": "beta"},
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    # Regular mode keeps the render stream bounded; the pinned row, description, value order,
    # live application and persistence are identical in both modes.
    return scenario("terminal-double-escape-action-settings", "regular", steps,
                    [{"columns": 100, "rows": 30}])


MALFORMED_SETTINGS = (
    ("number", 42),
    ("boolean", False),
    ("object", {}),
    ("array", []),
    ("bogus-string", "bogus"),
    ("null", None),
)


def malformed_settings_scenario(tui_mode):
    """DE19 settings half: what `/settings` does with a stored value the schema rejects.

    Pinned Pi passes the merged value straight into the settings list. Strings render; numbers,
    booleans and objects crash the renderer (`text.slice is not a function`) as soon as the row
    becomes visible. Each crashing value therefore gets its own restart, and the activation of a
    renderable malformed value is observed separately.
    """
    def install(prefix, value):
        return {
            "id": f"{prefix}-config",
            "writeFiles": {"agent": {"settings.json": settings(tui_mode, value)}},
            "restart": True,
            "expect": "beta",
        }

    steps = [{"id": "startup", "expect": "beta"}]
    for label, value in MALFORMED_SETTINGS:
        steps.append(install(label, value))
        steps.append({"id": f"{label}-open", "delayMs": 300, "allowStaticFrame": True,
                      "send": "/settings\r", "quiesce": True, "quietMs": 400})
        steps.append({"id": f"{label}-open-state", "allowStaticFrame": True})
        steps.append({"id": f"{label}-filter", "send": "double-escape", "quiesce": True,
                      "quietMs": 400})
        steps.append({"id": f"{label}-filter-state", "allowStaticFrame": True})
        if value is None or isinstance(value, str):
            # Renderable malformed values can also be activated; Pi cycles from index -1.
            steps.append({"id": f"{label}-activate", "send": "\r", "quiesce": True,
                          "quietMs": 400})
            steps.append({"id": f"{label}-activate-state", "allowStaticFrame": True})
    # No quit action: the crashing values end the process, so the harness closes the terminal.
    return scenario("terminal-double-escape-action-malformed-settings", tui_mode, steps,
                    [{"columns": 100, "rows": 30}])


def main():
    fixtures = {
        "terminal-double-escape-action.json": main_scenario("fullscreen"),
        "terminal-double-escape-action-regular.json": main_scenario("regular"),
        "terminal-double-escape-action-malformed.json": malformed_scenario("fullscreen"),
        "terminal-double-escape-action-project.json": layered_scenario(
            "terminal-double-escape-action-project", "fork", "fullscreen"),
        "terminal-double-escape-action-project-malformed.json": layered_scenario(
            "terminal-double-escape-action-project-malformed", 42, "fullscreen"),
        "terminal-double-escape-action-composition.json": composition_scenario("fullscreen"),
        "terminal-double-escape-action-settings.json": settings_scenario(),
        "terminal-double-escape-action-malformed-settings.json": malformed_settings_scenario("regular"),
    }
    for name, content in fixtures.items():
        (OUTPUT / name).write_text(json.dumps(content, indent=2) + "\n")
        print(f"wrote {OUTPUT / name}")


if __name__ == "__main__":
    main()
