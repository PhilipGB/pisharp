import base64
import errno
import fcntl
import json
import os
from pathlib import Path
import pty
import select
import signal
import struct
import subprocess
import sys
import termios
import time

from compare import ControlTrace


class TerminalProcess:
    def __init__(self, command, cwd, environment, options):
        self.render_frames = []
        self.emulator = subprocess.Popen(['node', str(Path(__file__).with_name('emulator.mjs'))],
                                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        self.ask(dict(op='start', options=options))
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', options['rows'], options['columns'], 0, 0))
        self.process = subprocess.Popen([sys.executable, __file__, '--exec-pty', *command],
                                        stdin=slave, stdout=slave, stderr=slave, cwd=cwd,
                                        env=environment, start_new_session=True)
        os.close(slave)
        os.set_blocking(self.master, False)
        self.raw = bytearray()
        self.trace = ControlTrace()
        self.closed = False
        self.last_output = time.monotonic()

    def ask(self, request):
        self.emulator.stdin.write(json.dumps(request) + '\n')
        self.emulator.stdin.flush()
        if not select.select([self.emulator.stdout], [], [], 5)[0]:
            raise TimeoutError('Terminal emulator did not answer')
        response = json.loads(self.emulator.stdout.readline())
        if 'error' in response:
            raise RuntimeError(response['error'])
        return response

    def pump(self, timeout=0.05):
        if self.closed or not select.select([self.master], [], [], timeout)[0]:
            return False
        try:
            data = os.read(self.master, 65536)
        except OSError as error:
            if error.errno != errno.EIO:
                raise
            data = b''
        if not data:
            self.closed = True
            return False
        self.raw.extend(data)
        self.trace.write(data)
        response = self.ask(dict(op='write', data=base64.b64encode(data).decode()))
        self.render_frames.extend(response.get('renderFrames', []))
        if response['replies']:
            self.send(base64.b64decode(response['replies']))
        self.last_output = time.monotonic()
        return True

    def send(self, data):
        view = memoryview(data if isinstance(data, bytes) else data.encode())
        while view:
            try:
                view = view[os.write(self.master, view):]
            except BlockingIOError:
                select.select([], [self.master], [], 1)

    def resize(self, columns, rows):
        self.ask(dict(op='resize', columns=columns, rows=rows))
        fcntl.ioctl(self.master, termios.TIOCSWINSZ, struct.pack('HHHH', rows, columns, 0, 0))
        self.last_output = time.monotonic()

    def snapshot(self):
        return self.ask(dict(op='snapshot'))['frame']

    def settle(self, contains=None, absent=None, timeout=20, quiet_ms=200, after=0):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.pump()
            if time.monotonic() - self.last_output < quiet_ms / 1000:
                continue
            frame = self.snapshot()
            text = '\n'.join(''.join(cell['chars'] for cell in line['cells'] if cell) for line in frame['viewport'])
            if (contains is None or contains in text) and (absent is None or absent not in text):
                if len(self.raw) > after or self.closed:
                    return frame
            if self.closed:
                raise RuntimeError(f'Terminal exited {self.process.poll()} before {contains!r}.\n{text}')
        raise TimeoutError(f'Terminal did not settle at {contains!r}.\n{text if "text" in locals() else ""}')

    def close(self):
        if self.process.poll() is None:
            os.killpg(self.process.pid, signal.SIGTERM)
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                os.killpg(self.process.pid, signal.SIGKILL)
                self.process.wait(timeout=3)
        os.close(self.master)
        self.emulator.stdin.close()
        self.emulator.wait(timeout=5)
        self.emulator.stdout.close()


if __name__ == '__main__' and sys.argv[1] == '--exec-pty':
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    os.execvpe(sys.argv[2], sys.argv[2:], os.environ)
