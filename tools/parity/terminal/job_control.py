"""Supervise one foreground terminal job, including a bounded suspend/resume handshake."""
import os
import signal
import sys
import termios


def supervise(command):
    original = termios.tcgetattr(0)
    ready_read, ready_write = os.pipe()
    child = os.fork()
    if child == 0:
        os.close(ready_write)
        os.setpgid(0, 0)
        for value in (signal.SIGTSTP, signal.SIGTTIN, signal.SIGTTOU):
            signal.signal(value, signal.SIG_DFL)
        os.read(ready_read, 1)
        os.close(ready_read)
        os.execvpe(command[0], command, os.environ)

    os.close(ready_read)
    os.setpgid(child, child)
    signal.signal(signal.SIGTTOU, signal.SIG_IGN)
    signal.signal(signal.SIGINT, signal.SIG_IGN)

    def terminate(_signal, _frame):
        raise SystemExit(143)

    signal.signal(signal.SIGTERM, terminate)
    signal.signal(signal.SIGHUP, terminate)
    try:
        os.tcsetpgrp(0, child)
        os.write(ready_write, b'1')
        os.close(ready_write)
        suspensions = 0
        while True:
            _, status = os.waitpid(child, os.WUNTRACED)
            if os.WIFEXITED(status):
                return os.WEXITSTATUS(status)
            if os.WIFSIGNALED(status):
                return 128 + os.WTERMSIG(status)
            if not os.WIFSTOPPED(status) or os.WSTOPSIG(status) != signal.SIGTSTP:
                raise RuntimeError('The terminal job stopped for an unexpected signal.')
            os.tcsetpgrp(0, os.getpgrp())
            flags = termios.tcgetattr(0)[3]
            required = termios.ICANON | termios.ECHO | termios.ISIG
            if flags & required != original[3] & required:
                raise RuntimeError('The suspended job did not restore terminal settings.')
            suspensions += 1
            print(f'\nsuspended:tty-restored:{suspensions}', flush=True)
            if sys.stdin.readline().strip() != 'resume':
                raise RuntimeError('Expected the supervised resume action.')
            os.tcsetpgrp(0, child)
            os.killpg(child, signal.SIGCONT)
    finally:
        for value in (signal.SIGCONT, signal.SIGKILL):
            try:
                os.killpg(child, value)
            except ProcessLookupError:
                pass
        try:
            os.waitpid(child, 0)
        except ChildProcessError:
            pass
        os.tcsetpgrp(0, os.getpgrp())
        termios.tcsetattr(0, termios.TCSANOW, original)


if __name__ == '__main__':
    raise SystemExit(supervise(sys.argv[1:]))
