#!/usr/bin/env python3
"""Opt-in, single-writer Pi RPC supervisor. State is durable; Pi sessions survive worker restarts.

Start only after leaving any other agent with write access to this checkout. See README.md.
"""
import argparse
import fcntl
import json
import os
from pathlib import Path
import queue
import signal
import subprocess
import sys
import tempfile
import threading
import time


def atomic_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".autonomy-", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(value, stream, indent=2, ensure_ascii=False)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def valid_task(task):
    return (task.get("status") == "verified" and bool(task.get("acceptance"))
            and bool(task.get("implementation")) and bool(task.get("evidence")))


def load_ledger(path):
    value = json.loads(path.read_text(encoding="utf-8"))
    tasks = value.get("tasks")
    if not isinstance(tasks, list) or not tasks or len({t["id"] for t in tasks}) != len(tasks):
        raise ValueError("Ledger must contain unique, nonempty task IDs")
    known = {t["id"] for t in tasks}
    if not set(value.get("required_ids", known)).issubset(known):
        raise ValueError("Requirements were removed from the ledger")
    if any(t.get("status") not in ("not_started", "in_progress", "blocked", "verified", "excluded")
           or t.get("status") == "excluded" and t["id"] != "feature-pi-packages" for t in tasks):
        raise ValueError("Unknown status or attempted exclusion beyond Pi Packages")
    if any(t.get("status") == "verified" and not valid_task(t) for t in tasks):
        raise ValueError("Verified task lacks acceptance criteria, implementation, or evidence")
    return tasks


def git_head(repo):
    return subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()


class ExternalLimitError(RuntimeError):
    """Provider refused work due to an authentication, quota, or spending constraint."""


class ProviderRunError(RuntimeError):
    """A completed RPC run had an unsuccessful assistant response."""


class RpcWorker:
    def __init__(self, command, cwd, lock_fd):
        self.process = subprocess.Popen(command, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, start_new_session=True, pass_fds=(lock_fd,))
        self.records = queue.Queue(maxsize=1024)
        self.errors = []
        self.stderr_reader = threading.Thread(target=self._drain_stderr, daemon=True)
        self.stdout_reader = threading.Thread(target=self._drain_stdout, daemon=True)
        self.stderr_reader.start()
        self.stdout_reader.start()
        self.serial = 0

    def _drain_stdout(self):
        try:
            for line in self.process.stdout:
                try:
                    self.records.put(json.loads(line), timeout=15)
                except (ValueError, queue.Full):
                    self.records.put({"type": "protocol_error"})
        finally:
            self.records.put({"type": "worker_eof"})

    def _drain_stderr(self):
        for line in self.process.stderr:
            self.errors.append(line.decode("utf-8", "replace")[:2000])
            if len(self.errors) > 20:
                del self.errors[0]

    def prompt(self, text, timeout):
        self.serial += 1
        identifier = f"autonomy-{self.serial}"
        command = (json.dumps({"id": identifier, "type": "prompt", "message": text}, ensure_ascii=False) + "\n").encode()
        self.process.stdin.write(command)
        self.process.stdin.flush()
        end = time.monotonic() + timeout
        accepted = False
        provider_error = None
        while time.monotonic() < end:
            try:
                record = self.records.get(timeout=min(1, max(0.01, end - time.monotonic())))
            except queue.Empty:
                if self.process.poll() is not None:
                    raise RuntimeError(f"Worker exited {self.process.returncode}; {self.errors[-2:]}")
                continue
            if record.get("type") == "response" and record.get("id") == identifier:
                if record.get("success") is not True:
                    raise RuntimeError("RPC prompt rejected: " + str(record.get("error", "unknown error")))
                accepted = True
            elif record.get("type") == "message_end":
                message = record.get("message") or {}
                if message.get("role") == "assistant" and message.get("stopReason") in ("error", "aborted"):
                    provider_error = str(message.get("errorMessage") or message.get("stopReason"))[:400]
            elif record.get("type") == "agent_settled" and accepted:
                if provider_error:
                    if any(word in provider_error.lower() for word in
                           ("usage limit", "quota", "insufficient_quota", "billing", "spending limit", "authentication", "unauthorized")):
                        raise ExternalLimitError("Provider constraint: " + provider_error)
                    raise ProviderRunError("Provider failed: " + provider_error)
                return
            elif record.get("type") in ("worker_eof", "protocol_error"):
                raise RuntimeError(f"Worker stream ended or invalid: {self.errors[-2:]}")
        raise TimeoutError("RPC worker did not settle before the configured deadline")

    def close(self):
        try:
            if self.process.poll() is None:
                self.process.stdin.close()
                try:
                    self.process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    os.killpg(self.process.pid, signal.SIGTERM)
                    try:
                        self.process.wait(timeout=3)
                    except subprocess.TimeoutExpired:
                        os.killpg(self.process.pid, signal.SIGKILL)
                        self.process.wait(timeout=3)
        finally:
            for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
                if stream and not stream.closed:
                    stream.close()
            self.stdout_reader.join(timeout=1)
            self.stderr_reader.join(timeout=1)

class Controller:
    def __init__(self, repo, ledger, runtime, worker_command, *, timeout=2400, max_dispatches=20,
                 max_attempts=3, final_command=None, require_pushed=True):
        self.repo, self.ledger, self.runtime = Path(repo).resolve(), Path(ledger), Path(runtime)
        self.worker_command = worker_command
        self.timeout, self.max_dispatches, self.max_attempts = timeout, max_dispatches, max_attempts
        self.final_command = final_command or ["dotnet", "test", "PiSharp.slnx", "--no-restore"]
        self.require_pushed = require_pushed
        self.state_file = self.runtime / "state.json"
        self.runtime.mkdir(parents=True, exist_ok=True)
        self.lock_file = open(self.runtime / "supervisor.lock", "a+b")
        try:
            fcntl.flock(self.lock_file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            self.lock_file.close()
            raise RuntimeError("Another supervisor or its worker holds the repository lease") from error
        self.state = json.loads(self.state_file.read_text()) if self.state_file.exists() else {
            "active_task": None, "attempts": {}, "progress": [], "completed": False}
        self.state.setdefault("validated", {})
        self.worker = None

    def save(self):
        self.state["progress"] = self.state["progress"][-100:]
        atomic_json(self.state_file, self.state)

    def close(self):
        try:
            if self.worker:
                self.worker.close()
        finally:
            self.lock_file.close()

    def validate(self, task):
        """Evidence in a file is insufficient: validate the actual pushed checkout."""
        identifier = task["id"]
        if self.require_pushed:
            if subprocess.check_output(["git", "status", "--porcelain"], cwd=self.repo).strip():
                raise RuntimeError("Worker left uncommitted changes; stopping rather than dispatching another writer")
            upstream = subprocess.check_output(["git", "rev-parse", "@{u}"], cwd=self.repo, text=True).strip()
            if git_head(self.repo) != upstream:
                raise RuntimeError("Worker has not pushed its commit to the tracked remote")
        result = subprocess.run(self.final_command, cwd=self.repo, timeout=self.timeout,
                                capture_output=True, text=True)
        self.state["progress"].append({"task": identifier, "event": "validated",
            "exit_code": result.returncode, "head": git_head(self.repo),
            "tail": (result.stdout + result.stderr)[-900:] if result.returncode else ""})
        if result.returncode != 0:
            raise RuntimeError("Deterministic validation failed; inspect persisted output")
        self.state["validated"][identifier] = True
        self.save()

    def run(self):
        try:
            for _ in range(self.max_dispatches):
                tasks = load_ledger(self.ledger)
                for recorded in tasks:
                    if valid_task(recorded) and recorded["id"] not in self.state["validated"]:
                        self.validate(recorded)
                pending = [t for t in tasks if t.get("status") != "excluded" and not valid_task(t)]
                if not pending:
                    ledger = json.loads(self.ledger.read_text(encoding="utf-8"))
                    audit = ledger.get("final_audit", {})
                    if ledger.get("plan_received") is not True or audit.get("complete") is not True or not audit.get("evidence"):
                        self.state["stop_reason"] = "Original plan reconciliation and independent final audit are required"
                        self.save()
                        return 4
                    validation = subprocess.run(self.final_command, cwd=self.repo, timeout=self.timeout,
                                                capture_output=True, text=True)
                    self.state["final_validation"] = {"exit_code": validation.returncode,
                        "tail": (validation.stdout + validation.stderr)[-2500:]}
                    self.state["completed"] = validation.returncode == 0
                    self.save()
                    return 0 if self.state["completed"] else 3
                available = [t for t in pending if t.get("status") != "blocked" and
                    self.state["attempts"].get(t["id"], 0) < self.max_attempts]
                if not available:
                    self.state["stop_reason"] = "Unverified tasks remain, but each is blocked or needs a changed approach"
                    self.save()
                    return 4
                previous = self.state.get("active_task")
                task = next((t for t in available if t["id"] == previous), available[0])
                identifier = task["id"]
                restarted = previous == identifier
                self.state["active_task"] = identifier
                self.state["attempts"][identifier] = self.state["attempts"].get(identifier, 0) + 1
                before = git_head(self.repo)
                self.state["progress"].append({"task": identifier, "event": "dispatched",
                    "attempt": self.state["attempts"][identifier], "git_head": before})
                self.save()
                if self.worker is None or self.worker.process.poll() is not None:
                    self.worker = RpcWorker(self.worker_command, self.repo, self.lock_file.fileno())
                instructions = (
                    "You are the sole implementation worker for PiSharp. Read tools/autonomy/README.md, "
                    "docs/parity/execution-ledger.json, docs/continuation.md, and git status first. "
                    f"Current task: {identifier}: {task['title']}. Acceptance: {task['acceptance']}. "
                    "Inspect upstream reference, implement one cohesive slice, integrate, run deterministic validation, "
                    "fix failures, commit, push and record actual implementation paths and verification evidence in the ledger. "
                    "Do not mark a capability verified without behavioural evidence; do not claim complete based on a commit. "
                    "The external supervisor dispatches the next task when this run settles. "
                    + ("A previous worker was interrupted. Inspect the repository and ledger for already completed work; "
                       "do not blindly repeat side effects. " if restarted else "")
                )
                try:
                    self.worker.prompt(instructions, self.timeout)
                    after = git_head(self.repo)
                    verified = valid_task(next(t for t in load_ledger(self.ledger) if t["id"] == identifier))
                    self.state["progress"].append({"task": identifier, "event": "settled", "verified": verified,
                        "before": before, "after": after})
                    if verified:
                        self.state["active_task"] = None
                    elif after == before:
                        # Don't submit the same unchanged prompt repeatedly; select another task.
                        self.state["attempts"][identifier] = self.max_attempts
                        self.state["active_task"] = None
                except ExternalLimitError as error:
                    self.state["stop_reason"] = str(error)
                    self.state["progress"].append({"task": identifier, "event": "external_limit", "error": str(error)})
                    self.state["active_task"] = identifier
                    self.state["attempts"][identifier] -= 1  # No implementation work was performed.
                    self.save()
                    return 4

                except ProviderRunError as error:
                    self.state["stop_reason"] = str(error)
                    self.state["progress"].append({"task": identifier, "event": "provider_failure", "error": str(error)})
                    self.state["active_task"] = identifier
                    self.state["attempts"][identifier] -= 1
                    self.save()
                    return 4
                except (RuntimeError, TimeoutError, BrokenPipeError) as error:
                    self.state["progress"].append({"task": identifier, "event": "failure", "error": str(error)[:400]})
                    self.worker.close()
                    self.worker = None
                    self.save()
                    time.sleep(min(2 ** self.state["attempts"][identifier], 30))
                self.save()
            self.state["stop_reason"] = "Dispatch budget exhausted; restart the supervisor to continue"
            self.save()
            return 5
        finally:
            self.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--ledger", type=Path)
    parser.add_argument("--runtime", type=Path)
    parser.add_argument("--timeout", type=int, default=2400)
    parser.add_argument("--max-dispatches", type=int, default=20)
    parser.add_argument("--worker-command", type=json.loads, help="JSON string array; defaults to installed Pi RPC")
    arguments = parser.parse_args()
    repo = arguments.repo.resolve()
    runtime = arguments.runtime or repo / ".pisharp-autonomy"
    ledger = arguments.ledger or repo / "docs/parity/execution-ledger.json"
    command = arguments.worker_command or ["pi", "--mode", "rpc", "--provider", "openai-codex",
                                           "--model", "gpt-5.6-sol", "--thinking", "high", "--offline",
                                           "--no-approve", "--no-extensions", "--session-dir",
                                           str(runtime / "pi-sessions"), "--continue"]
    try:
        controller = Controller(repo, ledger, runtime, command, timeout=arguments.timeout,
                                max_dispatches=arguments.max_dispatches)
        return controller.run()
    except (OSError, ValueError, RuntimeError) as error:
        print(f"Supervisor stopped: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
