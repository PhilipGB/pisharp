#!/usr/bin/env python3
"""Generate the deterministic treeFilterMode PTY fixtures.

Both product CLIs receive the same seeded session tree. Each configured mode is
installed by rewriting the harness-owned settings.json and restarting, so the
observed initial filter comes from startup configuration rather than from a
previous selector session. `tuiMode` is written explicitly because the rewrite
must keep both products in the same buffer mode after the restart.
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

MODES = (
    ("default", "default"),
    ("no-tools", "no-tools"),
    ("user-only", "user-only"),
    ("labeled-only", "labeled-only"),
    ("all", "all"),
)

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
    """Seeded session shared by both products.

    The pinned keys-tree session supplies every entry type the tree distinguishes;
    two trailing bookkeeping entries add session_info and context_edit coverage.
    """
    source = json.loads(SOURCE.read_text())
    session = source["files"]["pi"]["fixtures/tree.jsonl"]
    session += json.dumps({
        "type": "session_info",
        "id": "s0000017",
        "parentId": "L0000016",
        "timestamp": "2026-01-01T12:00:17.000Z",
        "name": "fixture session",
    }) + "\n"
    session += json.dumps({
        "type": "context_edit",
        "id": "e0000018",
        "parentId": "s0000017",
        "timestamp": "2026-01-01T12:00:18.000Z",
        "targetId": "y0000012",
        "replacement": None,
    }) + "\n"
    return session


def settings(tui_mode, mode=None):
    value = dict(BASE_SETTINGS, tuiMode=tui_mode)
    if mode is not None:
        value["treeFilterMode"] = mode
    return value


def write_and_restart(prefix, tui_mode, mode):
    return {
        "id": f"{prefix}-config",
        "writeFiles": {"agent": {"settings.json": settings(tui_mode, mode)}},
        "restart": True,
        "expect": "beta",
    }


def open_tree(prefix, expectation="Session Tree"):
    return [
        {"id": f"{prefix}-open", "send": "/tree\r", "expect": expectation},
        {"id": f"{prefix}-state", "allowStaticFrame": True},
        {"id": f"{prefix}-close", "send": "\u001b", "expect": "beta"},
    ]


def actions(tui_mode):
    steps = [
        {"id": "startup", "expect": "beta"},
        # No treeFilterMode configured: the effective filter is Pi's default.
        {"id": "unset-open", "send": "/tree\r", "expect": "Session Tree"},
        {"id": "unset-state", "allowStaticFrame": True},
        {"id": "unset-close", "send": "\u001b", "expect": "beta"},
    ]
    for prefix, mode in MODES:
        steps.append(write_and_restart(prefix, tui_mode, mode))
        steps.extend(open_tree(prefix))
    # An unrecognized value must resolve to the default filter without error.
    steps.append(write_and_restart("invalid", tui_mode, "bogus"))
    steps.extend(open_tree("invalid"))
    # Removing the key restores the default filter.
    steps.append(write_and_restart("reset", tui_mode, None))
    steps.extend(open_tree("reset"))
    # A manual filter change inside the selector must not persist.
    steps.append(write_and_restart("temporary", tui_mode, "user-only"))
    steps.extend([
        {"id": "temporary-open", "send": "/tree\r", "expect": "Session Tree"},
        {"id": "temporary-state", "allowStaticFrame": True},
        {"id": "temporary-cycle", "send": "\f", "expect": "[labeled]"},
        {"id": "temporary-close", "send": "\u001b", "expect": "beta"},
        {"id": "temporary-reopen", "send": "/tree\r", "expect": "[user]"},
        {"id": "temporary-reopen-state", "allowStaticFrame": True},
        {"id": "temporary-reopen-close", "send": "\u001b", "expect": "beta"},
    ])
    steps.append({"id": "quit", "send": "\u0015/quit\r", "exit": True})
    return steps


def scenario(identifier, tui_mode):
    source = json.loads(SOURCE.read_text())
    files = source["files"]["pi"]
    return {
        "id": identifier,
        "settings": dict(BASE_SETTINGS),
        "dimensions": [{"columns": 100, "rows": 30}, {"columns": 140, "rows": 40}],
        "themes": ["dark"],
        "modes": [tui_mode],
        "terminal": {"keyboardProtocol": "kitty"},
        "files": {
            product: {
                "fixtures/tree.jsonl": session_jsonl(),
                "models.json": files["models.json"],
            }
            for product in ("pi", "pisharp")
        },
        "actions": actions(tui_mode),
        "assertionFrames": [action["id"] for action in actions(tui_mode)
                            if not action.get("exit")],
        "captureAgentFiles": ["settings.json"],
        "arguments": ARGUMENTS,
    }


def settings_scenario():
    """Pinned-Pi settings journey: the selector cycles the five values in source order.

    PiSharp cannot follow the same keystrokes because its settings overlay opens a value
    list instead of cycling, so this fixture is a paired-Pi (calibration) observation.
    """
    source = json.loads(SOURCE.read_text())
    files = source["files"]["pi"]
    steps = [
        {"id": "startup", "expect": "beta"},
        {"id": "settings-open", "send": "/settings\r", "expect": "Auto-compact"},
        {"id": "settings-filter", "send": "tree filter", "expect": "Tree filter mode"},
        {"id": "settings-default", "allowStaticFrame": True},
        {"id": "settings-no-tools", "send": "\r", "expect": "no-tools"},
        {"id": "settings-no-tools-state", "allowStaticFrame": True},
        {"id": "settings-user-only", "send": "\r", "expect": "user-only"},
        {"id": "settings-labeled-only", "send": "\r", "expect": "labeled-only"},
        {"id": "settings-all", "send": "\r", "expect": "all"},
        {"id": "settings-all-state", "allowStaticFrame": True},
        {"id": "settings-close", "send": "\u001b", "expect": "beta"},
        {"id": "tree-open", "send": "/tree\r", "expect": "[all]"},
        {"id": "tree-state", "allowStaticFrame": True},
        {"id": "tree-close", "send": "\u001b", "expect": "beta"},
        {"id": "restart", "restart": True, "expect": "beta"},
        {"id": "tree-restart-open", "send": "/tree\r", "expect": "[all]"},
        {"id": "tree-restart-state", "allowStaticFrame": True},
        {"id": "tree-restart-close", "send": "\u001b", "expect": "beta"},
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    return {
        "id": "terminal-tree-filter-mode-settings",
        "settings": dict(BASE_SETTINGS),
        "dimensions": [{"columns": 100, "rows": 30}],
        "themes": ["dark"],
        "modes": ["fullscreen"],
        "terminal": {"keyboardProtocol": "kitty"},
        "files": {
            product: {
                "fixtures/tree.jsonl": session_jsonl(),
                "models.json": files["models.json"],
            }
            for product in ("pi", "pisharp")
        },
        "actions": steps,
        "assertionFrames": [step["id"] for step in steps if not step.get("exit")],
        "captureAgentFiles": ["settings.json"],
        "arguments": ARGUMENTS,
    }


def project_scenario():
    """Layered settings precedence: a trusted malformed project value must win, then resolve to default.

    The project file is fixture-level (`projectFiles`) because the harness re-prepares the
    workspace on every restart; only the agent trust default and user value change per case.
    """
    source = json.loads(SOURCE.read_text())
    files = source["files"]["pi"]

    def install(prefix, trust):
        return {
            "id": f"{prefix}-config",
            "writeFiles": {
                "agent": {"settings.json": dict(BASE_SETTINGS, tuiMode="fullscreen",
                                                defaultProjectTrust=trust,
                                                treeFilterMode="user-only")},
            },
            "restart": True,
            "expect": "beta",
        }

    steps = [
        {"id": "startup", "expect": "beta"},
        # A trusted project entry replaces the valid user value and then resolves to the default.
        install("trusted-malformed", "always"),
        {"id": "trusted-malformed-open", "send": "/tree\r", "expect": "Session Tree"},
        {"id": "trusted-malformed-state", "allowStaticFrame": True},
        {"id": "trusted-malformed-close", "send": "\u001b", "expect": "beta"},
        # An untrusted project contributes nothing, so the malformed entry cannot win.
        install("untrusted", "never"),
        {"id": "untrusted-open", "send": "/tree\r", "expect": "[user]"},
        {"id": "untrusted-state", "allowStaticFrame": True},
        {"id": "untrusted-close", "send": "\u001b", "expect": "beta"},
        {"id": "quit", "send": "\u0015/quit\r", "exit": True},
    ]
    arguments = {
        "pi": list(ARGUMENTS["pi"]),
        # Trust is decided by defaultProjectTrust here, so no explicit trust override is passed.
        "pisharp": [argument for argument in ARGUMENTS["pisharp"] if argument != "--no-approve"],
    }
    return {
        "id": "terminal-tree-filter-mode-project",
        # The fixture ships a project settings file, so the first launch must already be trusted.
        "settings": dict(BASE_SETTINGS, defaultProjectTrust="always"),
        "projectFiles": {".pi/settings.json": json.dumps({"treeFilterMode": 42})},
        "dimensions": [{"columns": 100, "rows": 30}],
        "themes": ["dark"],
        "modes": ["fullscreen"],
        "terminal": {"keyboardProtocol": "kitty"},
        "files": {
            product: {
                "fixtures/tree.jsonl": session_jsonl(),
                "models.json": files["models.json"],
            }
            for product in ("pi", "pisharp")
        },
        "actions": steps,
        "assertionFrames": [step["id"] for step in steps if not step.get("exit")],
        "captureAgentFiles": ["settings.json"],
        "arguments": arguments,
    }


def main():
    fixtures = {
        "terminal-tree-filter-mode.json": scenario("terminal-tree-filter-mode", "fullscreen"),
        "terminal-tree-filter-mode-regular.json": scenario(
            "terminal-tree-filter-mode-regular", "regular"),
        "terminal-tree-filter-mode-settings.json": settings_scenario(),
        "terminal-tree-filter-mode-project.json": project_scenario(),
    }
    for name, content in fixtures.items():
        (OUTPUT / name).write_text(json.dumps(content, indent=2) + "\n")
        print(f"wrote {OUTPUT / name}")


if __name__ == "__main__":
    main()
