"""Pinned-process scenarios for RPC session creation, navigation and recovery."""

from __future__ import annotations

import json
from dataclasses import replace

from process_oracle import (
    _ABSENT,
    _r10_compaction_fixture,
    FixtureProviderStep,
    InputRun,
    _R7_BRANCHES,
    _R7_LINEAR,
    _line,
    _r7_fixture,
    _r7_message,
)


def _sequence(*requests: tuple[str, bytes], capture_session_files: bool = True,
              **flags: object) -> InputRun:
    chunks = tuple((payload, 0.0) for _, payload in requests) + ((b"", 0.0),)
    waits = tuple((index, f'"id":"{request_id}"', 1, 20.0)
                  for index, (request_id, _) in enumerate(requests))
    return InputRun(chunks, stdout_waits=waits, capture_session_files=capture_session_files, **flags)


_R12_NEW_EMPTY = _sequence(
    ("new", _line("new", "new_session")),
    ("state", _line("state", "get_state")),
    ("entries", _line("entries", "get_entries")),
    ("messages", _line("messages", "get_messages")),
    expected_response_ids=("new", "state", "entries", "messages"),
    expected_response_commands=("new_session", "get_state", "get_entries", "get_messages"),
    expected_successes=(True, True, True, True),
    expected_data_presence=(True, True, True, True),
    preserve_response_data_commands=("new_session", "get_state", "get_entries", "get_messages"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    session_fixture_jsonl=_R7_LINEAR,
    session_fixture_name="rpc-r7-session.jsonl",
    bind_fixture_working_directory=True,
    omit_no_tools=True,
)


_R12_PARENT_SESSION = _r7_fixture((
    _r7_message("r12-parent-user", None, "user", "parent session message", minute=1),
    _r7_message("r12-parent-assistant", "r12-parent-user", "assistant", "parent session answer", minute=2),
), session_id="r12-parent-session").replace('"cwd":"/tmp"', '"cwd":"__RPC_WORKING_DIRECTORY__"')


def _parent_session_run(value: object, run_name: str) -> InputRun:
    fixture_files = (("r12-parent.jsonl", _R12_PARENT_SESSION),)
    chunks = (
        (_line("new", "new_session", parentSession=value), 0.0),
        (_line("state", "get_state"), 0.0),
        (b"", 0.0),
    )
    return InputRun(
        chunks,
        expected_response_ids=("new", "state"),
        expected_response_commands=("new_session", "get_state"),
        expected_successes=(True, True),
        expected_data_presence=(True, True),
        stdout_waits=((0, '"id":"new"', 1, 20.0), (1, '"id":"state"', 1, 20.0)),
        response_only=True,
        compare_provider_requests=True,
        preserve_response_data_commands=("new_session",),
        preserve_session_identity=True,
        session_fixture_jsonl=_R7_LINEAR,
        bind_fixture_working_directory=True,
        fixture_files=fixture_files,
        pisharp_input_replacements=(("__R12_PARENT__", "r12-parent.jsonl"),),
        pi_input_replacements=(("__R12_PARENT__", "r12-parent.jsonl"),),
        omit_no_tools=True,
    )


_R12_PARENT_VALID = _parent_session_run("__R12_PARENT__", "valid")


def _parent_session_variants() -> tuple[InputRun, ...]:
    runs = [_R12_PARENT_VALID]
    for label, value in (("null", None), ("empty", ""), ("false", False), ("zero", 0),
                         ("number", 7), ("object", {"source": "rpc"}), ("array", [])):
        run = _parent_session_run(value, label)
        runs.append(run)
    return tuple(runs)


_R12_NATIVE_ENTRIES = (
    {"type": "model_change", "id": "r12-native-model", "parentId": None,
     "timestamp": "2025-02-01T00:00:01.000Z", "provider": "fixture", "modelId": "fixture-model"},
    {"type": "thinking_level_change", "id": "r12-native-thinking", "parentId": "r12-native-model",
     "timestamp": "2025-02-01T00:00:02.000Z", "thinkingLevel": "off"},
    {"type": "session_info", "id": "r12-native-name", "parentId": "r12-native-thinking",
     "timestamp": "2025-02-01T00:00:03.000Z", "name": "r12 native target"},
)
_R12_NATIVE_JSONL = _r7_fixture(_R12_NATIVE_ENTRIES, session_id="r12-native-session").replace(
    '"cwd":"/tmp"', '"cwd":"__RPC_WORKING_DIRECTORY__"')
_R12_NATIVE_DOCUMENT = json.dumps({
    "Version": 2,
    "Id": "r12-native-session",
    "WorkingDirectory": "__RPC_WORKING_DIRECTORY__",
    "Model": "fixture-model",
    "Endpoint": None,
    "Name": "r12 native target",
    "HeadId": "r12-name",
    "Entries": [
        {"Id": "r12-model", "ParentId": None, "Type": "model_change",
         "Payload": {"model": "fixture-model", "provider": "fixture", "endpoint": None},
         "Timestamp": "2025-02-01T00:00:01.000Z"},
        {"Id": "r12-thinking", "ParentId": "r12-model", "Type": "thinking_level_change",
         "Payload": {"thinkingLevel": "off"}, "Timestamp": "2025-02-01T00:00:02.000Z"},
        {"Id": "r12-name", "ParentId": "r12-thinking", "Type": "session_info",
         "Payload": {"name": "r12 native target"}, "Timestamp": "2025-02-01T00:00:03.000Z"},
    ],
    "Provider": "fixture",
    "PiHeader": {
        "type": "session", "version": 3, "id": "r12-native-session",
        "timestamp": "2025-02-01T00:00:00.000Z", "cwd": "__RPC_WORKING_DIRECTORY__",
        "provider": "fixture", "modelId": "fixture-model", "thinkingLevel": "off",
    },
    "ParentSessionPath": None,
}, ensure_ascii=False, separators=(",", ":")) + "\n"


_R12_SWITCH_NATIVE = InputRun(
    (
        (_line("switch", "switch_session", sessionPath="__R12_TARGET__"), 0.0),
        (_line("state", "get_state"), 0.0),
        (_line("entries", "get_entries"), 0.0),
        (_line("tree", "get_tree"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("switch", "state", "entries", "tree"),
    expected_response_commands=("switch_session", "get_state", "get_entries", "get_tree"),
    expected_successes=(True, True, True, True),
    expected_data_presence=(True, True, True, True),
    stdout_waits=((0, '"id":"switch"', 1, 20.0), (1, '"id":"state"', 1, 20.0),
                  (2, '"id":"entries"', 1, 20.0), (3, '"id":"tree"', 1, 20.0)),
    preserve_response_data_commands=("switch_session", "get_state", "get_entries", "get_tree"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    # Compare the full RPC state/entries/tree. Keep both dialect-specific target files in raw captures.
    compare_session_files=False,
    session_fixture_jsonl=_R7_LINEAR,
    bind_fixture_working_directory=True,
    omit_no_tools=True,
    pi_fixture_files=(("r12-native-target.jsonl", _R12_NATIVE_JSONL),),
    pisharp_fixture_files=(("r12-native-target.session.json", _R12_NATIVE_DOCUMENT),),
    pi_input_replacements=(("__R12_TARGET__", "r12-native-target.jsonl"),),
    pisharp_input_replacements=(("__R12_TARGET__", "r12-native-target.session.json"),),
)


_R12_SWITCH_PI_JSONL = InputRun(
    (
        (_line("switch", "switch_session", sessionPath="r12-branch-target.jsonl"), 0.0),
        (_line("state", "get_state"), 0.0),
        (_line("entries", "get_entries"), 0.0),
        (_line("tree", "get_tree"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("switch", "state", "entries", "tree"),
    expected_response_commands=("switch_session", "get_state", "get_entries", "get_tree"),
    expected_successes=(True, True, True, True),
    expected_data_presence=(True, True, True, True),
    stdout_waits=((0, '"id":"switch"', 1, 20.0), (1, '"id":"state"', 1, 20.0),
                  (2, '"id":"entries"', 1, 20.0), (3, '"id":"tree"', 1, 20.0)),
    preserve_response_data_commands=("switch_session", "get_state", "get_entries", "get_tree"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    session_fixture_jsonl=_R7_LINEAR,
    bind_fixture_working_directory=True,
    omit_no_tools=True,
    fixture_files=(("r12-branch-target.jsonl",
                    _R7_BRANCHES.replace('"cwd":"/tmp"', '"cwd":"__RPC_WORKING_DIRECTORY__"')),
                   ),
)


_R12_MISSING_OR_MALFORMED = InputRun(
    (
        (_line("missing", "switch_session", sessionPath="r12-not-found.jsonl"), 0.0),
        (_line("state-1", "get_state"), 0.0),
        (_line("entries-1", "get_entries"), 0.0),
        (_line("malformed", "switch_session", sessionPath="r12-malformed.jsonl"), 0.0),
        (_line("state-2", "get_state"), 0.0),
        (_line("entries-2", "get_entries"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("missing", "state-1", "entries-1", "malformed", "state-2", "entries-2"),
    expected_response_commands=("switch_session", "get_state", "get_entries", "switch_session", "get_state", "get_entries"),
    expected_successes=(True, True, True, False, True, True),
    expected_data_presence=(True, True, True, False, True, True),
    expected_error_presence=(False, False, False, True, False, False),
    stdout_waits=tuple((index, f'"id":"{request_id}"', 1, 20.0) for index, request_id in enumerate(
        ("missing", "state-1", "entries-1", "malformed", "state-2", "entries-2"))),
    preserve_response_data_commands=("get_state", "get_entries"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    session_fixture_jsonl=_R7_LINEAR,
    bind_fixture_working_directory=True,
    omit_no_tools=True,
    fixture_files=(("r12-malformed.jsonl", "{\"type\":\"session\",\"id\":\n"),),
)


_R12_FORK = InputRun(
    (
        (_line("fork", "fork", entryId="r7-u3"), 0.0),
        (_line("state", "get_state"), 0.0),
        (_line("entries", "get_entries"), 0.0),
        (_line("tree", "get_tree"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("fork", "state", "entries", "tree"),
    expected_response_commands=("fork", "get_state", "get_entries", "get_tree"),
    expected_successes=(True, True, True, True),
    expected_data_presence=(True, True, True, True),
    response_data_expectations=(("fork", "text", "selected branch question"), ("fork", "cancelled", False)),
    stdout_waits=((0, '"id":"fork"', 1, 20.0), (1, '"id":"state"', 1, 20.0),
                  (2, '"id":"entries"', 1, 20.0), (3, '"id":"tree"', 1, 20.0)),
    preserve_response_data_commands=("fork", "get_state", "get_entries", "get_tree"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    session_fixture_jsonl=_R7_BRANCHES,
    bind_fixture_working_directory=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    omit_no_tools=True,
)


_R12_CLONE = InputRun(
    (
        (_line("clone", "clone"), 0.0),
        (_line("state", "get_state"), 0.0),
        (_line("entries", "get_entries"), 0.0),
        (_line("tree", "get_tree"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("clone", "state", "entries", "tree"),
    expected_response_commands=("clone", "get_state", "get_entries", "get_tree"),
    expected_successes=(True, True, True, True),
    expected_data_presence=(True, True, True, True),
    stdout_waits=((0, '"id":"clone"', 1, 20.0), (1, '"id":"state"', 1, 20.0),
                  (2, '"id":"entries"', 1, 20.0), (3, '"id":"tree"', 1, 20.0)),
    preserve_response_data_commands=("clone", "get_state", "get_entries", "get_tree"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    session_fixture_jsonl=_R7_BRANCHES,
    bind_fixture_working_directory=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    omit_no_tools=True,
)


_R12_FAILED_RECOVERY = InputRun(
    (
        (_line("malformed", "switch_session", sessionPath="r12-malformed.jsonl"), 0.0),
        (_line("prompt", "prompt", message="continue the selected session"), 0.0),
        (_line("state", "get_state"), 0.0),
        (_line("entries", "get_entries"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("malformed", "prompt", "state", "entries"),
    expected_response_commands=("switch_session", "prompt", "get_state", "get_entries"),
    expected_successes=(False, True, True, True),
    expected_data_presence=(False, True, True, True),
    expected_error_presence=(True, False, False, False),
    lifecycle_event_types=("agent_start", "turn_start", "turn_end", "agent_end", "agent_settled"),
    minimum_lifecycle_event_counts=(("agent_start", 1), ("turn_start", 1), ("turn_end", 1),
                                    ("agent_end", 1), ("agent_settled", 1)),
    stdout_waits=((0, '"id":"malformed"', 1, 20.0),
                  (1, '"id":"prompt"', 1, 20.0), (1, '"type":"agent_settled"', 1, 20.0),
                  (2, '"id":"state"', 1, 20.0), (3, '"id":"entries"', 1, 20.0)),
    provider_steps=(FixtureProviderStep(text="old session remained active", usage_updates=((12, 2, 14),)),),
    provider_waits=((2, 1, 20.0),),
    expected_min_provider_requests=1,
    compare_provider_requests=True,
    provider_request_text_excludes=("first branch question", "first branch answer"),
    preserve_response_data_commands=("get_state", "get_entries"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    # Pi updates its imported JSONL in place; PiSharp persists the same logical session in its native file.
    # Compare full RPC history and verify new text reached a persisted session while retaining both raw files.
    compare_session_files=False,
    expected_persisted_session_text=("continue the selected session", "old session remained active"),
    session_fixture_jsonl=_R7_BRANCHES,
    bind_fixture_working_directory=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    fixture_files=(("r12-malformed.jsonl", "{\"type\":\"session\",\"id\":\n"),),
)


_R12_CROSS_PROJECT_SOURCE = _r7_fixture((), session_id="r12-cross-project-source").replace(
    '"cwd":"/tmp"', '"cwd":"__RPC_SOURCE_PROJECT__"')
_R12_CROSS_PROJECT_TARGET = _r7_fixture((), session_id="r12-cross-project-target").replace(
    '"cwd":"/tmp"', '"cwd":"__RPC_TARGET_PROJECT__"')
_R12_CROSS_PROJECT = InputRun(
    (
        (_line("source-commands", "get_commands"), 0.0),
        (_line("switch", "switch_session", sessionPath="__RPC_TARGET_PROJECT__/r12-target.jsonl"), 0.0),
        (_line("target-commands", "get_commands"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("source-commands", "switch", "target-commands"),
    expected_response_commands=("get_commands", "switch_session", "get_commands"),
    expected_successes=(True, True, True),
    expected_data_presence=(True, True, True),
    stdout_waits=((0, '"id":"source-commands"', 1, 20.0),
                  (1, '"id":"switch"', 1, 20.0),
                  (2, '"id":"target-commands"', 1, 20.0)),
    response_only=True,
    preserve_response_data_commands=("get_commands", "switch_session"),
    expected_project_commands=(
        ("source-commands", ()),
        ("target-commands", (("target-resource", "prompt", "auto", "project", "top-level",
                              ".pi/prompts/target-resource.md"),)),
    ),
    expected_project_contexts=(("source-project", False), ("target-project", True)),
    project_rebind_fixture=True,
    normalize_workspace_paths=True,
    compare_stderr=False,
    session_fixture_jsonl=_R12_CROSS_PROJECT_SOURCE,
    fixture_files=(("target-project/r12-target.jsonl", _R12_CROSS_PROJECT_TARGET),),
    omit_no_tools=True,
)


_R12_PROVIDER_SOURCE = _r7_fixture((
    _r7_message("r12-provider-source-user", None, "user", "source base history", minute=1),
), session_id="r12-provider-source").replace('"cwd":"/tmp"', '"cwd":"__RPC_WORKING_DIRECTORY__"')
_R12_PROVIDER_TARGET = _r7_fixture((
    _r7_message("r12-provider-target-user", None, "user", "destination history", minute=1),
), session_id="r12-provider-destination").replace('"cwd":"/tmp"', '"cwd":"__RPC_WORKING_DIRECTORY__"')
_R12_BLOCKED_TOOL = FixtureProviderStep(
    tool_name="bash",
    tool_arguments=json.dumps({
        "command": 'gate=__RPC_TOOL_GATE__; echo effect >> "$gate.effect"; touch "$gate.started"; '
                   'while [ ! -f "$gate.release" ]; do sleep 0.01; done; echo released',
    }),
)
_R12_REPLACE_DURING_ACTIVE_TOOL = InputRun(
    (
        (_line("tool-prompt", "prompt", message="run one blocked non-idempotent shell command"), 0.0),
        (_line("fork", "fork", entryId="r7-u3"), 0.0),
        (_line("state", "get_state"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("tool-prompt", "fork", "state"),
    expected_response_commands=("prompt", "fork", "get_state"),
    expected_successes=(True, True, True),
    expected_data_presence=(True, True, True),
    expected_error_presence=(False, False, False),
    expected_response_dispositions=("started", None, None),
    response_data_expectations=(("fork", "text", "selected branch question"),
                               ("fork", "cancelled", False)),
    provider_steps=(_R12_BLOCKED_TOOL,),
    provider_waits=((0, 1, 10.0),),
    tool_start_waits=((0, 10.0),),
    stdout_waits=((0, '"id":"tool-prompt"', 1, 10.0),
                  (0, '"type":"tool_execution_start"', 1, 10.0),
                  (1, '"id":"fork"', 1, 8.0),
                  (1, '"type":"agent_settled"', 1, 8.0),
                  (2, '"id":"state"', 1, 10.0)),
    release_tool_gate_after_input_index=1,
    expected_min_provider_requests=1,
    compare_provider_requests=True,
    enable_bash_tool=True,
    lifecycle_event_types=("agent_start", "turn_start", "message_start", "message_update", "message_end",
                           "tool_execution_start", "tool_execution_update", "tool_execution_end", "turn_end",
                           "agent_end", "agent_settled"),
    preserve_event_payload_types=("message_start", "message_end", "tool_execution_start",
                                  "tool_execution_update", "tool_execution_end", "turn_end", "agent_settled"),
    preserve_message_update_deltas_only=True,
    preserve_agent_end_messages=True,
    minimum_lifecycle_event_counts=(("agent_start", 1), ("tool_execution_start", 1),
                                    ("tool_execution_end", 1), ("turn_end", 2),
                                    ("agent_end", 1), ("agent_settled", 1)),
    expected_marker_order=("response:tool-prompt", "event:agent_start", "event:tool_execution_start",
                           "event:tool_execution_end", "event:turn_end", "event:turn_start",
                           "event:message_end", "event:turn_end", "event:agent_end",
                           "event:agent_settled", "response:fork"),
    capture_session_files=True,
    compare_session_files=False,
    tool_side_effect_expectations=((".effect", "effect\n"),),
    session_fixture_jsonl=_R7_BRANCHES,
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
)
_R12_REPLACE_DURING_PROVIDER_STREAM = InputRun(
    (
        (_line("source-prompt", "prompt", message="source active prompt"), 0.0),
        (_line("switch", "switch_session",
               sessionPath="__RPC_SESSION_DIRECTORY__/r12-provider-target.jsonl"), 0.0),
        (_line("destination-prompt", "prompt", message="continue on destination"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("source-prompt", "switch", "destination-prompt"),
    expected_response_commands=("prompt", "switch_session", "prompt"),
    expected_successes=(True, True, True),
    expected_data_presence=(True, True, True),
    expected_error_presence=(False, False, False),
    expected_response_dispositions=("started", None, "started"),
    provider_steps=(
        FixtureProviderStep(text="source partial", hold_after_partial=True),
        FixtureProviderStep(text="destination reply"),
    ),
    provider_waits=((0, 1, 10.0), (2, 2, 20.0)),
    stdout_waits=((0, '"id":"source-prompt"', 1, 10.0),
                  (0, "source partial", 1, 10.0),
                  (1, '"id":"switch"', 1, 20.0),
                  (1, '"type":"agent_settled"', 1, 20.0),
                  (2, '"id":"destination-prompt"', 1, 10.0),
                  (2, '"type":"agent_settled"', 2, 20.0)),
    release_provider_after_input_index=1,
    expected_min_provider_requests=2,
    compare_provider_requests=True,
    provider_request_expectations=((1, ("destination history", "continue on destination"),
                                    ("source active prompt", "source partial")),),
    preserve_response_data_commands=("switch_session",),
    lifecycle_event_types=("turn_end", "agent_end", "agent_settled"),
    minimum_lifecycle_event_counts=(("turn_end", 2), ("agent_end", 2), ("agent_settled", 2)),
    expected_marker_order=("response:source-prompt", "event:turn_end", "event:agent_end",
                           "event:agent_settled", "response:switch", "response:destination-prompt",
                           "event:turn_end", "event:agent_end", "event:agent_settled"),
    session_fixture_jsonl=_R12_PROVIDER_SOURCE,
    session_fixture_files=(("r12-provider-target.jsonl", _R12_PROVIDER_TARGET),),
    session_file_expectations=(
        ("r12-provider-source", ("source base history", "source active prompt", "source partial"),
         ("destination reply",)),
        ("r12-provider-destination", ("destination history", "destination reply"),
         ("source active prompt", "source partial")),
    ),
    capture_session_files=True,
    # Pi writes JSONL while PiSharp owns a native session document. The explicit
    # source/target content assertions above compare their shared persistence contract.
    compare_session_files=False,
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    omit_no_tools=True,
)


_R12_NO_STALE_SESSION_WRITES = replace(
    _R12_REPLACE_DURING_PROVIDER_STREAM,
    release_provider_after_input_index=2,
)


_R12_OWNERSHIP_TARGET = _r7_fixture((
    _r7_message("r12-ownership-user", None, "user", "ownership base question", minute=1),
    _r7_message("r12-ownership-answer", "r12-ownership-user", "assistant",
                "ownership base answer", minute=2),
), session_id="r12-ownership-target").replace('"cwd":"/tmp"', '"cwd":"__RPC_WORKING_DIRECTORY__"')

_R12_SESSION_FILE_OWNERSHIP = InputRun(
    (
        (_line("new", "new_session"), 0.0),
        (_line("new-prompt", "prompt", message="new owned prompt"), 0.0),
        (_line("new-state", "get_state"), 0.0),
        (_line("switch", "switch_session",
               sessionPath="__RPC_SESSION_DIRECTORY__/ownership/r12-target.jsonl"), 0.0),
        (_line("switch-prompt", "prompt", message="switched owned prompt"), 0.0),
        (_line("switch-state", "get_state"), 0.0),
        (_line("fork", "fork", entryId="r12-ownership-user"), 0.0),
        (_line("fork-prompt", "prompt", message="fork owned prompt"), 0.0),
        (_line("fork-state", "get_state"), 0.0),
        (_line("clone", "clone"), 0.0),
        (_line("clone-prompt", "prompt", message="clone owned prompt"), 0.0),
        (_line("clone-state", "get_state"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("new", "new-prompt", "new-state", "switch", "switch-prompt", "switch-state",
                           "fork", "fork-prompt", "fork-state", "clone", "clone-prompt", "clone-state"),
    expected_response_commands=("new_session", "prompt", "get_state", "switch_session", "prompt", "get_state",
                                "fork", "prompt", "get_state", "clone", "prompt", "get_state"),
    expected_successes=(True,) * 12,
    expected_data_presence=(True,) * 12,
    response_only=True,
    provider_steps=(
        FixtureProviderStep(text="new session response"),
        FixtureProviderStep(text="switched session response"),
        FixtureProviderStep(text="forked session response"),
        FixtureProviderStep(text="cloned session response"),
    ),
    provider_waits=((1, 1, 20.0), (4, 2, 20.0), (7, 3, 20.0), (10, 4, 20.0)),
    stdout_waits=((0, '"id":"new"', 1, 15.0),
                  (1, '"id":"new-prompt"', 1, 15.0), (1, '"type":"agent_settled"', 1, 20.0),
                  (2, '"id":"new-state"', 1, 15.0),
                  (3, '"id":"switch"', 1, 15.0),
                  (4, '"id":"switch-prompt"', 1, 15.0), (4, '"type":"agent_settled"', 2, 20.0),
                  (5, '"id":"switch-state"', 1, 15.0),
                  (6, '"id":"fork"', 1, 15.0),
                  (7, '"id":"fork-prompt"', 1, 15.0), (7, '"type":"agent_settled"', 3, 20.0),
                  (8, '"id":"fork-state"', 1, 15.0),
                  (9, '"id":"clone"', 1, 15.0),
                  (10, '"id":"clone-prompt"', 1, 15.0), (10, '"type":"agent_settled"', 4, 20.0),
                  (11, '"id":"clone-state"', 1, 15.0)),
    expected_min_provider_requests=4,
    compare_provider_requests=True,
    preserve_response_data_commands=("new_session", "switch_session", "fork", "clone", "get_state"),
    preserve_session_identity=True,
    normalize_session_entry_ids=True,
    capture_session_files=True,
    compare_session_files=False,
    session_file_expectations=(
        ("rpc-r7-session", ("first question", "first answer"),
         ("new owned prompt", "switched owned prompt", "fork owned prompt", "clone owned prompt")),
        ("r12-ownership-target", ("ownership base question", "ownership base answer", "switched owned prompt"),
         ("fork owned prompt", "clone owned prompt")),
    ),
    session_file_state_expectations=(
        ("new-state", None, ("new owned prompt",),
         ("switched owned prompt", "fork owned prompt", "clone owned prompt")),
        ("switch-state", "ownership/", ("ownership base question", "ownership base answer",
                                           "switched owned prompt"),
         ("fork owned prompt", "clone owned prompt")),
        ("fork-state", "ownership/", ("fork owned prompt",),
         ("switched owned prompt", "clone owned prompt")),
        ("clone-state", "ownership/", ("fork owned prompt", "clone owned prompt"),
         ("switched owned prompt",)),
    ),
    session_fixture_jsonl=_R7_LINEAR,
    session_fixture_files=(("ownership/r12-target.jsonl", _R12_OWNERSHIP_TARGET),),
    bind_fixture_working_directory=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    omit_no_tools=True,
)


def _r12_history_after_operation(operation_id: str, operation: bytes, *,
                                 session_fixture_jsonl: str | None = None,
                                 fixture_files: tuple[tuple[str, str], ...] = (),
                                 expected_history: tuple[str, ...],
                                 excluded_history: tuple[str, ...]) -> InputRun:
    prompt_id = f"{operation_id}-prompt"
    prompt_text = f"follow-up request after {operation_id}"
    return InputRun(
        (
            (operation, 0.0),
            (_line(prompt_id, "prompt", message=prompt_text), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=(operation_id, prompt_id),
        expected_response_commands=("new_session" if operation_id == "new" else
                                    "switch_session" if operation_id == "switch" else operation_id,
                                    "prompt"),
        expected_successes=(True, True),
        expected_data_presence=(True, True),
        response_only=True,
        provider_steps=(FixtureProviderStep(text=f"reply after {operation_id}"),),
        provider_waits=((1, 1, 20.0),),
        stdout_waits=((0, f'"id":"{operation_id}"', 1, 15.0),
                      (1, f'"id":"{prompt_id}"', 1, 15.0),
                      (1, '"type":"agent_settled"', 1, 20.0)),
        expected_min_provider_requests=1,
        compare_provider_requests=True,
        provider_request_expectations=((0, expected_history, excluded_history),),
        session_fixture_jsonl=session_fixture_jsonl,
        fixture_files=fixture_files,
        capture_session_files=True,
        compare_session_files=False,
        normalize_session_entry_ids=True,
        bind_fixture_working_directory=True,
        model_context_window=65_536,
        settings_fixture={"compaction": {"enabled": False}},
        omit_no_tools=True,
    )


_R12_SUBSEQUENT_PROVIDER_HISTORY = (
    _r12_history_after_operation(
        "new", _line("new", "new_session"), session_fixture_jsonl=_R7_BRANCHES,
        expected_history=("follow-up request after new",),
        excluded_history=("root question", "first branch question", "selected branch question",
                          "selected branch answer")),
    _r12_history_after_operation(
        "switch", _line("switch", "switch_session", sessionPath="r12-history-target.jsonl"),
        session_fixture_jsonl=_R7_LINEAR,
        fixture_files=(("r12-history-target.jsonl", _R12_PROVIDER_TARGET),),
        expected_history=("destination history", "follow-up request after switch"),
        excluded_history=("first question", "root question")),
    _r12_history_after_operation(
        "fork", _line("fork", "fork", entryId="r7-u3"), session_fixture_jsonl=_R7_BRANCHES,
        expected_history=("root question", "follow-up request after fork"),
        excluded_history=("first branch question", "first branch answer", "selected branch answer")),
    _r12_history_after_operation(
        "clone", _line("clone", "clone"), session_fixture_jsonl=_R7_BRANCHES,
        expected_history=("root question", "selected branch question", "selected branch answer",
                          "follow-up request after clone"),
        excluded_history=("first branch question", "first branch answer")),
)


_R13_RESOURCE_FILES = (
    ("rpc-command-resources/review.md",
     "---\ndescription: Review a change\n---\nReview the current change.\n"),
    ("rpc-command-resources/quality/SKILL.md",
     "---\nname: quality-check\ndescription: Check quality\n---\nRun the quality checks.\n"),
)

_R13_SESSION_NAME_AND_CLEAR = InputRun(
    (
        (_line("set-name", "set_session_name", name="  R13 session name  "), 0.0),
        (_line("state-named", "get_state"), 0.0),
        (_line("clear-name", "set_session_name", name=""), 0.0),
        (_line("state-after-clear", "get_state"), 0.0),
        (_line("entries", "get_entries"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("set-name", "state-named", "clear-name", "state-after-clear", "entries"),
    expected_response_commands=("set_session_name", "get_state", "set_session_name", "get_state", "get_entries"),
    expected_successes=(True, True, False, True, True),
    expected_data_presence=(False, True, False, True, True),
    expected_error_presence=(False, False, True, False, False),
    state_expectations=(("state-named", "sessionName", "R13 session name"),
                        ("state-after-clear", "sessionName", "R13 session name")),
    capture_session_files=True,
    compare_session_files=False,
    expected_persisted_session_text=("R13 session name",),
    session_fixture_jsonl=_R7_LINEAR,
    bind_fixture_working_directory=True,
    normalize_session_entry_ids=True,
    preserve_response_data_commands=("get_state", "get_entries"),
    lifecycle_event_types=("session_info_changed",),
    preserve_event_payloads=True,
    expected_marker_order=("event:session_info_changed", "response:set-name", "response:state-named",
                           "response:clear-name", "response:state-after-clear", "response:entries"),
)

_R13_SESSION_INFO_ORDER = InputRun(
    (
        (_line("set-name", "set_session_name", name="R13 event order"), 0.0),
        (_line("state", "get_state"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("set-name", "state"),
    expected_response_commands=("set_session_name", "get_state"),
    expected_successes=(True, True),
    expected_data_presence=(False, True),
    lifecycle_event_types=("session_info_changed",),
    preserve_event_payloads=True,
    expected_marker_order=("event:session_info_changed", "response:set-name", "response:state"),
    state_expectations=(("state", "sessionName", "R13 event order"),),
)

_R13_EXPORT_DEFAULT = InputRun(
    (
        (_line("export", "export_html"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("export",),
    expected_response_commands=("export_html",),
    expected_successes=(True,),
    expected_data_presence=(True,),
    stdout_waits=((0, '"id":"export"', 1, 15.0),),
    session_fixture_jsonl=_R7_LINEAR,
    session_fixture_name="rpc-r7-session.jsonl",
    bind_fixture_working_directory=True,
    capture_export_files=True,
    compare_export_files=True,
    export_file_expectations=(("export", "pi-session-rpc-r7-session.html",
                               ("first question", "first answer"), ()),),
    normalize_workspace_paths=True,
)

_R13_EXPORT_ACTIVE = InputRun(
    (
        (_line("active-prompt", "prompt", message="R13 active export prompt"), 0.0),
        (_line("state-active", "get_state"), 0.0),
        (_line("export", "export_html", outputPath="rpc-exports/active.html"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("active-prompt", "state-active", "export"),
    expected_response_commands=("prompt", "get_state", "export_html"),
    expected_successes=(True, True, True),
    expected_data_presence=(True, True, True),
    expected_response_dispositions=("started", None, None),
    provider_steps=(FixtureProviderStep(text="partial response", hold_before_response=True),),
    provider_waits=((0, 1, 15.0),),
    stdout_waits=((0, '"id":"active-prompt"', 1, 15.0),
                  (1, '"id":"state-active"', 1, 15.0),
                  (2, '"id":"export"', 1, 15.0),
                  (3, '"type":"agent_settled"', 1, 20.0)),
    release_provider_after_input_index=2,
    provider_request_expectations=((0, ("R13 active export prompt",), ()),),
    compare_provider_requests=True,
    expected_min_provider_requests=1,
    session_fixture_jsonl=_R7_LINEAR,
    session_fixture_name="rpc-r7-session.jsonl",
    fixture_files=(("rpc-exports/.keep", ""),),
    bind_fixture_working_directory=True,
    capture_export_files=True,
    compare_export_files=True,
    export_file_expectations=(("export", "active.html",
                               ("first question", "first answer", "R13 active export prompt"),
                               ("partial response",)),),
    preserve_state_flags=True,
    state_flag_expectations=(("state-active", "isStreaming", True),),
    lifecycle_event_types=("message_update", "agent_settled"),
    expected_marker_order=("response:active-prompt", "response:state-active", "response:export",
                           "event:message_update", "event:agent_settled"),
    normalize_workspace_paths=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
)

_R13_EXPORT_ERRORS = (
    InputRun(
        ((_line("in-memory", "export_html"), 0.0), (b"", 0.0)),
        expected_response_ids=("in-memory",), expected_response_commands=("export_html",),
        expected_successes=(False,), expected_data_presence=(False,), expected_error_presence=(True,),
        stdout_waits=((0, '"id":"in-memory"', 1, 15.0),),
        no_session=True,
    ),
    InputRun(
        ((_line("invalid-path", "export_html", outputPath="rpc-export-blocker/out.html"), 0.0),
         (_line("state", "get_state"), 0.0), (b"", 0.0)),
        expected_response_ids=("invalid-path", "state"),
        expected_response_commands=("export_html", "get_state"),
        expected_successes=(False, True), expected_data_presence=(False, True),
        expected_error_presence=(True, False),
        stdout_waits=((0, '"id":"invalid-path"', 1, 15.0), (1, '"id":"state"', 1, 15.0)),
        session_fixture_jsonl=_R7_LINEAR,
        fixture_files=(("rpc-export-blocker", "obstacle"),),
        bind_fixture_working_directory=True,
        preserve_response_data_commands=("get_state",),
    ),
)

_R13_COMMANDS_BUILTIN = InputRun(
    ((_line("commands", "get_commands"), 0.0), (b"", 0.0)),
    expected_response_ids=("commands",), expected_response_commands=("get_commands",),
    expected_successes=(True,), expected_data_presence=(True,),
    expected_command_names=(("commands", ("review", "skill:quality-check")),),
    fixture_files=_R13_RESOURCE_FILES,
    rpc_command_resources=True,
    normalize_workspace_paths=True,
    preserve_response_data_commands=("get_commands",),
)

_R13_COMMANDS_EXTENSION_ACTIVE = InputRun(
    (
        (_line("prompt", "prompt", message="wait while discovering extensions"), 0.0),
        (_line("state", "get_state"), 0.0),
        (_line("commands", "get_commands"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("prompt", "state", "commands"),
    expected_response_commands=("prompt", "get_state", "get_commands"),
    expected_successes=(True, True, True), expected_data_presence=(True, True, True),
    expected_response_dispositions=("started", None, None),
    provider_steps=(FixtureProviderStep(text="extension discovery complete", hold_before_response=True),),
    provider_waits=((0, 1, 15.0),),
    stdout_waits=((0, '"id":"prompt"', 1, 15.0),
                  (1, '"id":"state"', 1, 15.0),
                  (2, '"id":"commands"', 1, 15.0),
                  (3, '"type":"agent_settled"', 1, 20.0)),
    release_provider_after_input_index=2,
    expected_min_provider_requests=1,
    compare_provider_requests=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    state_flag_expectations=(("state", "isStreaming", True),),
    preserve_state_flags=True,
    expected_command_names=(("commands", ("rpc-fixture-navigate-tree",)),),
    use_rpc_navigation_extension=True,
    lifecycle_event_types=("agent_settled",),
    expected_marker_order=("response:prompt", "response:state", "response:commands", "event:agent_settled"),
    normalize_workspace_paths=True,
)

_R13_COMMANDS_AFTER_RELOAD = (
    InputRun(
        ((_line("commands", "get_commands"), 0.0), (b"", 0.0)),
        expected_response_ids=("commands",),
        expected_response_commands=("get_commands",),
        expected_successes=(True,), expected_data_presence=(True,),
        expected_command_names=(("commands", ("rpc-fixture-navigate-tree", "review", "skill:quality-check")),),
        fixture_files=_R13_RESOURCE_FILES,
        rpc_command_resources=True,
        use_rpc_navigation_extension=True,
        normalize_workspace_paths=True,
    ),
    InputRun(
        (
            (_line("commands", "get_commands"), 0.0),
            (_line("state", "get_state"), 0.0),
            (b"", 0.0),
        ),
        expected_response_ids=("commands", "state"),
        expected_response_commands=("get_commands", "get_state"),
        expected_successes=(True, True), expected_data_presence=(True, True),
        expected_command_names=(("commands", ("rpc-fixture-navigate-tree", "review", "skill:quality-check")),),
        fixture_files=_R13_RESOURCE_FILES,
        rpc_command_resources=True,
        use_rpc_navigation_extension=True,
        reuse_workspace_previous=True,
        normalize_workspace_paths=True,
    ),
)


R12_SCENARIOS: dict[str, tuple[InputRun, ...]] = {
    "R12.new-session-empty": (_R12_NEW_EMPTY,),
    "R12.new-session-parent-session": _parent_session_variants(),
    "R12.switch-native-session": (_R12_SWITCH_NATIVE,),
    "R12.switch-pi-jsonl-session": (_R12_SWITCH_PI_JSONL,),
    "R12.switch-missing-or-malformed-target": (_R12_MISSING_OR_MALFORMED,),
    "R12.fork-entry-and-branch-point": (_R12_FORK,),
    "R12.clone-current-branch": (_R12_CLONE,),
    "R12.failed-replacement-recovery": (_R12_FAILED_RECOVERY,),
    "R12.cross-project-rebind-trust": (_R12_CROSS_PROJECT,),
    "R12.replace-during-provider-stream": (_R12_REPLACE_DURING_PROVIDER_STREAM,),
    "R12.aborted-turn-settlement-order": (_R12_REPLACE_DURING_PROVIDER_STREAM,),
    "R12.replace-during-active-tool": (_R12_REPLACE_DURING_ACTIVE_TOOL,),
    "R12.no-stale-session-writes": (_R12_NO_STALE_SESSION_WRITES,),
    "R12.session-file-ownership": (_R12_SESSION_FILE_OWNERSHIP,),
    "R12.subsequent-provider-history": _R12_SUBSEQUENT_PROVIDER_HISTORY,
}

R13_SCENARIOS: dict[str, tuple[InputRun, ...]] = {
    "R13.set-session-name-and-clear": (_R13_SESSION_NAME_AND_CLEAR,),
    "R13.session-info-event-order": (_R13_SESSION_INFO_ORDER,),
    "R13.export-default-path": (_R13_EXPORT_DEFAULT,),
    "R13.export-explicit-path": (_R13_EXPORT_ACTIVE,),
    "R13.export-errors": _R13_EXPORT_ERRORS,
    "R13.get-commands-builtins": (_R13_COMMANDS_BUILTIN,),
    "R13.get-commands-extension": (_R13_COMMANDS_EXTENSION_ACTIVE,),
    "R13.commands-after-reload": _R13_COMMANDS_AFTER_RELOAD,
}


def _r14_ui_request(method: str, **fields: object) -> dict[str, object]:
    return {"type": "extension_ui_request", "method": method, **fields}


def _r14_command(operation: str, *, requests: tuple[dict[str, object], ...] = (),
                 responses: tuple[tuple[str, str, dict[str, object]], ...] = (),
                 wait_all: bool = False, **flags: object) -> InputRun:
    response_id = str(flags.pop("response_id", "r14-command"))
    return InputRun(
        ((_line(response_id, "prompt", message=f"/rpc-fixture-ui {operation}"), 0.0),),
        expected_response_ids=(response_id,),
        expected_response_commands=("prompt",),
        expected_successes=(True,),
        expected_data_presence=(True,),
        expected_response_dispositions=("handled",),
        stdout_waits=((0, f'"id":"{response_id}"', 1, 20.0),),
        ui_responses=responses,
        ui_wait_all_requests=wait_all,
        ui_expected_requests=requests,
        use_rpc_ui_extension=True,
        **flags,
    )


_R14_SELECT = _r14_command(
    "select",
    requests=(
        _r14_ui_request("select", title="R14 select", options=["red", "blue"]),
        _r14_ui_request("notify", message='R14:select:"blue"', notifyType="info"),
    ),
    responses=(("select", "R14 select", {"value": "blue"}),),
)

_R14_CONFIRM_RUNS = tuple(
    _r14_command(
        "confirm",
        requests=(
            _r14_ui_request("confirm", title="R14 confirm", message="Proceed?"),
            _r14_ui_request("notify", message=f"R14:confirm:{str(confirmed).lower()}", notifyType="info"),
        ),
        responses=(("confirm", "R14 confirm", {"confirmed": confirmed}),),
        response_id=f"r14-confirm-{str(confirmed).lower()}",
    )
    for confirmed in (True, False)
)

_R14_INPUT = _r14_command(
    "input",
    requests=(
        _r14_ui_request("input", title="R14 input", placeholder="type here"),
        _r14_ui_request("input", title="R14 input no placeholder"),
        _r14_ui_request("notify", message='R14:input:["first value","second value"]', notifyType="info"),
    ),
    responses=(
        ("input", "R14 input", {"value": "first value"}),
        ("input", "R14 input no placeholder", {"value": "second value"}),
    ),
)

_R14_EDITOR = _r14_command(
    "editor",
    requests=(
        _r14_ui_request("editor", title="R14 editor", prefill="prefill text"),
        _r14_ui_request("notify", message='R14:editor:"edited text"', notifyType="info"),
    ),
    responses=(("editor", "R14 editor", {"value": "edited text"}),),
)

_R14_NOTIFY = _r14_command(
    "notify",
    requests=(
        _r14_ui_request("notify", message="R14 notify info", notifyType="info"),
        _r14_ui_request("notify", message="R14 notify warning", notifyType="warning"),
        _r14_ui_request("notify", message="R14 notify error", notifyType="error"),
    ),
)

_R14_STATUS = _r14_command(
    "status",
    requests=(
        _r14_ui_request("setStatus", statusKey="r14", statusText="working"),
        _r14_ui_request("setStatus", statusKey="r14"),
    ),
)

_R14_WIDGET = _r14_command(
    "widget",
    requests=(
        _r14_ui_request("setWidget", widgetKey="r14", widgetLines=["first", "second"],
                        widgetPlacement="aboveEditor"),
        _r14_ui_request("setWidget", widgetKey="r14", widgetLines=["below"],
                        widgetPlacement="belowEditor"),
        _r14_ui_request("setWidget", widgetKey="r14"),
    ),
)

_R14_TITLE = _r14_command(
    "title", requests=(_r14_ui_request("setTitle", title="R14 title"),),
)

_R14_EDITOR_TEXT_ACTIVE = InputRun(
    (
        (_line("r14-stream-prompt", "prompt", message="Hold a provider stream for the RPC UI event."), 0.0),
        (_line("r14-active-state", "get_state"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("r14-stream-prompt", "r14-active-state"),
    expected_response_commands=("prompt", "get_state"),
    expected_successes=(True, True), expected_data_presence=(True, True),
    expected_response_dispositions=("started", None),
    stdout_waits=((0, '"id":"r14-stream-prompt"', 1, 15.0),
                  (1, '"id":"r14-active-state"', 1, 15.0),
                  (2, '"type":"agent_settled"', 1, 20.0),
                  (2, '"method":"set_editor_text"', 1, 15.0)),
    provider_steps=(FixtureProviderStep(text="R14 active stream response", hold_before_response=True),),
    provider_waits=((0, 1, 15.0),),
    release_provider_after_input_index=1,
    expected_min_provider_requests=1,
    compare_provider_requests=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    lifecycle_event_types=("message_update", "agent_settled"),
    preserve_message_update_deltas_only=True,
    state_flag_expectations=(("r14-active-state", "isStreaming", True),),
    preserve_state_flags=True,
    ui_expected_requests=(_r14_ui_request("set_editor_text", text="R14 editor text"),),
    use_rpc_ui_extension=True,
    rpc_ui_stream_editor=True,
)

_R14_EDITOR_TEXT_ACTIVE = replace(
    _R14_EDITOR_TEXT_ACTIVE,
    stdout_waits=((0, '"id":"r14-stream-prompt"', 1, 15.0),
                  (1, '"id":"r14-active-state"', 1, 15.0),
                  (2, '"method":"set_editor_text"', 1, 15.0),
                  (2, '"type":"agent_settled"', 1, 20.0)),
)

_R14_CONCURRENT_RESPONSES = (
    ("select", "R14 concurrent select", {"value": "two"}),
    ("input", "R14 concurrent input", {"value": "concurrent value"}),
)
_R14_CONCURRENT_REQUESTS = (
    _r14_ui_request("select", title="R14 concurrent select", options=["one", "two"]),
    _r14_ui_request("input", title="R14 concurrent input", placeholder="concurrent placeholder"),
    _r14_ui_request("notify", message='R14:concurrent:["two","concurrent value"]', notifyType="info"),
)
_R14_CONCURRENT = _r14_command(
    "concurrent", requests=_R14_CONCURRENT_REQUESTS,
    responses=_R14_CONCURRENT_RESPONSES, wait_all=True,
)
_R14_OUT_OF_ORDER = _r14_command(
    "concurrent", requests=_R14_CONCURRENT_REQUESTS,
    responses=tuple(reversed(_R14_CONCURRENT_RESPONSES)), wait_all=True,
)

_R14_TIMEOUT = _r14_command(
    "timeout",
    requests=(
        _r14_ui_request("input", title="R14 timeout", placeholder="wait", timeout=40),
        _r14_ui_request("notify", message="R14:timeout:null", notifyType="info"),
    ),
)

_R14_CANCEL = _r14_command(
    "cancel",
    requests=(
        _r14_ui_request("input", title="R14 cancel", placeholder="cancel me"),
        _r14_ui_request("notify", message="R14:cancel:null", notifyType="info"),
    ),
    responses=(("input", "R14 cancel", {"cancelled": True}),),
    ui_duplicate_late_response=True,
)

_R14_PENDING_SHUTDOWN = InputRun(
    ((_line("r14-pending", "prompt", message="/rpc-fixture-ui pending"), 0.0),),
    expected_response_ids=(), expected_response_commands=(), expected_successes=(),
    expected_data_presence=(), expected_response_dispositions=(),
    stdout_waits=((0, '"method":"input"', 1, 15.0),),
    ui_expected_requests=(_r14_ui_request("input", title="R14 pending", placeholder="shutdown"),),
    use_rpc_ui_extension=True,
)

_R14_UNSUPPORTED = _r14_command(
    "unsupported",
    requests=(
        _r14_ui_request("set_editor_text", text="R14 pasted text"),
        _r14_ui_request("notify", message='R14:unsupported:{"custom":true,"editor":"","toolsExpanded":false,"theme":true,"themeSwitchFailed":true,"editorComponent":true,"themes":0}',
                        notifyType="info"),
    ),
)

_R14_EXTENSION_ERROR = InputRun(
    (
        (_line("r14-error-prompt", "prompt", message="trigger the deterministic extension error"), 0.0),
        (_line("r14-after-error-state", "get_state"), 0.0),
        (b"", 0.0),
    ),
    expected_response_ids=("r14-error-prompt", "r14-after-error-state"),
    expected_response_commands=("prompt", "get_state"),
    expected_successes=(True, True), expected_data_presence=(True, True),
    expected_response_dispositions=("started", None),
    stdout_waits=((0, '"id":"r14-error-prompt"', 1, 15.0),
                  (0, '"type":"extension_error"', 1, 15.0),
                  (1, '"id":"r14-after-error-state"', 1, 15.0),
                  (1, '"type":"agent_settled"', 1, 20.0)),
    provider_steps=(FixtureProviderStep(text="R14 provider continued after extension error"),),
    provider_waits=((0, 1, 15.0),),
    expected_min_provider_requests=1,
    compare_provider_requests=True,
    model_context_window=65_536,
    settings_fixture={"compaction": {"enabled": False}},
    lifecycle_event_types=("extension_error",),
    preserve_event_payload_types=("extension_error",),
    use_rpc_ui_extension=True,
    rpc_ui_extension_error=True,
    normalize_workspace_paths=True,
    normalize_extension_paths=True,
)

_R14_CORRELATION = _r14_command(
    "correlation",
    requests=(
        _r14_ui_request("select", title="R14 correlation", options=["ok"]),
        _r14_ui_request("notify", message='R14:correlation:"ok"', notifyType="info"),
    ),
    responses=(("select", "R14 correlation", {"value": "ok"}),),
    response_id="r14-correlated-command",
    ui_unknown_response_ids=("r14-correlated-command",),
)

R14_SCENARIOS: dict[str, tuple[InputRun, ...]] = {
    "R14.ui-select-roundtrip": (_R14_SELECT,),
    "R14.ui-confirm-roundtrip": _R14_CONFIRM_RUNS,
    "R14.ui-input-roundtrip": (_R14_INPUT,),
    "R14.ui-editor-roundtrip": (_R14_EDITOR,),
    "R14.ui-notify-fire-and-forget": (_R14_NOTIFY,),
    "R14.ui-set-status": (_R14_STATUS,),
    "R14.ui-set-widget": (_R14_WIDGET,),
    "R14.ui-set-title": (_R14_TITLE,),
    "R14.ui-set-editor-text": (_R14_EDITOR_TEXT_ACTIVE,),
    "R14.ui-unique-concurrent-ids": (_R14_CONCURRENT,),
    "R14.ui-out-of-order-responses": (_R14_OUT_OF_ORDER,),
    "R14.ui-timeout-default": (_R14_TIMEOUT,),
    "R14.ui-cancel-late-unknown": (_R14_CANCEL,),
    "R14.ui-unload-and-shutdown": (_R14_PENDING_SHUTDOWN,),
    "R14.ui-unsupported-methods": (_R14_UNSUPPORTED,),
    "R14.extension-handler-error-event": (_R14_EXTENSION_ERROR,),
    "R14.ui-isolated-from-command-correlation": (_R14_CORRELATION,),
}


_R15_LARGE_NAME = "🧵" * 24_000
_R15_TOOL_CALL = FixtureProviderStep(
    tool_name="bash",
    tool_arguments=json.dumps({
        "command": 'gate=__RPC_TOOL_GATE__; echo effect >> "$gate.effect"; '
                   'touch "$gate.started"; '
                   'while [ ! -f "$gate.release" ]; do sleep 0.01; done; '
                   'cat "$gate.marker"',
    }),
)

R15_SCENARIOS: dict[str, tuple[InputRun, ...]] = {
    "R15.normal-eof": (
        InputRun(
            ((_line("eof-state", "get_state"), 0.0), (b"", 0.0)),
            expected_response_ids=("eof-state",),
            expected_response_commands=("get_state",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            stdout_waits=((0, '"id":"eof-state"', 1, 10.0),),
            require_all_stdout_json=True,
            require_empty_stderr=True,
        ),
    ),
    "R15.explicit-process-termination": (
        InputRun(
            ((_line("signal-idle-state", "get_state"), 0.0),),
            expected_response_ids=("signal-idle-state",),
            expected_response_commands=("get_state",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            expected_exit_code=None,
            stdout_waits=((0, '"id":"signal-idle-state"', 1, 10.0),),
            terminate_after_input_index=0,
            termination_signal="SIGKILL",
            require_all_stdout_json=True,
        ),
        InputRun(
            ((_line("signal-active-prompt", "prompt", message="hold until SIGTERM"), 0.0),),
            expected_response_ids=("signal-active-prompt",),
            expected_response_commands=("prompt",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            expected_response_dispositions=("started",),
            expected_exit_code=None,
            provider_steps=(FixtureProviderStep(hold_before_response=True, text="x" * 5_000_000),),
            provider_waits=((0, 1, 10.0),),
            stdout_waits=((0, '"id":"signal-active-prompt"', 1, 10.0),),
            terminate_after_input_index=0,
            termination_signal="SIGKILL",
            require_all_stdout_json=True,
        ),
    ),
    "R15.cancel-provider-turn-on-shutdown": (
        InputRun(
            ((_line("shutdown-prompt", "prompt", message="hold until stdin closes"), 0.0),
             (b"", 0.0)),
            expected_response_ids=("shutdown-prompt",),
            expected_response_commands=("prompt",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            expected_response_dispositions=("started",),
            provider_steps=(FixtureProviderStep(hold_before_response=True, text="x" * 5_000_000),),
            provider_waits=((0, 1, 10.0),),
            expected_min_provider_requests=1,
            compare_provider_requests=True,
            model_catalog=({
                "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
                "reasoning": False, "input": ["text"], "contextWindow": 65_536, "maxTokens": 1,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
            },),
            initial_model_id="fixture-model",
            stdout_waits=((0, '"id":"shutdown-prompt"', 1, 10.0),),
            lifecycle_event_types=("agent_start", "turn_start", "message_start", "message_update",
                                   "message_end", "turn_end", "agent_end", "agent_settled"),
            preserve_event_payloads=True,
            capture_session_files=True,
            compare_session_files=False,
            response_only=True,
            normalize_session_entry_ids=True,
            assert_process_group_disposed=True,
        ),
    ),
    "R15.pending-command-shutdown": (
        InputRun(
            ((_line("shutdown-compact", "compact", customInstructions="Hold the summary request"), 0.0),
             (_line("shutdown-entries", "get_entries"), 0.0),
             (_line("shutdown-state", "get_state"), 0.0),
             (b"", 0.0)),
            provider_steps=(FixtureProviderStep(hold_before_response=True, text="shutdown summary"),),
            provider_waits=((0, 1, 10.0),),
            expected_min_provider_requests=1,
            compare_provider_requests=True,
            stdout_waits=((0, '"type":"compaction_start"', 1, 10.0),
                          (1, '"id":"shutdown-entries"', 1, 10.0),
                          (2, '"id":"shutdown-state"', 1, 10.0)),
            lifecycle_event_types=("compaction_start", "compaction_end"),
            preserve_event_payloads=True,
            session_fixture_jsonl=_r10_compaction_fixture(),
            capture_session_files=True,
            compare_session_files=False,
            normalize_session_entry_ids=True,
            session_file_expectations=(("r10-compact-session", ("earlier user question with context",),
                                        ('"type":"compaction"', '"Type":"compaction"')),
                                       ),
            model_context_window=65_536,
            settings_fixture={"compaction": {"enabled": True, "keepRecentTokens": 10}},
        ),
    ),
    "R15.pending-extension-dialog-shutdown": (
        InputRun(
            ((_line("shutdown-dialog", "prompt", message="/rpc-fixture-ui pending"), 0.0),
             (b"", 0.0)),
            expected_response_ids=(),
            expected_response_commands=(),
            expected_successes=(),
            expected_data_presence=(),
            expected_response_dispositions=(),
            stdout_waits=((0, '"method":"input"', 1, 15.0),),
            ui_expected_requests=(_r14_ui_request("input", title="R14 pending", placeholder="shutdown"),),
            use_rpc_ui_extension=True,
            require_all_stdout_json=True,
        ),
    ),
    "R15.active-tool-cancellation": (
        InputRun(
            ((_line("shutdown-tool-prompt", "prompt", message="run the blocked fixture tool"), 0.0),
             (b"", 0.0)),
            expected_response_ids=("shutdown-tool-prompt",),
            expected_response_commands=("prompt",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            expected_response_dispositions=("started",),
            provider_steps=(_R15_TOOL_CALL,),
            provider_waits=((0, 1, 10.0),),
            tool_start_waits=((0, 10.0),),
            expected_min_provider_requests=1,
            compare_provider_requests=True,
            model_catalog=({
                "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
                "reasoning": False, "input": ["text"], "contextWindow": 65_536, "maxTokens": 1,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
            },),
            initial_model_id="fixture-model",
            lifecycle_event_types=("agent_start", "turn_start", "message_start", "message_update", "message_end",
                                   "tool_execution_start", "tool_execution_update", "tool_execution_end",
                                   "turn_end", "agent_end", "agent_settled"),
            preserve_event_payloads=True,
            capture_session_files=True,
            compare_session_files=False,
            response_only=True,
            normalize_session_entry_ids=True,
            enable_bash_tool=True,
            fixture_files=(("rpc-tool-gate.marker", "R15_LATE_TOOL_OUTPUT_5e6f3\n"),),
            tool_side_effect_expectations=((".effect", "effect\n"),),
            forbidden_persisted_session_text=("R15_LATE_TOOL_OUTPUT_5e6f3",),
            assert_process_group_disposed=True,
        ),
    ),
    "R15.partial-input-shutdown": (
        InputRun(
            ((b'{"type":"get_state","id":"partial-input"', 0.0), (b"", 0.0)),
            expected_response_ids=(_ABSENT,),
            expected_response_commands=("parse",),
            expected_successes=(False,),
            require_diagnostic=True,
            require_all_stdout_json=True,
            require_empty_stderr=True,
        ),
    ),
    "R15.output-backpressure": (
        InputRun(
            ((_line("backpressure-name", "set_session_name", name=_R15_LARGE_NAME), 0.0), (b"", 0.0)),
            expected_response_ids=("backpressure-name",),
            expected_response_commands=("set_session_name",),
            expected_successes=(True,),
            expected_data_presence=(False,),
            stdout_read_chunk_size=4096,
            stdout_pause_after_input_index=0,
            stdout_pause_seconds=0.25,
            require_all_stdout_json=True,
            require_empty_stderr=True,
        ),
    ),
    "R15.broken-pipe-handling": (
        InputRun(
            ((_line("pipe-state", "get_state"), 0.0),
             (_line("pipe-name", "set_session_name", name=_R15_LARGE_NAME), 0.0),
             (b"", 0.0)),
            expected_response_ids=None,
            expected_exit_code=None,
            stdout_waits=((0, '"id":"pipe-state"', 1, 10.0),),
            stdout_pause_before_input_index=1,
            stdout_pause_after_input_index=1,
            stdout_pause_seconds=0.5,
            close_stdout_after_input_index=1,
            require_all_stdout_json=False,
            compare_stderr=False,
        ),
        InputRun(
            ((_line("closed-stderr", "get_state"), 0.0), (b"", 0.0)),
            expected_response_ids=("closed-stderr",),
            expected_response_commands=("get_state",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            stdout_waits=((0, '"id":"closed-stderr"', 1, 10.0),),
            close_stderr_after_input_index=0,
            require_all_stdout_json=True,
        ),
    ),
    "R15.rapid-command-burst": (
        InputRun(
            ((b"".join(_line(f"burst-{index:03d}", "get_state") for index in range(100)), 0.0),
             (b"", 0.0)),
            expected_response_ids=tuple(f"burst-{index:03d}" for index in range(100)),
            expected_response_commands=("get_state",) * 100,
            expected_successes=(True,) * 100,
            expected_data_presence=(True,) * 100,
            stdout_waits=((0, '"id":"burst-099"', 1, 20.0),),
            require_all_stdout_json=True,
            require_empty_stderr=True,
        ),
    ),
    "R15.multiple-pending-shutdown": (
        InputRun(
            ((_line("multi-prompt", "prompt", message="hold provider request until shutdown"), 0.0),
             (_line("multi-bash", "bash", command=(
                 "gate=__RPC_TOOL_GATE__; echo effect >> \"$gate.effect\"; "
                 "touch \"$gate.started\"; "
                 "while [ ! -f \"$gate.release\" ]; do sleep 0.01; done; "
                 "cat \"$gate.marker\"")), 0.0),
             (b"", 0.0)),
            provider_steps=(FixtureProviderStep(hold_before_response=True),),
            provider_waits=((0, 1, 10.0),),
            tool_start_waits=((1, 10.0),),
            expected_min_provider_requests=1,
            compare_provider_requests=True,
            model_catalog=({
                "id": "fixture-model", "name": "fixture-model", "api": "openai-completions",
                "reasoning": False, "input": ["text"], "contextWindow": 65_536, "maxTokens": 1,
                "cost": {"input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0},
            },),
            initial_model_id="fixture-model",
            stdout_waits=((0, '"id":"multi-prompt"', 1, 10.0),),
            lifecycle_event_types=("agent_start", "turn_start", "bash_execution_update", "agent_settled"),
            preserve_event_payloads=True,
            capture_session_files=True,
            compare_session_files=False,
            response_only=True,
            normalize_session_entry_ids=True,
            enable_bash_tool=True,
            bind_rpc_tool_gate_path=True,
            fixture_files=(("rpc-tool-gate.marker", "R15_LATE_TOOL_OUTPUT_5e6f3\n"),),
            tool_side_effect_expectations=((".effect", "effect\n"),),
            forbidden_persisted_session_text=("R15_LATE_TOOL_OUTPUT_5e6f3",),
            assert_process_group_disposed=True,
        ),
    ),
    "R15.process-disposal-repeat": tuple(
        InputRun(
            ((_line(f"dispose-{index}", "get_state"), 0.0), (b"", 0.0)),
            expected_response_ids=(f"dispose-{index}",),
            expected_response_commands=("get_state",),
            expected_successes=(True,),
            expected_data_presence=(True,),
            stdout_waits=((0, f'"id":"dispose-{index}"', 1, 10.0),),
            require_all_stdout_json=True,
            require_empty_stderr=True,
        )
        for index in range(3)
    ),
    "R15.partial-write-record-integrity": (
        InputRun(
            ((_line("partial-write-name", "set_session_name", name="🧵" * 8_000), 0.0),
             (b"", 0.0)),
            expected_response_ids=("partial-write-name",),
            expected_response_commands=("set_session_name",),
            expected_successes=(True,),
            expected_data_presence=(False,),
            stdout_read_chunk_size=7,
            require_all_stdout_json=True,
            require_empty_stderr=True,
        ),
    ),
}
