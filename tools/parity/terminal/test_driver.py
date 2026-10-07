import copy
import os
from pathlib import Path
import sys
import unittest

from compare import ControlTrace, differences
from pty_process import TerminalProcess
from run import expand_environment


class DriverTests(unittest.TestCase):
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


if __name__ == '__main__':
    unittest.main()
