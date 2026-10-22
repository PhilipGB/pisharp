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
import shutil
import struct
import subprocess
import sys
import tempfile
import termios
from threading import Thread
import time

from compare import ControlTrace, osc52_clipboard_writes
from thread_limits import apply_test_thread_limit

apply_test_thread_limit()


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

    def write_compressed_to(self, output):
        """Copy the already-compressed JSONL member without expanding or re-encoding it."""
        self.finish()
        self._file.seek(0)
        shutil.copyfileobj(self._file, output, length=1024 * 1024)


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

    def write_compressed_to(self, output):
        """Write concatenated gzip members, preserving each store's compressed render stream."""
        for store in self.stores:
            store.write_compressed_to(output)


class TerminalProcess:
    def __init__(self, command, cwd, environment, options, spool_render_frames=False):
        if options.get('jobControl') and sys.platform != 'linux':
            raise ValueError('The supervised job-control profile requires Linux /proc process ownership.')
        self.render_frames = RenderFrameStore() if spool_render_frames else []
        self._new_render_frames = []
        self.emulator = subprocess.Popen(['node', str(Path(__file__).with_name('emulator.mjs'))],
                                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        self.ask(dict(op='start', options=options))
        self.master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', options['rows'], options['columns'], 0, 0))
        self.job_control = bool(options.get('jobControl'))
        if self.job_control:
            command = [sys.executable, str(Path(__file__).with_name('job_control.py')), *command]
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

    def settle(self, contains=None, absent=None, timeout=20, quiet_ms=200, after=0, render_match=False,
               allow_static_frame=False, cursor_before=None, clipboard_texts=None, clipboard_after=0):
        deadline = time.monotonic() + timeout
        expected = (contains,) if isinstance(contains, str) else tuple(contains or ())
        expected_matched = False
        absent_matched = absent is None
        output_advanced = False
        cursor_matched = cursor_before is None
        static_pump_timeout = 0

        def clipboard_matched():
            return clipboard_texts is None or [entry['text'] for entry in
                osc52_clipboard_writes(self.raw[clipboard_after:])] == clipboard_texts

        def cursor_changed(frame):
            nonlocal cursor_matched
            if cursor_before is None:
                cursor_matched = True
                return cursor_matched
            cursor = frame.get('cursor', {})
            cursor_matched = (cursor.get('x'), cursor.get('y')) != (
                cursor_before.get('x'), cursor_before.get('y'))
            return cursor_matched

        while time.monotonic() < deadline:
            self.pump(timeout=static_pump_timeout if allow_static_frame else 0.05)
            new_render_frames = self._new_render_frames
            self._new_render_frames = []
            if allow_static_frame and not render_match:
                frame = self.snapshot()
                text = '\n'.join(''.join(cell['chars'] for cell in line['cells'] if cell)
                                 for line in frame['viewport'])
                expected_matched = all(item in text for item in expected)
                absent_matched = absent is None or absent not in text
                modes = frame.get('modes', {})
                synchronized_output = modes.get('synchronizedOutput', False) or modes.get('private', {}).get('2026', False)
                if (expected_matched and absent_matched and cursor_changed(frame) and
                        not synchronized_output and clipboard_matched()):
                    return frame
                static_pump_timeout = 0.01
            if render_match:
                for frame in new_render_frames:
                    text = '\n'.join(''.join(cell['chars'] for cell in line['cells'] if cell)
                                      for line in frame['viewport'])
                    if (clipboard_texts is None and all(item in text for item in expected) and
                            (absent is None or absent not in text) and
                            cursor_changed(frame) and (len(self.raw) > after or self.closed)):
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
                    cursor_changed(frame) and (not render_match or not synchronized_output) and clipboard_matched()):
                return frame
            if self.closed:
                raise RuntimeError(f'Terminal exited {self.process.poll()} before {contains!r}.\n{text}')
        raise TimeoutError(
            f'Terminal did not settle at {contains!r} '
            f'(expected={expected_matched}, absent={absent_matched}, output={output_advanced}, '
            f'renderMatch={render_match}, cursorChanged={cursor_matched}).\n'
            f'{text if "text" in locals() else ""}')

    def close(self):
        if self.process.poll() is None:
            groups = {self.process.pid}
            if self.job_control:
                children = Path(f'/proc/{self.process.pid}/task/{self.process.pid}/children')
                try:
                    for child in children.read_text().split():
                        try:
                            if os.getsid(int(child)) == self.process.pid:
                                groups.add(os.getpgid(int(child)))
                        except ProcessLookupError:
                            pass
                except FileNotFoundError:
                    pass
            for group in groups:
                try:
                    if self.job_control:
                        os.killpg(group, signal.SIGCONT)
                    os.killpg(group, signal.SIGTERM)
                except ProcessLookupError:
                    pass
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                for group in groups:
                    try:
                        os.killpg(group, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                self.process.wait(timeout=3)
        os.close(self.master)

        def drain_emulator():
            while self.emulator.stdout.read(64 * 1024):
                pass

        # A pending snapshot can fill stdout and prevent exit after stdin closes.
        # Captures have already consumed their responses; drain teardown output in bounded chunks.
        drain = Thread(target=drain_emulator, daemon=True)
        drain.start()
        try:
            self.emulator.stdin.close()
            self.emulator.wait(timeout=5)
        except (subprocess.TimeoutExpired, BrokenPipeError):
            if self.emulator.poll() is None:
                self.emulator.kill()
                self.emulator.wait(timeout=3)
            raise
        finally:
            drain.join(timeout=3)
            self.emulator.stdout.close()
            self._new_render_frames = []
            finish_render_frames = getattr(self.render_frames, 'finish', None)
            if finish_render_frames is not None:
                finish_render_frames()


if __name__ == '__main__' and sys.argv[1] == '--exec-pty':
    fcntl.ioctl(0, termios.TIOCSCTTY, 0)
    os.execvpe(sys.argv[2], sys.argv[2:], os.environ)
