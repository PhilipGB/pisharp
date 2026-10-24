import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { execFileSync } from 'node:child_process';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, output] = process.argv.slice(2);
verifyPiCheckout(pi);
const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-grep-protocol-'));
const workspace = path.join(root, 'workspace');
const bin = path.join(root, 'agent', 'bin');
process.env.PI_CODING_AGENT_DIR = path.join(root, 'agent');
process.env.PI_OFFLINE = '1';
process.env.PATH = bin + path.delimiter + process.env.PATH;
await fs.mkdir(workspace, { recursive: true });
await fs.mkdir(bin, { recursive: true });
await fs.writeFile(path.join(workspace, 'file.txt'), 'needle\n');
await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/experimental/source-resolver.ts')).href);
const { createGrepToolDefinition } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/tools/grep.ts')).href);
const valid = JSON.stringify({ type: 'match', data: { path: { text: '<workspace>/file.txt' },
  lines: { text: 'needle\n' }, line_number: 1, submatches: [] } });
const inputs = [
  ['valid', valid + '\n'],
  ['valid-no-final-newline', valid],
  ['ignored-garbage-then-valid', 'not json\n' + valid + '\n'],
  ['invalid-trailing-token', valid + ' trailing\n'],
  ['invalid-two-objects-one-line', valid + valid + '\n'],
  ['invalid-missing-comma', valid.replace('"match",', '"match"') + '\n'],
  ['invalid-number', valid.replace('"line_number":1', '"line_number":1x') + '\n'],
  ['invalid-string-escape', valid.replace('needle', 'needle\\q') + '\n'],
  ['invalid-unicode-escape', valid.replace('needle', 'needle\\u0q00') + '\n'],
  ['invalid-raw-control', valid.replace('needle', 'needle\x01') + '\n'],
  ['incomplete-object', valid.slice(0, -1)],
  ['valid-root-array-ignored', '[' + valid + ']\n'],
  ['valid-escaped-key', valid.replace('"type"', '"ty\\u0070e"') + '\n'],
  ['valid-type-after-data', JSON.stringify({ data: JSON.parse(valid).data, type: 'match' }) + '\n'],
  ['valid-unicode-path-and-line', JSON.stringify({ type: 'match', data: { path: { text: '<workspace>/目录/日本語.txt' },
    lines: { text: 'café 😀\n' }, line_number: 1 } }) + '\n'],
  ['valid-unicode-escapes', valid.replace('needle\\n', 'caf\\u00e9\\ud83d\\ude00\\n') + '\n'],
  ['valid-long-unicode-line', valid.replace('needle\\n', '😀'.repeat(251) + '\\n') + '\n'],
  ['duplicate-type-null', valid.slice(0, -1) + ',"type":null}\n'],
  ['duplicate-data-null', valid.slice(0, -1) + ',"data":null}\n'],
  ['large-ignored-submatches', valid.replace('"submatches":[]', '"submatches":' + JSON.stringify(Array.from({ length: 20000 },
    (_, index) => ({ match: { text: 'needle' }, start: index, end: index + 6 })))) + '\n'],
  ['exit-two-with-stderr', '', 'fixture search failure\n', 2],
  ['exit-127', '', '', 127],
  ['oversized-pattern', ''],
  ['nul-pattern', ''],
  ['nul-path', ''],
  ['non-executable', '', '', 0, 0o644],
  ['missing-interpreter', '', '', 0, 0o755, '#!/pisharp-fixture-missing-interpreter\n'],
];
const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
const executable = path.join(bin, 'rg');
const rows = [];

async function fifoCancellation() {
  await fs.unlink(executable);
  const fifo = path.join(workspace, 'blocked-input');
  execFileSync('mkfifo', ['-m', '600', fifo]);
  const controller = new AbortController();
  const tool = createGrepToolDefinition(workspace);
  let settled = false;
  let timer;
  let writer;
  const updates = [];
  const blocked = tool.execute('real-fifo', { pattern: 'needle', path: 'blocked-input' }, controller.signal,
    update => updates.push(update)).then(result => ({ result }), error => ({ error: error.message }))
    .finally(() => { settled = true; });
  const writerPending = fs.open(fifo, fs.constants.O_WRONLY);
  const deadline = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Real rg FIFO barrier timed out')), 10000); });
  const children = async () => {
    const result = new Set();
    for (const task of await fs.readdir('/proc/self/task')) {
      let text;
      try { text = await fs.readFile(`/proc/self/task/${task}/children`, 'utf8'); }
      catch (error) { if (error.code === 'ENOENT') continue; throw error; }
      for (const pid of text.trim().split(/\s+/).filter(Boolean)) {
        try { if ((await fs.readFile(`/proc/${pid}/cmdline`, 'utf8')).includes(fifo)) result.add(Number(pid)); }
        catch (error) { if (error.code !== 'ENOENT' && error.code !== 'ESRCH') throw error; }
      }
    }
    return [...result];
  };
  try {
    writer = await Promise.race([writerPending, deadline]);
    const parallel = await Promise.race([tool.execute('parallel', { pattern: 'needle', path: 'file.txt' }), deadline]);
    const blockedBeforeCancellation = !settled;
    const pids = await children();
    controller.abort();
    const outcome = await Promise.race([blocked, deadline]);
    const processStates = [];
    for (const pid of pids) {
      try { const stat = await fs.readFile(`/proc/${pid}/stat`, 'utf8'); processStates.push(stat.slice(stat.lastIndexOf(')') + 2).split(' ')[0]); }
      catch (error) { if (error.code === 'ENOENT' || error.code === 'ESRCH') processStates.push('gone'); else throw error; }
    }
    const continuation = await tool.execute('after-cancel', { pattern: 'needle', path: 'file.txt' });
    return { dependency: 'Real system rg; synthetic executable removed before execution',
      version: execFileSync('rg', ['--version'], { encoding: 'utf8' }).split('\n')[0],
      barrier: 'FIFO writer opened after real rg reader; no data supplied before cancellation',
      blockedBeforeCancellation, parallel, outcome, updates, processCount: pids.length, processStates, continuation };
  } finally {
    clearTimeout(timer);
    controller.abort();
    for (const pid of await children()) {
      try { process.kill(pid, 'SIGKILL'); } catch (error) { if (error.code !== 'ESRCH') throw error; }
    }
    if (!writer) {
      const unblock = await fs.open(fifo, fs.constants.O_RDONLY | fs.constants.O_NONBLOCK);
      writer = await writerPending;
      await unblock.close();
    }
    await writer.close();
    await blocked;
  }
}

try {
  for (const [caseId, stdout, stderr = '', exitCode = 0, mode = 0o755, header = '#!/bin/sh\n'] of inputs) {
    const stdoutFile = path.join(root, 'stdout');
    const stderrFile = path.join(root, 'stderr');
    await fs.writeFile(stdoutFile, stdout.replaceAll('<workspace>', workspace));
    await fs.writeFile(stderrFile, stderr);
    await fs.writeFile(executable, header + 'if [ "$1" = "--version" ]; then printf "synthetic-rg-protocol-fixture\\n"; exit 0; fi\n' +
      `cat ${quote(stdoutFile)}\ncat ${quote(stderrFile)} >&2\nexit ${exitCode}\n`);
    await fs.chmod(executable, mode);
    const argumentsValue = { pattern: caseId === 'oversized-pattern' ? 'a'.repeat(140000) : caseId === 'nul-pattern' ? 'needle\0x' : 'needle' };
    if (caseId === 'nul-path') argumentsValue.path = 'file.txt\0suffix';
    let result;
    let error;
    try { result = await createGrepToolDefinition(workspace).execute(caseId, argumentsValue); }
    catch (failure) { error = failure.message.replaceAll(executable, '<executable>').replaceAll(workspace, '<workspace>'); }
    rows.push({ caseId, stdout: caseId === 'large-ignored-submatches' ? undefined : stdout,
      stdoutRecipe: caseId === 'large-ignored-submatches' ? { event: valid, submatches: 20000 } : undefined,
      stderr, exitCode, mode, header, arguments: caseId === 'oversized-pattern' ? { pattern: { repeat: 'a', count: 140000 } } : argumentsValue,
      expected: { rejected: error !== undefined, content: result?.content ?? [{ type: 'text', text: error }], details: result?.details ?? null } });
  }
  const realRipgrepCancellation = await fifoCancellation();
  await fs.writeFile(output, JSON.stringify({ schema: 1, pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/grep.ts', fixture: 'tools/parity/builtin-coding-tool-grep-protocol.mjs',
    dependency: 'Explicit synthetic rg process/protocol fixture; real-rg FIFO cancellation recorded separately',
    realRipgrepCancellation, cases: rows }, null, 2) + '\n');
} finally { await fs.rm(root, { recursive: true, force: true }); }
