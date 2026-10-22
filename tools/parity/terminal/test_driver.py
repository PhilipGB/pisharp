import copy
import gzip
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import time
from threading import Event, Thread
import tempfile
import urllib.request
import unittest
from unittest.mock import patch

from compare import (ControlTrace, canonical_render_frames, canonicalize_tool_call_arguments_in_requests,
                     compare_products, differences, normalize_terminal_state, osc52_clipboard_writes)
from fixture_http import FixtureServer
from pty_process import RenderFrameSequence, RenderFrameStore, TerminalProcess
from run import commands, environment, expand_environment, prepare_workspace, stage_native_extension_package, write_json_stream, write_scenario_files
from thread_limits import TEST_THREAD_LIMIT, apply_test_thread_limit, thread_limit_environment

apply_test_thread_limit()


class DriverTests(unittest.TestCase):
    def test_clipboard_checkpoint_waits_for_every_expected_write_and_the_settled_frame(self):
        terminal = object.__new__(TerminalProcess)
        terminal.raw = bytearray()
        terminal.closed = False
        terminal.last_output = 0
        terminal._new_render_frames = []
        terminal.current = None
        terminal.pumps = 0
        first = dict(viewport=[dict(cells=[dict(chars='Copied!')])], modes={}, generation=1)
        final = dict(viewport=[dict(cells=[dict(chars='Copied!')])], modes={}, generation=2)

        def pump(timeout=0):
            terminal.pumps += 1
            terminal.current = first if terminal.pumps == 1 else final
            terminal._new_render_frames = [terminal.current]
            terminal.raw.extend(b'\x1b]52;c;d29yZA==\x07' if terminal.pumps == 1 else
                                b'\x1b]52;c;bGluZQ==\x07')

        terminal.pump = pump
        terminal.snapshot = lambda: terminal.current
        captured = terminal.settle('Copied!', quiet_ms=0, render_match=True,
                                   clipboard_texts=['word', 'line'])
        self.assertIs(final, captured)
        self.assertEqual(2, terminal.pumps)

    @unittest.skipUnless(sys.platform == 'linux', 'Requires Linux process-group supervision')
    def test_suspended_fixture_cleanup_reaps_the_owned_child_group(self):
        with tempfile.TemporaryDirectory() as directory:
            options = dict(columns=80, rows=24, jobControl=True)
            terminal = TerminalProcess(
                [sys.executable, '-c', 'import os, signal; os.kill(0, signal.SIGTSTP)'],
                Path(directory), dict(os.environ), options)
            try:
                terminal.settle('suspended:tty-restored', timeout=5)
                child_file = Path(f'/proc/{terminal.process.pid}/task/{terminal.process.pid}/children')
                children = [int(value) for value in child_file.read_text().split()]
                self.assertEqual(1, len(children))
                self.assertIn('\nState:\tT ', Path(f'/proc/{children[0]}/status').read_text())
            finally:
                terminal.close()
            self.assertFalse(Path(f'/proc/{children[0]}').exists())

    def test_required_terminal_matrix_has_named_stable_checkpoints(self):
        required = {
            'startup-verbose': {'startup', 'quit'},
            'transcript-markdown-resume': {'transcript', 'resize', 'quit'},
            'terminal-streaming-markdown': {
                'stream-first-boundary', 'complete-inline-boundaries', 'commit-answer', 'exit',
            },
            'terminal-code-math': {
                'render-inline-math', 'render-csharp', 'render-javascript', 'render-python-json',
                'render-bash-fallback', 'render-block-math', 'render-math-table',
                'render-code-math', 'commit-code-math', 'exit',
            },
            'terminal-tool-lifecycle': {
                'tool-call-start', 'tool-output-in-flight', 'tool-result-and-follow-up', 'expand-tool-result',
            },
            'terminal-long-transcript': {
                'page-up', 'search-open', 'search-query', 'search-cursor-left', 'scroll-to-bottom', 'exit',
            },
            'terminal-resize-during-stream': {'resize-during-stream', 'commit-answer', 'exit'},
            'terminal-small-overlay': {'model-picker-open', 'model-picker-dismiss', 'quit'},
            'terminal-suspend-resume': {'draft', 'suspended', 'resumed', 'edited-after-resume', 'quit'},
            'startup-editor': {
                'type-h', 'type-d', 'cursor-left', 'paste', 'clear', 'resize', 'quit',
            },
            'terminal-mouse-selection': {'response-in-flight', 'selection-drag', 'wheel-up', 'wheel-down', 'exit'},
            'terminal-mouse-multiclick': {'double-click-word', 'triple-click-line', 'double-click-drag', 'exit'},
            'terminal-image-fallback': {'read-image', 'expand-image-result', 'exit'},
            'terminal-restoration-existing-main-buffer': {
                'native-ui-startup', 'native-ui-widget', 'native-ui-input-update', 'native-ui-quit',
            },
            'native-extension-interactive-ui': {
                'native-ui-startup', 'native-ui-widget', 'native-ui-input-update', 'native-ui-quit',
            },
        }
        fixture_directory = Path(__file__).parent / 'fixtures'

        for fixture_id, expected_checkpoints in required.items():
            with self.subTest(fixture=fixture_id):
                fixture = json.loads((fixture_directory / f'{fixture_id}.json').read_text())
                actions = {action['id'] for action in fixture['actions']}
                checkpoints = set(fixture.get('assertionFrames', []))

                self.assertTrue(expected_checkpoints <= actions)
                self.assertTrue(expected_checkpoints <= checkpoints)
                if fixture_id == 'terminal-streaming-markdown':
                    exit_action = next(action for action in fixture['actions'] if action['id'] == 'exit')
                    self.assertEqual('\u0003\u0003', exit_action.get('send'))
                    self.assertTrue(exit_action.get('exit'))
                if fixture_id == 'terminal-mouse-selection':
                    response_action = next(action for action in fixture['actions']
                                           if action['id'] == 'response-in-flight')
                    self.assertEqual('0.0%/8.2k', response_action.get('notExpect'))
                    self.assertTrue(response_action.get('renderMatch'))
                    selection = next(action for action in fixture['actions'] if action['id'] == 'selection-drag')
                    self.assertTrue(selection.get('expectClipboardWrite'))
                    self.assertEqual(['Log section 09: Mouse selec'], selection.get('expectClipboardTexts'))
                if fixture_id == 'terminal-mouse-multiclick':
                    for action in fixture['actions']:
                        if action['id'] in {'double-click-word', 'triple-click-line', 'double-click-drag'}:
                            self.assertTrue(action.get('expectClipboardWrite'))
                            self.assertTrue(action.get('expectClipboardTexts'))
                            self.assertTrue(action.get('renderMatch'))
                if fixture_id == 'terminal-long-transcript':
                    cursor_action = next(action for action in fixture['actions']
                                         if action['id'] == 'search-cursor-left')
                    self.assertTrue(cursor_action.get('expectCursorChange'))

    def test_settle_waits_for_an_expected_hidden_cursor_change(self):
        class ScriptedTerminal:
            def __init__(self):
                self.frames = [
                    dict(viewport=[dict(cells=[dict(chars='READY')])],
                         cursor=dict(x=10, y=2), modes=dict(private={'2026': False})),
                    dict(viewport=[dict(cells=[dict(chars='READY')])],
                         cursor=dict(x=9, y=2), modes=dict(private={'2026': False})),
                ]
                self.current = self.frames.pop(0)
                self._new_render_frames = []
                self.raw = bytearray(b'rendered')
                self.closed = False
                self.last_output = time.monotonic() - 1

            def pump(self, timeout=0):
                if self.frames:
                    self.current = self.frames.pop(0)
                self._new_render_frames = [self.current]
                self.last_output = time.monotonic() - 1

            def snapshot(self):
                return self.current

        terminal = ScriptedTerminal()
        frame = TerminalProcess.settle(terminal, contains='READY', render_match=True,
                                       cursor_before=dict(x=10, y=2))

        self.assertEqual(dict(x=9, y=2), frame['cursor'])

    def test_current_pin_launches_the_normal_coding_agent_cli_with_fixture_argument_overrides(self):
        for scenario in ({}, {'arguments': {'pi': ['--no-session']}}, {'mockHuggingFace': True}):
            command = commands(Path('/current-pi'), Path('/pisharp.dll'), scenario)['pi']
            self.assertIn('/current-pi/packages/coding-agent/src/cli.ts', command)
            self.assertEqual(0 if scenario.get('arguments', {}).get('pi') else 1,
                             command.count('--no-context-files'))
            self.assertNotIn('/current-pi/packages/coding-agent/src/experimental/cli.ts', command)
            self.assertIn('file:///current-pi/packages/coding-agent/src/experimental/source-resolver.ts', command)

            pisharp_command = commands(Path('/current-pi'), Path('/pisharp.dll'), scenario)['pisharp']
            self.assertEqual(1, pisharp_command.count('--no-context-files'))

    def test_product_argument_overrides_preserve_each_cli_executable_prefix(self):
        result = commands(Path('/current-pi'), Path('/pisharp.dll'), {
            'arguments': {
                'pi': ['--session', '{agent}/fixtures/transcript.jsonl'],
                'pisharp': ['--session', '{agent}/fixtures/transcript.jsonl', '--no-context-files'],
            },
        })

        self.assertEqual('node', result['pi'][0])
        self.assertEqual('/current-pi/packages/coding-agent/src/cli.ts', result['pi'][3])
        self.assertEqual(['--session', '{agent}/fixtures/transcript.jsonl'], result['pi'][-2:])
        self.assertEqual(['dotnet', '/pisharp.dll'], result['pisharp'][:2])
        self.assertEqual(['--session', '{agent}/fixtures/transcript.jsonl', '--no-context-files'],
                         result['pisharp'][-3:])

    def test_terminal_prelude_is_written_before_both_real_cli_processes(self):
        result = commands(Path('/current-pi'), Path('/pisharp.dll'),
                          {'terminalPrelude': 'existing shell history\n'})
        for product in ('pi', 'pisharp'):
            self.assertEqual('sh', result[product][0])
            self.assertEqual('printf %s "$1"; shift; exec "$@"', result[product][2])
            self.assertEqual('existing shell history\n', result[product][4])
            self.assertIn('/current-pi/packages/coding-agent/src/cli.ts' if product == 'pi'
                          else '/pisharp.dll', result[product])

    def test_scenario_files_can_write_validated_base64_assets(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            write_scenario_files({'fixture.bin': {'$base64': 'AAECAw=='}}, root)
            self.assertEqual(bytes((0, 1, 2, 3)), (root / 'fixture.bin').read_bytes())

            with self.assertRaises(ValueError):
                write_scenario_files({'invalid.bin': {'$base64': 'not base64!'}}, root)

    def test_fixture_files_expand_the_selected_workspace_path(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            workspace = root / 'workspace'
            agent = root / 'agent'
            with patch('run.WORKSPACE', workspace):
                write_scenario_files({'fixtures/long.jsonl': '{"cwd":"{workspace}"}'}, agent)
            self.assertEqual(f'{{"cwd":"{workspace}"}}', (agent / 'fixtures/long.jsonl').read_text())

    def test_http_comparison_normalizes_tool_argument_json_without_hiding_value_changes(self):
        compact = [{'body': {'messages': [{'role': 'assistant', 'tool_calls': [{'function': {
            'arguments': '{"path":"fixture-image.png"}'}}]}]}}]
        formatted = [{'body': {'messages': [{'role': 'assistant', 'tool_calls': [{'function': {
            'arguments': '{\n  "path": "fixture-image.png"\n}'}}]}]}}]
        changed = [{'body': {'messages': [{'role': 'assistant', 'tool_calls': [{'function': {
            'arguments': '{"path":"other-image.png"}'}}]}]}}]

        self.assertEqual(canonicalize_tool_call_arguments_in_requests(compact),
                         canonicalize_tool_call_arguments_in_requests(formatted))
        self.assertNotEqual(canonicalize_tool_call_arguments_in_requests(compact),
                            canonicalize_tool_call_arguments_in_requests(changed))

    def test_osc52_clipboard_evidence_decodes_selection_text_and_compares_exactly(self):
        raw = b'prefix\x1b]52;c;TG9nIHNlY3Rpb24gMDc=\x07middle\x1b]52;c;Y29weSB0ZXh0\x1b\\'
        expected = [
            dict(payloadBase64='TG9nIHNlY3Rpb24gMDc=', text='Log section 07'),
            dict(payloadBase64='Y29weSB0ZXh0', text='copy text'),
        ]
        self.assertEqual(expected, osc52_clipboard_writes(raw))

        left = dict(frames=[], renders=[], raw='', http=[], clipboardByFrame=[
            dict(id='selection-drag', writes=expected)])
        right = dict(frames=[], renders=[], raw='', http=[], clipboardByFrame=[
            dict(id='selection-drag', writes=[expected[0]])])
        comparison = compare_products(left, right)
        self.assertFalse(comparison['clipboardMatch'])
        self.assertIn('$.clipboardByFrame[0].writes.length',
                      {item['path'] for item in comparison['clipboardDifferences']})

    def test_emulator_shutdown_drains_a_snapshot_larger_than_the_pipe(self):
        terminal = TerminalProcess([sys.executable, '-c', 'import time; time.sleep(30)'],
                                   Path('/tmp'), dict(os.environ), dict(columns=120, rows=40),
                                   spool_render_frames=True)
        try:
            terminal.emulator.stdin.write(json.dumps(dict(op='snapshot')) + '\n')
            terminal.emulator.stdin.flush()
            terminal.close()
            self.assertEqual(0, terminal.emulator.returncode)
            self.assertTrue(terminal.emulator.stdout.closed)
        finally:
            if terminal.emulator.poll() is None:
                terminal.emulator.kill()
                terminal.emulator.wait(timeout=5)
            terminal.render_frames.close()

    def test_session_imports_use_the_owned_agent_directory(self):
        child_environment = environment(Path('/tmp/parity-agent'), persist_sessions=True)
        self.assertEqual('/tmp/parity-agent/sessions', child_environment['PISHARP_SESSION_DIR'])
        self.assertNotIn('PISHARP_SESSION_DIR', environment(Path('/tmp/parity-agent')))

    def test_assertion_evidence_omits_raw_and_render_streams(self):
        from run import write_assertion_evidence

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'capture.assertions.json.gz'
            products = {
                name: {
                    'frames': [{'id': 'ready', 'state': {'viewport': []}}],
                    'assertionFrames': [{'id': 'ready', 'state': {'viewport': []}}],
                    'http': [{'method': 'POST', 'path': '/v1/chat/completions'}],
                    'scenarioError': None,
                    'raw': 'large terminal byte stream',
                    'renders': object(),
                }
                for name in ('pi', 'pisharp')
            }

            write_assertion_evidence(path, products, dict(columns=120, rows=40), 'dark', 'fullscreen',
                                     frame_ids=['ready'])

            with gzip.open(path, 'rt', encoding='utf-8') as stream:
                evidence = json.load(stream)
            self.assertEqual(['ready'], [frame['id'] for frame in evidence['pi']['frames']])
            self.assertEqual('/v1/chat/completions', evidence['pisharp']['http'][0]['path'])
            self.assertNotIn('raw', evidence['pi'])
            self.assertNotIn('renders', evidence['pi'])

    def test_assertion_evidence_summarizes_working_usage_without_retaining_frames(self):
        from run import write_assertion_evidence

        def state(status, footer):
            lines = [dict(cells=[dict(chars=' ' * 120, width=1)]) for _ in range(40)]
            lines[35]['cells'] = [dict(chars=status, width=1)]
            lines[39]['cells'] = [dict(chars=footer, width=1)]
            return dict(rows=40, activeScreen='alternate', buffers=dict(alternate=dict(lines=lines)))

        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / 'capture.assertions.json.gz'
            products = {
                name: {
                    'frames': [],
                    'http': [],
                    'scenarioError': None,
                    'renders': [state('Working', '↑10 ↓3 0.2%/8.2k'), state('Idle', '↑13 ↓3')],
                }
                for name in ('pi', 'pisharp')
            }

            write_assertion_evidence(path, products, dict(columns=120, rows=40), 'dark', 'fullscreen',
                                     include_working_indicator=True)

            with gzip.open(path, 'rt', encoding='utf-8') as stream:
                evidence = json.load(stream)
            self.assertEqual(1, evidence['pi']['workingIndicator']['workingRenderFrames'])
            self.assertEqual(['↑10 ↓3 0.2%/8.2k'], evidence['pisharp']['workingIndicator']['prematureUsage'])
            self.assertNotIn('renders', evidence['pi'])

    def test_runner_and_child_environment_are_limited_to_two_threads(self):
        self.assertEqual(2, TEST_THREAD_LIMIT)
        expected = thread_limit_environment()
        self.assertEqual(expected, {name: '2' for name in expected})
        child_environment = environment(Path('/tmp/parity-agent'))
        self.assertTrue(all(child_environment[name] == '2' for name in expected))
        if hasattr(os, 'sched_getaffinity'):
            self.assertLessEqual(len(os.sched_getaffinity(0)), TEST_THREAD_LIMIT)
            child = subprocess.run(
                [sys.executable, '-c',
                 'import json, os; print(json.dumps({"affinity": len(os.sched_getaffinity(0)), '
                 '"cpuCount": os.process_cpu_count() if hasattr(os, "process_cpu_count") else None}))'],
                check=True, capture_output=True, text=True, env=child_environment)
            child_limits = json.loads(child.stdout)
            self.assertLessEqual(child_limits['affinity'], TEST_THREAD_LIMIT)
            if child_limits['cpuCount'] is not None:
                self.assertLessEqual(child_limits['cpuCount'], TEST_THREAD_LIMIT)

    @staticmethod
    def read_sse_event(stream):
        while True:
            line = stream.readline().decode()
            if line.startswith('data: '):
                return json.loads(line.removeprefix('data: '))

    def test_workspace_fixture_files_are_reset_and_contained(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / 'workspace'
            workspace.mkdir()
            (workspace / '.pisharp-terminal-harness').touch()
            (workspace / 'stale.txt').write_text('stale')

            working = prepare_workspace({
                'workingDirectory': 'trust-project',
                'projectFiles': {'.pi/settings.json': '{}'},
            }, workspace)

            self.assertEqual(workspace / 'trust-project', working)
            self.assertFalse((workspace / 'stale.txt').exists())
            self.assertEqual('{}', (workspace / '.pi/settings.json').read_text())
            with self.assertRaises(ValueError):
                prepare_workspace({'workingDirectory': '../outside'}, workspace)

    def test_default_workspace_uses_the_selected_fixture_root_at_execution_time(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / 'workspace'
            with patch('run.WORKSPACE', workspace):
                working = prepare_workspace({'workingDirectory': 'project'})
            self.assertEqual(workspace / 'project', working)
            self.assertTrue((workspace / '.pisharp-terminal-harness').is_file())

    def test_fixture_can_load_a_native_extension_only_in_the_pisharp_process(self):
        with tempfile.TemporaryDirectory() as temporary:
            extension = Path(temporary) / 'dynamic-resources.dll'
            extension.touch()
            scenario = {
                'pisharpExtension': True,
                'arguments': {
                    'pi': ['--no-extensions', '--extension', 'dynamic-resources.ts'],
                    'pisharp': ['--no-extensions'],
                },
            }

            result = commands(Path('/current-pi'), Path('/pisharp.dll'), scenario, extension)

            self.assertEqual(['--extension', str(extension.resolve())], result['pisharp'][-2:])
            self.assertNotIn(str(extension.resolve()), result['pi'])
            with self.assertRaises(ValueError):
                commands(Path('/current-pi'), Path('/pisharp.dll'), scenario)

    def test_fixture_stages_compiled_native_extension_as_a_manifest_package(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            agent = root / 'agent'
            agent.mkdir()
            extension = root / 'NativeFixture.dll'
            extension.write_bytes(b'compiled-extension')
            extension.with_suffix('.deps.json').write_text('{"runtimeTarget":{}}')
            scenario = {
                'pisharpExtension': True,
                'pisharpNativeExtensionPackage': {
                    'id': 'Example.Native-Fixture',
                    'version': '1.0.0',
                    'type': 'Example.NativeFixture.EntryPoint',
                },
            }
            (agent / 'settings.json').write_text('{"theme":"dark"}')

            stage_native_extension_package(agent, scenario, extension)

            package_root = agent / 'native-extensions' / 'example.native-fixture' / '1.0.0'
            manifest = json.loads((package_root / 'pisharp-extension.json').read_text())
            settings = json.loads((agent / 'settings.json').read_text())
            self.assertEqual('Example.Native-Fixture', manifest['id'])
            self.assertEqual('Example.NativeFixture.EntryPoint', manifest['extensions'][0]['type'])
            self.assertEqual(b'compiled-extension', (package_root / 'lib/net10.0/NativeFixture.dll').read_bytes())
            self.assertEqual(1, len(settings['nativeExtensions']))
            self.assertTrue(settings['nativeExtensions'][0]['source'].endswith('.nupkg'))
            self.assertTrue(Path(settings['nativeExtensions'][0]['source']).is_file())

            command = commands(Path('/current-pi'), Path('/pisharp.dll'), scenario, extension)['pisharp']
            self.assertNotIn('--extension', command)

            unsafe = copy.deepcopy(scenario)
            unsafe['pisharpNativeExtensionPackage']['id'] = '../outside'
            with self.assertRaises(ValueError):
                stage_native_extension_package(agent, unsafe, extension)

    def test_runner_restarts_keep_render_frame_spooling_enabled(self):
        from run import start_terminal_process

        command = ['dotnet', 'PiSharp.Cli.dll']
        cwd = Path('/tmp/pisharp-fixture')
        child_environment = {'PATH': '/usr/bin'}
        options = dict(columns=100, rows=32)
        with patch('run.TerminalProcess') as terminal_process:
            start_terminal_process(command, cwd, child_environment, options)

        terminal_process.assert_called_once_with(command, cwd, child_environment, options,
                                                 spool_render_frames=True)

    def test_fixture_file_updates_are_contained_to_the_owned_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'owned'
            root.mkdir()
            write_scenario_files({'project/.pi/settings.json': {'defaultTools': ['bash']}}, root)
            self.assertEqual({'defaultTools': ['bash']},
                             json.loads((root / 'project/.pi/settings.json').read_text()))
            with self.assertRaises(ValueError):
                write_scenario_files({'../outside.json': '{}'}, root)
            outside = Path(temporary) / 'outside'
            outside.mkdir()
            (root / 'escape').symlink_to(outside, target_is_directory=True)
            with self.assertRaises(ValueError):
                write_scenario_files({'escape/settings.json': '{}'}, root)

    def test_fixture_server_returns_and_records_chat_completion_requests(self):
        server = FixtureServer()
        body = dict(model='fixture-model', stream=True, tools=[dict(type='function', function=dict(name='read'))])
        stream = None
        try:
            request = urllib.request.Request(
                server.url + '/v1/chat/completions', data=json.dumps(body).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            stream = urllib.request.urlopen(request, timeout=2)
            first = self.read_sse_event(stream)
            second = self.read_sse_event(stream)
            separator = stream.readline().decode().strip()
            done = stream.readline().decode().strip() if not separator else separator
            self.assertEqual('fixture reply', first['choices'][0]['delta']['content'])
            self.assertEqual('stop', second['choices'][0]['finish_reason'])
            self.assertEqual('data: [DONE]', done)
            self.assertEqual(body, server.requests[0]['body'])
            self.assertEqual('/v1/chat/completions', server.requests[0]['path'])
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_fixture_server_streams_reasoning_before_chat_content(self):
        server = FixtureServer(behavior=dict(chatResponseReasoningByRequest=['private reasoning'],
                                             chatResponseContentByRequest=['fixture answer']))
        stream = None
        try:
            request = urllib.request.Request(
                server.url + '/v1/chat/completions', data=json.dumps(dict(stream=True)).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            stream = urllib.request.urlopen(request, timeout=2)
            reasoning = self.read_sse_event(stream)
            answer = self.read_sse_event(stream)
            finished = self.read_sse_event(stream)
            separator = stream.readline().decode().strip()
            done = stream.readline().decode().strip() if not separator else separator
            self.assertEqual('private reasoning', reasoning['choices'][0]['delta']['reasoning_content'])
            self.assertEqual('fixture answer', answer['choices'][0]['delta']['content'])
            self.assertEqual('stop', finished['choices'][0]['finish_reason'])
            self.assertEqual('data: [DONE]', done)
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_fixture_server_streams_gated_content_chunks_in_declared_order(self):
        server = FixtureServer(behavior=dict(
            chatResponseChunksByRequest=[['first partial', 'second partial']],
            chatResponseChunkGatesByRequest=[[None, 'release-second-chunk']],
            chatResponseChunkReadyGatesByRequest=[['first-chunk-sent', 'second-chunk-sent']]))
        stream = None
        try:
            request = urllib.request.Request(
                server.url + '/v1/chat/completions', data=json.dumps(dict(stream=True)).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            stream = urllib.request.urlopen(request, timeout=2)
            self.assertTrue(server.wait_for_gate('first-chunk-sent', timeout=1))
            first = self.read_sse_event(stream)
            self.assertEqual('first partial', first['choices'][0]['delta']['content'])
            self.assertFalse(server.wait_for_gate('second-chunk-sent', timeout=0.05))
            server.release_gate('release-second-chunk')
            second = self.read_sse_event(stream)
            self.assertEqual('second partial', second['choices'][0]['delta']['content'])
            self.assertTrue(server.wait_for_gate('second-chunk-sent', timeout=1))
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_fixture_server_streams_openai_tool_calls_for_agent_continuation(self):
        server = FixtureServer(behavior=dict(chatToolCallsByRequest=[[
            dict(id='call-fixture-tool', name='bash', arguments={'command': 'printf tool-output'})]]))
        stream = None
        try:
            request = urllib.request.Request(
                server.url + '/v1/chat/completions', data=json.dumps(dict(stream=True)).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            stream = urllib.request.urlopen(request, timeout=2)
            call = self.read_sse_event(stream)
            finish = self.read_sse_event(stream)
            separator = stream.readline().decode().strip()
            done = stream.readline().decode().strip() if not separator else separator
            delta = call['choices'][0]['delta']['tool_calls'][0]
            self.assertEqual('bash', delta['function']['name'])
            self.assertEqual('{"command":"printf tool-output"}', delta['function']['arguments'])
            self.assertEqual('tool_calls', finish['choices'][0]['finish_reason'])
            self.assertEqual('data: [DONE]', done)
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_fixture_server_gates_stream_content_and_finish_separately(self):
        server = FixtureServer(behavior=dict(chatRequestGates=['chat-request'],
                                             chatResponseContentGates=['chat-content'],
                                             chatResponseFinishGates=['chat-finish'],
                                             chatResponseContentByRequest=['unique fixture reply']))
        stream = None
        try:
            request = urllib.request.Request(
                server.url + '/v1/chat/completions', data=json.dumps(dict(stream=True)).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            stream = urllib.request.urlopen(request, timeout=2)
            self.assertTrue(server.wait_for_gate('chat-request', timeout=1))
            server.release_gate('chat-content')
            first = self.read_sse_event(stream)
            self.assertEqual('unique fixture reply', first['choices'][0]['delta']['content'])
            server.release_gate('chat-finish')
            second = self.read_sse_event(stream)
            separator = stream.readline().decode().strip()
            done = stream.readline().decode().strip() if not separator else separator
            self.assertEqual('stop', second['choices'][0]['finish_reason'])
            self.assertEqual('data: [DONE]', done)
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_chat_completion_request_gate_holds_the_response_until_release(self):
        server = FixtureServer(behavior=dict(chatRequestGates=['chat-request'],
                                             chatResponseGates=['chat-response']))
        completed = Event()
        result = []

        def request_chat_completion():
            try:
                request = urllib.request.Request(
                    server.url + '/v1/chat/completions', data=json.dumps(dict(stream=False)).encode(),
                    headers={'Content-Type': 'application/json'}, method='POST')
                with urllib.request.urlopen(request, timeout=3) as response:
                    result.append(json.loads(response.read()))
            finally:
                completed.set()

        thread = Thread(target=request_chat_completion)
        try:
            thread.start()
            self.assertTrue(server.wait_for_gate('chat-request', timeout=2))
            self.assertFalse(completed.is_set())
            self.assertEqual(1, server.chat_request_count)

            server.release_gate('chat-response')
            thread.join(timeout=3)

            self.assertFalse(thread.is_alive())
            self.assertEqual('fixture reply', result[0]['choices'][0]['message']['content'])
        finally:
            server.release_gate('chat-response')
            thread.join(timeout=3)
            server.close()

    def test_fixture_reset_waits_for_disconnected_sse_clients(self):
        server = FixtureServer(behavior=dict(downloadFinishGate='finish'))
        stream = None
        try:
            stream = urllib.request.urlopen(server.url + '/models/sse', timeout=2)
            stream.close()
            stream = None

            self.assertTrue(server.wait_for_sse_streams(2))
            server.release_gate('finish')
            self.assertTrue(server.gate_released('finish'))
            server.reset()
            self.assertEqual(0, server.active_sse_streams)
            self.assertFalse(server.gate_released('finish'))
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_terminal_comparison_preserves_global_stream_across_checkpoint_boundaries(self):
        state = dict(viewport=[dict(text='ready')], cursor=dict(x=1, y=0, visible=False))
        events = ['1b5b3f3230323668', '1b5b323b3148', '1b5b3f323032366c']
        left = dict(frames=[
            dict(id='first', state=state, controls=events[:2]),
            dict(id='second', state=state, controls=events[2:]),
        ], raw='AQID', http=[dict(method='GET', path='/models')])
        right = dict(frames=[
            dict(id='first', state=state, controls=events[:1]),
            dict(id='second', state=state, controls=events[1:]),
        ], raw='AQID', http=[dict(method='GET', path='/models')])

        result = compare_products(left, right)

        self.assertTrue(result['match'])
        self.assertTrue(result['rawMatch'])
        self.assertTrue(result['httpMatch'])
        self.assertGreater(result['controlBoundaryDifferenceCount'], 0)
        self.assertEqual(events, [event for frame in left['frames'] for event in frame['controls']])
        self.assertEqual(events, [event for frame in right['frames'] for event in frame['controls']])

    def test_terminal_comparison_reports_raw_difference_separately_from_state_and_requests(self):
        state = dict(viewport=[dict(text='ready')])
        frame = dict(id='ready', state=state, controls=[])
        left = dict(frames=[frame], renders=[state], raw='AQID', http=[dict(method='GET', path='/models'),
                                                                        dict(method='GET', path='/props')])
        changed = dict(frames=[frame], renders=[state], raw='AQIE', http=[dict(method='GET', path='/props'),
                                                                           dict(method='GET', path='/models')])

        result = compare_products(left, changed)

        self.assertFalse(result['match'])
        self.assertFalse(result['rawMatch'])
        self.assertFalse(result['httpMatch'])
        self.assertTrue(result['terminalMatch'])
        self.assertEqual(dict(piBytes=3, pisharpBytes=3, firstDifferingByte=2), result['rawDifference'])
        self.assertNotIn('$.raw', {item['path'] for item in result['differences']})
        self.assertIn('$.http[0].path', {item['path'] for item in result['differences']})

    def test_request_comparison_canonicalizes_only_generated_documentation_install_paths(self):
        def product(package_root, user_text=None):
            messages = [dict(role='system', content=(
                f'- Main documentation: {package_root}/README.md\n'
                f'- Additional docs: {package_root}/docs\n'
                f'- Examples: {package_root}/examples'))]
            if user_text is not None:
                messages.append(dict(role='user', content=user_text))
            return dict(http=[dict(method='POST', path='/v1/chat/completions', body=dict(messages=messages))])

        equivalent = compare_products(product('/pi/package'), product('/pisharp/package'))

        self.assertTrue(equivalent['httpMatch'])
        self.assertFalse(equivalent['rawHttpMatch'])
        self.assertEqual(1, equivalent['rawHttpDifferenceCount'])
        self.assertEqual('$.http[0].body.messages[0].content',
                         equivalent['rawHttpFirstDifferences'][0]['path'])
        self.assertTrue(equivalent['match'])
        self.assertIn('package-root', equivalent['httpNormalization'])

        differing_user_path = compare_products(
            product('/pi/package', '- Main documentation: /pi/user-content'),
            product('/pisharp/package', '- Main documentation: /pisharp/user-content'))

        self.assertFalse(differing_user_path['httpMatch'])
        self.assertEqual(['$.http[0].body.messages[1].content'],
                         [item['path'] for item in differing_user_path['httpDifferences']])

    def test_raw_redraw_scheduling_does_not_mask_exact_rendered_state_comparison(self):
        state = dict(activeScreen='alternate', viewport=[dict(text='same')],
                     cursor=dict(x=4, y=2, visible=False, style='block', blink=False),
                     buffers=dict(alternate=dict(cursorX=4, cursorY=2)))
        shifted_hidden_cursor = copy.deepcopy(state)
        shifted_hidden_cursor['cursor'].update(x=7, y=5)
        shifted_hidden_cursor['buffers']['alternate'].update(cursorX=7, cursorY=5)
        frame = dict(id='ready', state=state)
        other_frame = dict(id='ready', state=shifted_hidden_cursor)
        left = dict(frames=[frame], renders=[state], raw='AQID', http=[])
        right = dict(frames=[other_frame], renders=[shifted_hidden_cursor], raw='AQIE', http=[])

        result = compare_products(left, right)

        self.assertTrue(result['match'])
        self.assertTrue(result['terminalMatch'])
        self.assertFalse(result['rawMatch'])
        self.assertEqual([], result['renderDifferences'])

        changed_visibility = copy.deepcopy(shifted_hidden_cursor)
        changed_visibility['cursor']['visible'] = True
        changed = dict(frames=[dict(id='ready', state=changed_visibility)],
                       renders=[changed_visibility], raw='AQIE', http=[])
        rejected = compare_products(left, changed)
        self.assertFalse(rejected['terminalMatch'])
        self.assertIn('$.renders[0].cursor.visible', {item['path'] for item in rejected['renderDifferences']})

    def test_terminal_comparison_ignores_only_bottom_status_loader_frame_changes(self):
        def state(spinner, row=35):
            lines = [dict(cells=[dict(chars=' ') for _ in range(8)]) for _ in range(40)]
            lines[row]['cells'][3]['chars'] = spinner
            return dict(rows=40, viewport=copy.deepcopy(lines),
                        buffers=dict(alternate=dict(lines=copy.deepcopy(lines))))

        first = state('⠋')
        next_frame = state('⠙')
        left = dict(frames=[dict(id='loading', state=first)], renders=[first], raw='', http=[])
        right = dict(frames=[dict(id='loading', state=next_frame)], renders=[next_frame], raw='', http=[])

        result = compare_products(left, right)

        self.assertTrue(result['terminalMatch'])
        self.assertEqual([], result['renderDifferences'])

        changed_content = state('⠙', row=10)
        rejected = compare_products(left, dict(frames=[dict(id='loading', state=changed_content)],
                                             renders=[changed_content], raw='', http=[]))
        self.assertFalse(rejected['terminalMatch'])
        self.assertTrue(rejected['renderDifferences'])

    def test_intermediate_render_transitions_are_compared_and_only_adjacent_duplicates_collapse(self):
        first = dict(viewport=[dict(text='loading')])
        second = dict(viewport=[dict(text='50%')])
        third = dict(viewport=[dict(text='finished')])
        self.assertEqual([first, second], canonical_render_frames([first, first, second, second]))
        frame = dict(id='finished', state=third)
        pi = dict(frames=[frame], renders=[first, second, third], raw='', http=[])
        pisharp = dict(frames=[frame], renders=[first, third], raw='', http=[])

        result = compare_products(pi, pisharp)

        self.assertFalse(result['match'])
        self.assertFalse(result['terminalMatch'])
        self.assertEqual(3, result['comparedRenderFrameCount'])
        self.assertEqual(1, result['renderDifferenceCount'])
        self.assertEqual([dict(pi=0, pisharp=0), dict(pi=2, pisharp=1)],
                         result['renderAlignment']['exactMatches'])
        self.assertEqual([1], result['renderAlignment']['piOnlyRenderFrames'])
        self.assertEqual([], result['renderAlignment']['pisharpOnlyRenderFrames'])
        self.assertIn('$.renders.pi[1]', {item['path'] for item in result['renderDifferences']})

    def test_render_sequence_alignment_retains_style_differences_between_exact_states(self):
        first = dict(viewport=[dict(text='same', style='normal')])
        pi_middle = dict(viewport=[dict(text='selected', style='inverse')])
        pisharp_middle = dict(viewport=[dict(text='selected', style='normal')])
        last = dict(viewport=[dict(text='done', style='normal')])

        result = compare_products(
            dict(frames=[], renders=[first, pi_middle, last], raw='', http=[]),
            dict(frames=[], renders=[first, pisharp_middle, last], raw='', http=[]))

        self.assertFalse(result['terminalMatch'])
        self.assertEqual([dict(pi=0, pisharp=0), dict(pi=2, pisharp=2)],
                         result['renderAlignment']['exactMatches'])
        self.assertEqual(1, result['renderDifferenceCount'])
        self.assertIn('$.renders[1].viewport[0].style',
                      {item['path'] for item in result['renderDifferences']})

    def test_working_indicator_render_is_calibrated_noise_but_settled_frames_stay_exact(self):
        def state(status, footer):
            lines = [dict(cells=[dict(chars=' ') for _ in range(120)]) for _ in range(40)]
            border = '─' * 120 if status is None else '── ⠋ Working ' + '─' * 106
            for line, text in ((35, border), (39, footer)):
                for index, character in enumerate(text[:120]):
                    lines[line]['cells'][index]['chars'] = character
            return dict(rows=40, columns=120, activeScreen='alternate', viewport=copy.deepcopy(lines),
                        buffers=dict(alternate=dict(lines=copy.deepcopy(lines))))

        settled = state(None, '↑13 ↓3 0.3%/8.2k (auto)')
        in_flight = state('Working', '↑10 ↓3 0.2%/8.2k (auto)')
        pi = dict(frames=[dict(id='reply', state=settled)], renders=[in_flight, settled], raw='', http=[])
        repeated_pi = dict(frames=[dict(id='reply', state=settled)], renders=[settled], raw='', http=[])

        self.assertTrue(compare_products(pi, repeated_pi)['match'])
        self.assertFalse(compare_products(
            dict(frames=[dict(id='reply', state=in_flight)], renders=[], raw='', http=[]),
            repeated_pi)['match'])

    def test_render_comparison_streams_all_frames_and_bounds_only_the_difference_sample(self):
        frame_count = 120
        left = dict(frames=[], renders=(dict(viewport=[dict(text=f'pi {index}')])
                                        for index in range(frame_count)), raw='', http=[])
        right = dict(frames=[], renders=(dict(viewport=[dict(text=f'pisharp {index}')])
                                         for index in range(frame_count)), raw='', http=[])

        result = compare_products(left, right)

        self.assertFalse(result['match'])
        self.assertEqual(frame_count, result['renderDifferenceCount'])
        self.assertEqual(frame_count, result['differenceCount'])
        self.assertEqual(100, len(result['renderDifferences']))
        self.assertEqual(frame_count, result['comparedRenderFrameCount'])

    def test_compressed_render_frame_sequence_replays_and_streams_as_json(self):
        first = RenderFrameStore()
        second = RenderFrameStore()
        frames = [dict(viewport=[dict(text='start')]), dict(viewport=[dict(text='finish')])]
        first.append(frames[0])
        second.append(frames[1])
        sequence = RenderFrameSequence(first, second)
        stream = io.StringIO()
        try:
            self.assertEqual(2, len(sequence))
            write_json_stream(stream, dict(renders=sequence))
            self.assertEqual({'renders': frames}, json.loads(stream.getvalue()))
            self.assertEqual(frames, list(sequence))
            with tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / 'renders.jsonl.gz'
                with path.open('wb') as output:
                    sequence.write_compressed_to(output)
                with gzip.open(path, 'rt', encoding='utf-8') as output:
                    self.assertEqual(frames, [json.loads(line) for line in output])
        finally:
            sequence.close()

    def test_product_comparison_streams_compressed_checkpoint_frames(self):
        pi = RenderFrameStore()
        pisharp = RenderFrameStore()
        self.addCleanup(pi.close)
        self.addCleanup(pisharp.close)
        for index in range(4):
            pi.append(dict(id=f'checkpoint-{index}', state=dict(viewport=[dict(text=f'row {index}')]), controls=[]))
            text = 'changed row' if index == 2 else f'row {index}'
            pisharp.append(dict(id=f'checkpoint-{index}', state=dict(viewport=[dict(text=text)]), controls=[]))

        result = compare_products(dict(frames=pi, renders=[], raw='', http=[]),
                                  dict(frames=pisharp, renders=[], raw='', http=[]))

        self.assertFalse(result['terminalMatch'])
        self.assertEqual(1, result['stateDifferenceCount'])
        self.assertEqual('$.frames[2].state.viewport[0].text', result['stateDifferences'][0]['path'])

    def test_product_comparison_preserves_order_around_declared_parallel_requests(self):
        state = dict(viewport=[dict(text='ready')])
        frame = dict(id='ready', state=state, controls=[])
        requests = [dict(method='GET', path='/models'),
                    dict(method='GET', path='/props?model=one'),
                    dict(method='GET', path='/props?model=two'),
                    dict(method='GET', path='/model/refresh')]
        reordered = [requests[0], requests[2], requests[1], requests[3]]
        group = [[dict(method='GET', path='/props?model=one'),
                  dict(method='GET', path='/props?model=two')]]
        left = dict(frames=[frame], raw='AQID', http=requests)
        right = dict(frames=[frame], raw='AQID', http=reordered)

        result = compare_products(left, right, group)
        self.assertTrue(result['match'])

        wrong_outer_order = dict(frames=[frame], raw='AQID',
                                 http=[requests[3], requests[2], requests[1], requests[0]])
        rejected = compare_products(left, wrong_outer_order, group)
        self.assertFalse(rejected['httpMatch'])
        self.assertIn('$.http[0].path', {item['path'] for item in rejected['differences']})

        duplicate = dict(frames=[frame], raw='AQID', http=[*reordered, requests[1]])
        rejected = compare_products(left, duplicate, group)
        self.assertFalse(rejected['httpMatch'])
        self.assertIn('$.http.length', {item['path'] for item in rejected['differences']})

    def test_llama_download_progress_waits_for_configured_deadline(self):
        server = FixtureServer(behavior=dict(downloadDelaySeconds=1, downloadProgressDelaySeconds=0.2))
        try:
            request = urllib.request.Request(
                server.url + '/models',
                data=json.dumps(dict(model='owner/model:Q4_K_M')).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            with urllib.request.urlopen(request, timeout=2):
                pass

            started = time.monotonic()
            with urllib.request.urlopen(server.url + '/models/sse', timeout=2) as response:
                event = response.readline().decode()

            self.assertGreaterEqual(time.monotonic() - started, 0.15)
            self.assertIn('"event": "download_progress"', event)
            self.assertIn('"total": 1024', event)
        finally:
            server.close()

    def test_llama_download_progress_and_completion_follow_fixture_events(self):
        server = FixtureServer(
            behavior=dict(downloadProgressGate='download-progress',
                          downloadFinishGate='download-finished',
                          downloadPollGate='download-poll'))
        stream = None
        try:
            stream = urllib.request.urlopen(server.url + '/models/sse', timeout=2)
            request = urllib.request.Request(
                server.url + '/models', data=json.dumps(dict(model='owner/model:Q4_K_M')).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            with urllib.request.urlopen(request, timeout=2):
                pass

            server.release_gate('download-progress')
            progress = self.read_sse_event(stream)
            self.assertEqual('download_progress', progress['event'])

            server.release_gate('download-finished')
            finished = self.read_sse_event(stream)
            self.assertEqual('download_finished', finished['event'])
            with urllib.request.urlopen(server.url + '/models', timeout=2) as response:
                model = json.loads(response.read())['data'][0]
            self.assertEqual('unloaded', model['status']['value'])
        finally:
            if stream is not None:
                stream.close()
            server.close()

    def test_llama_download_start_response_can_be_gated_after_request_arrival(self):
        server = FixtureServer(behavior=dict(downloadRequestGate='download-request',
                                             downloadResponseGate='download-response'))
        completed = Event()
        responses = []
        errors = []
        request = urllib.request.Request(
            server.url + '/models', data=json.dumps(dict(model='owner/model:Q4_K_M')).encode(),
            headers={'Content-Type': 'application/json'}, method='POST')

        def post_download():
            try:
                with urllib.request.urlopen(request, timeout=3) as response:
                    responses.append(json.loads(response.read()))
            except Exception as error:
                errors.append(error)
            finally:
                completed.set()

        try:
            Thread(target=post_download, daemon=True).start()
            self.assertTrue(server.wait_for_gate('download-request', 2),
                            'download request did not reach the fixture')
            self.assertFalse(completed.is_set(), 'download response escaped its fixture gate')
            server.release_gate('download-response')
            self.assertTrue(completed.wait(2), 'download response did not follow its fixture gate')
            if errors:
                raise errors[0]
            self.assertEqual([dict(success=True)], responses)
        finally:
            server.close()

    def test_llama_unload_cancellation_releases_poll_without_finishing_download(self):
        server = FixtureServer(models=[dict(id='loaded-model', status=dict(value='loaded'))],
                               behavior=dict(downloadProgressGate='download-progress',
                                             downloadFinishGate='download-finished',
                                             downloadPollGate='download-poll'))
        poll_result = []
        poll_errors = []
        poll_done = Event()
        try:
            request = urllib.request.Request(
                server.url + '/models/unload', data=json.dumps(dict(model='loaded-model')).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            with urllib.request.urlopen(request, timeout=2):
                pass
            self.assertFalse(server.gate_released('download-finished'))
            self.assertFalse(server.gate_released('download-poll'))

            request = urllib.request.Request(
                server.url + '/models', data=json.dumps(dict(model='owner/model:Q4_K_M')).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            with urllib.request.urlopen(request, timeout=2):
                pass
            with urllib.request.urlopen(server.url + '/models', timeout=2) as response:
                models = json.loads(response.read())['data']
            downloaded = next(model for model in models if model['id'] == 'owner/model:Q4_K_M')
            self.assertEqual('downloading', downloaded['status']['value'])

            def poll_catalog():
                try:
                    with urllib.request.urlopen(server.url + '/models', timeout=2) as response:
                        poll_result.extend(json.loads(response.read())['data'])
                except Exception as error:
                    poll_errors.append(error)
                finally:
                    poll_done.set()

            poll_thread = Thread(target=poll_catalog, daemon=True)
            poll_thread.start()
            self.assertTrue(server.wait_for_model_poll('owner/model:Q4_K_M', 2),
                            'catalog poll did not wait at its fixture gate')

            request = urllib.request.Request(
                server.url + '/models/unload', data=json.dumps(dict(model='owner/model:Q4_K_M')).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            with urllib.request.urlopen(request, timeout=2):
                pass
            self.assertTrue(poll_done.wait(2), 'cancellation did not release the catalog poll')
            poll_thread.join(timeout=2)

            self.assertEqual([], poll_errors)
            cancelled = next(model for model in poll_result if model['id'] == 'owner/model:Q4_K_M')
            self.assertEqual('unloaded', cancelled['status']['value'])
            self.assertFalse(server.gate_released('download-finished'))
            self.assertTrue(server.gate_released('download-poll'))
        finally:
            server.close()

    def test_llama_load_progress_waits_for_configured_deadline(self):
        server = FixtureServer(
            models=[dict(id='fixture-model', status=dict(value='unloaded'))],
            behavior=dict(loadDelaySeconds=1, loadProgressDelaySeconds=0.2))
        try:
            request = urllib.request.Request(
                server.url + '/models/load',
                data=json.dumps(dict(model='fixture-model')).encode(),
                headers={'Content-Type': 'application/json'}, method='POST')
            with urllib.request.urlopen(request, timeout=2):
                pass

            started = time.monotonic()
            with urllib.request.urlopen(server.url + '/models/sse', timeout=2) as response:
                event = response.readline().decode()

            self.assertGreaterEqual(time.monotonic() - started, 0.15)
            self.assertIn('"event": "status_change"', event)
            self.assertIn('"value": 0.5', event)
        finally:
            server.close()

    def test_llama_load_response_gate_holds_request_until_explicit_release(self):
        server = FixtureServer(
            models=[dict(id='fixture-model', status=dict(value='unloaded'))],
            behavior=dict(loadRequestGate='load-requested', loadResponseGate='load-response',
                          loadStartGate='load-started'))
        result = []
        errors = []
        done = Event()
        try:
            def load_model():
                try:
                    request = urllib.request.Request(
                        server.url + '/models/load', data=json.dumps(dict(model='fixture-model')).encode(),
                        headers={'Content-Type': 'application/json'}, method='POST')
                    with urllib.request.urlopen(request, timeout=3) as response:
                        result.append(json.loads(response.read()))
                except Exception as error:
                    errors.append(error)
                finally:
                    done.set()

            request_thread = Thread(target=load_model, daemon=True)
            request_thread.start()
            self.assertTrue(server.wait_for_gate('load-requested', timeout=2))
            self.assertFalse(done.is_set())
            self.assertFalse(server.gate_released('load-started'))

            server.release_gate('load-response')
            deadline = time.monotonic() + 2
            self.assertTrue(done.wait(max(0, deadline - time.monotonic())))
            self.assertTrue(server.wait_for_gate('load-started', timeout=max(0, deadline - time.monotonic())))
            request_thread.join(timeout=max(0, deadline - time.monotonic()))

            self.assertEqual([], errors)
            self.assertEqual([dict(success=True)], result)
            self.assertTrue(server.gate_released('load-started'))
        finally:
            server.close()

    def test_environment_placeholders_expand_router_and_agent_paths(self):
        expanded = expand_environment(
            {'HF_TOKEN_PATH': '{agent}/hf-token', 'LLAMA_BASE_URL': '{router}', 'COUNT': 3},
            Path('/tmp/parity-agent'), 'http://127.0.0.1:1234')
        self.assertEqual({'HF_TOKEN_PATH': '/tmp/parity-agent/hf-token',
                          'LLAMA_BASE_URL': 'http://127.0.0.1:1234', 'COUNT': 3}, expanded)

    def test_escape_sequences_are_independent_of_read_chunking(self):
        data = b' text \x1b[38;2;1;2;3mX\r\n\x1b]52;c;YWJj\x07\x1b_Ga=T;fixture\x1b\\'
        whole, split = ControlTrace(), ControlTrace()
        whole.write(data)
        for byte in data:
            split.write(bytes([byte]))
        self.assertEqual(whole.events, split.events)
        self.assertEqual(['1b5b33383b323b313b323b336d', '0d', '0a',
                          '1b5d35323b633b59574a6a07', '1b5f47613d543b666978747572651b5c'], whole.events)

    def test_unicode_and_c1_controls_keep_their_original_bytes(self):
        data = '界\x9b31mX\x9d52;c;YWJj\x9c'.encode()
        whole, split = ControlTrace(), ControlTrace()
        whole.write(data)
        for byte in data:
            split.write(bytes([byte]))
        self.assertEqual(whole.events, split.events)
        self.assertEqual(['\x9b31m'.encode().hex(), '\x9d52;c;YWJj\x9c'.encode().hex()], whole.events)

    def test_apc_and_dcs_payloads_are_preserved(self):
        trace = ControlTrace()
        trace.write(b'\x1bPqabc\x07def\x1b\\\x1b_Gpayload\x1b\\')
        self.assertEqual([b'\x1bPqabc\x07def\x1b\\'.hex(), b'\x1b_Gpayload\x1b\\'.hex()], trace.events)

    def test_comparison_reports_exact_nested_cell_and_mode_paths(self):
        original = dict(viewport=[dict(cells=[dict(chars=' ', foreground=dict(mode='rgb', value=1))])], cursor=dict(x=2), modes=dict(bracketedPasteMode=True))
        changed = copy.deepcopy(original)
        changed['viewport'][0]['cells'][0]['chars'] = ''
        changed['viewport'][0]['cells'][0]['foreground']['value'] = 2
        changed['cursor']['x'] = 3
        changed['modes']['bracketedPasteMode'] = False
        self.assertEqual({'$.viewport[0].cells[0].chars', '$.viewport[0].cells[0].foreground.value',
                          '$.cursor.x', '$.modes.bracketedPasteMode'}, {item['path'] for item in differences(original, changed)})
        self.assertEqual([], list(differences(original, copy.deepcopy(original))))

    def test_terminal_comparison_normalizes_empty_cell_glyphs_but_keeps_style_and_width_differences(self):
        def state(chars, foreground=1, width=1):
            return dict(rows=1, viewport=[dict(cells=[dict(chars=chars, width=width,
                foreground=dict(mode='rgb', value=foreground))])], buffers={})

        self.assertEqual(normalize_terminal_state(state('')),
                         normalize_terminal_state(state(' ')))
        self.assertNotEqual(normalize_terminal_state(state('', foreground=1)),
                            normalize_terminal_state(state(' ', foreground=2)))
        self.assertNotEqual(normalize_terminal_state(state('', width=1)),
                            normalize_terminal_state(state(' ', width=2)))

    def test_real_pty_input_resize_and_cursor_round_trip(self):
        child = '''import os, signal, tty
from shutil import get_terminal_size
tty.setraw(0)
def ready(*_):
    size = get_terminal_size()
    os.write(1, ("\\x1b[HREADY:%sx%s" % (size.columns, size.lines)).encode())
signal.signal(signal.SIGWINCH, ready)
ready()
while True:
    value = os.read(0, 1024)
    if not value: break
    os.write(1, b"\\x1b[2;3H" + value.upper())
'''
        terminal = TerminalProcess([sys.executable, '-c', child], Path('/tmp'), dict(os.environ), dict(columns=80, rows=24))
        try:
            frame = terminal.settle('READY:80x24')
            self.assertEqual(24, frame['rows'])
            mark = len(terminal.raw)
            terminal.send('abc')
            frame = terminal.settle(['ABC', 'READY:80x24'], after=mark, render_match=True)
            self.assertEqual(dict(x=5, y=1, visible=True, style='block', blink=False), frame['cursor'])
            terminal.resize(100, 30)
            frame = terminal.settle('READY:100x30')
            self.assertEqual((100, 30), (frame['columns'], frame['rows']))
        finally:
            terminal.close()

    def test_render_settle_does_not_return_mid_synchronized_update(self):
        terminal = object.__new__(TerminalProcess)
        partial = dict(modes=dict(private={'2026': True}), viewport=[dict(cells=[dict(chars='READY')])])
        complete = dict(modes=dict(private={'2026': False}), viewport=[dict(cells=[dict(chars='READY')])])
        terminal.render_frames = []
        terminal.raw = bytearray(b'new output')
        terminal.last_output = time.monotonic() - 1
        terminal.closed = False
        terminal.process = None
        calls = 0
        snapshots = []

        def pump(timeout=0.05):
            nonlocal calls
            calls += 1
            terminal._new_render_frames = []
            if calls == 2:
                terminal.render_frames.append(complete)
                terminal._new_render_frames = [complete]

        terminal.pump = pump
        terminal.snapshot = lambda: snapshots.append(partial) or partial

        frame = terminal.settle('READY', quiet_ms=0, render_match=True)

        self.assertIs(complete, frame)
        self.assertEqual(1, len(snapshots))

    def test_allow_static_frame_captures_gate_ready_screen_without_new_output(self):
        terminal = object.__new__(TerminalProcess)
        terminal.render_frames = []
        terminal._new_render_frames = []
        terminal.raw = bytearray()
        terminal.last_output = time.monotonic()
        terminal.closed = False
        terminal.process = None
        frame = dict(viewport=[dict(cells=[dict(chars='prompt accepted')])],
                     modes=dict(synchronizedOutput=False, private={'2026': False}))
        pump_timeouts = []
        terminal.pump = lambda timeout=0.05: pump_timeouts.append(timeout)
        terminal.snapshot = lambda: frame

        captured = terminal.settle('prompt accepted', allow_static_frame=True)

        self.assertIs(frame, captured)
        self.assertEqual([0], pump_timeouts)

    def test_settle_waits_until_a_transient_message_disappears(self):
        child = '''import os, time
os.write(1, b"\\x1b[1;1HStarting...\\x1b[K")
time.sleep(0.1)
os.write(1, b"\\x1b[1;1HLoading model\\x1b[K")
'''
        terminal = TerminalProcess([sys.executable, '-c', child], Path('/tmp'), dict(os.environ), dict(columns=40, rows=5))
        try:
            frame = terminal.settle('Loading model', absent='Starting...')
            text = '\n'.join(''.join(cell['chars'] for cell in row['cells'] if cell) for row in frame['viewport'])
            self.assertIn('Loading model', text)
            self.assertNotIn('Starting...', text)
        finally:
            terminal.close()


if __name__ == '__main__':
    unittest.main()
