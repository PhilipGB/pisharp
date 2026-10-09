import copy
import json
import os
from pathlib import Path
import sys
import time
from threading import Event, Thread
import tempfile
import urllib.request
import unittest

from compare import ControlTrace, canonical_render_frames, compare_products, differences
from fixture_http import FixtureServer
from pty_process import TerminalProcess
from run import commands, expand_environment, prepare_workspace, write_scenario_files


class DriverTests(unittest.TestCase):
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
        self.assertIn('$.renders[1].viewport[0].text', {item['path'] for item in result['renderDifferences']})

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
            self.assertTrue(done.wait(2))
            request_thread.join(timeout=2)

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
            frame = terminal.settle('ABC', after=mark)
            self.assertEqual(dict(x=5, y=1, visible=True, style='block', blink=False), frame['cursor'])
            terminal.resize(100, 30)
            frame = terminal.settle('READY:100x30')
            self.assertEqual((100, 30), (frame['columns'], frame['rows']))
        finally:
            terminal.close()

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
