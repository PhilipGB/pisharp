import base64
import errno
import fcntl
import gzip
import json
import os
from pathlib import Path
import pty
import select
import signal
import struct
import subprocess
import sys
import tempfile
import termios
import time

from compare import ControlTrace


class RenderFrameStore:
    """Replayable compressed JSONL spool for synchronized terminal snapshots."""

    def __init__(self):
        self._file = tempfile.TemporaryFile(mode='w+b')
        self._writer = gzip.GzipFile(fileobj=self._file, mode='wb', compresslevel=1, mtime=0)
        self._count = 0
        self._finished = False

    def append(self, frame):
        if self._finished:
            raise RuntimeError('Cannot append to a finished render-frame store')
        self._writer.write(json.dumps(frame, ensure_ascii=False, separators=(',', ':')).encode('utf-8'))
        self._writer.write(b'\n')
        self._count += 1

    def extend(self, frames):
        for frame in frames:
            self.append(frame)

    def finish(self):
        if not self._finished:
            self._writer.close()
            self._finished = True

    def __len__(self):
        return self._count

    def __iter__(self):
        self.finish()
        self._file.seek(0)
        with gzip.GzipFile(fileobj=self._file, mode='rb') as reader:
            for line in reader:
                yield json.loads(line)

    def close(self):
        self.finish()
        self._file.close()


class RenderFrameSequence:
    """Chain frame stores without joining their contents in memory."""

    def __init__(self, *stores):
        self.stores = tuple(stores)

    def __iter__(self):
        for store in self.stores:
            yield from store

    def __len__(self):
        return sum(len(store) for store in self.stores)

    def close(self):
        for store in self.stores:
            close = getattr(store, 'close', None)
            if close is not None:
                close()


class TerminalProcess:
    def __init__(self, command, cwd, environment, options, spool_render_frames=False):
        self.render_frames = RenderFrameStore() if spool_render_frames else []
        self._new_render_frames = []
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
        self._new_render_frames = response.pop('renderFrames', [])
        self.render_frames.extend(self._new_render_frames)
        return response

    def pump(self, timeout=0.05):
        self._new_render_frames = []
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

    def settle(self, contains=None, absent=None, timeout=20, quiet_ms=200, after=0, render_match=False):
        deadline = time.monotonic() + timeout
        expected = (contains,) if isinstance(contains, str) else tuple(contains or ())
        expected_matched = False
        absent_matched = absent is None
        output_advanced = False
        while time.monotonic() < deadline:
            self.pump()
            new_render_frames = self._new_render_frames
            self._new_render_frames = []
            if render_match:
                for frame in new_render_frames:
                    text = '\n'.join(''.join(cell['chars'] for cell in line['cells'] if cell)
                                      for line in frame['viewport'])
                    if (all(item in text for item in expected) and (absent is None or absent not in text) and
                            (len(self.raw) > after or self.closed)):
                        return frame
            if time.monotonic() - self.last_output < quiet_ms / 1000:
                continue
            frame = self.snapshot()
            text = '\n'.join(''.join(cell['chars'] for cell in line['cells'] if cell) for line in frame['viewport'])
            expected_matched = all(item in text for item in expected)
            absent_matched = absent is None or absent not in text
            output_advanced = len(self.raw) > after or self.closed
            modes = frame.get('modes', {})
            synchronized_output = modes.get('synchronizedOutput', False) or modes.get('private', {}).get('2026', False)
            if (expected_matched and absent_matched and output_advanced and
                    (not render_match or not synchronized_output)):
                return frame
            if self.closed:
                raise RuntimeError(f'Terminal exited {self.process.poll()} before {contains!r}.\n{text}')
        raise TimeoutError(
            f'Terminal did not settle at {contains!r} '
            f'(expected={expected_matched}, absent={absent_matched}, output={output_advanced}, '
            f'renderMatch={render_match}).\n{text if "text" in locals() else ""}')

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
        self._new_render_frames = []
        finish_render_frames = getattr(self.render_frames, 'finish', None)
        if finish_render_frames is not None:
            finish_render_frames()


if __name__ == '__main__' and sys.argv[1] == '--exec-pty':
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    os.execvpe(sys.argv[2], sys.argv[2:], os.environ)
