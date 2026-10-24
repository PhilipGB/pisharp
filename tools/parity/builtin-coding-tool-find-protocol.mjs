import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, output] = process.argv.slice(2);
verifyPiCheckout(pi);
const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-fd-protocol-'));
const workspace = path.join(root, 'workspace');
const bin = path.join(root, 'agent', 'bin');
process.env.PI_CODING_AGENT_DIR = path.join(root, 'agent');
process.env.PI_OFFLINE = '1';
process.env.PATH = bin + path.delimiter + process.env.PATH;
await fs.mkdir(workspace, { recursive: true });
await fs.mkdir(path.join(workspace, '.git'));
await fs.mkdir(bin, { recursive: true });
await fs.writeFile(path.join(workspace, 'file.txt'), 'needle\n');
await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/experimental/source-resolver.ts')).href);
const { createFindToolDefinition } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/tools/find.ts')).href);
const inputs = [
  ['absolute', '<workspace>/file.txt\n'],
  ['relative', 'file.txt\n'],
  ['directory-marker', '<workspace>/nested/\n'],
  ['no-final-newline', '<workspace>/file.txt'],
  ['crlf', '<workspace>/file.txt\r\n'],
  ['cr-only', '<workspace>/first.txt\r<workspace>/second.txt\r'],
  ['order-preserved', '<workspace>/second.txt\n<workspace>/first.txt\n'],
  ['whitespace-trim', ' \t<workspace>/file.txt \t\n'],
  ['unicode-trim-bom', '<workspace>/file.txt\ufeff\n'],
  ['unicode-nel-preserved', '<workspace>/file.txt\u0085\n'],
  ['unicode-path', '<workspace>/目录/日本語.txt\n'],
  ['empty', ''],
  ['one-empty-line', '\n'],
  ['two-empty-lines', '\n\n'],
  ['whitespace-only', ' \t\n'],
  ['partial-error', '<workspace>/file.txt\n', 'fixture traversal failure\n', 1],
  ['partial-error-whitespace', ' \n', 'fixture traversal failure\n', 2],
  ['error-without-output', '', 'fixture traversal failure\n', 1],
  ['exit-127', '', '', 127],
  ['limit-one-extra-output', '<workspace>/one.txt\n<workspace>/two.txt\n'],
  ['fractional-limit-output', '<workspace>/one.txt\n<workspace>/two.txt\n'],
  ['zero-limit-empty', ''],
  ['oversized-pattern', ''],
  ['nul-pattern', ''],
  ['nul-path', ''],
  ['non-executable', '', '', 0, 0o644],
  ['missing-interpreter', '', '', 0, 0o755, '#!/pisharp-fixture-missing-interpreter\n'],
];
const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
const executable = path.join(bin, 'fd');
const rows = [];
try {
  for (const [caseId, stdout, stderr = '', exitCode = 0, mode = 0o755, header = '#!/bin/sh\n'] of inputs) {
    const stdoutFile = path.join(root, 'stdout');
    const stderrFile = path.join(root, 'stderr');
    await fs.writeFile(stdoutFile, stdout.replaceAll('<workspace>', workspace));
    await fs.writeFile(stderrFile, stderr);
    await fs.writeFile(executable, header + 'if [ "$1" = "--version" ]; then printf "synthetic-fd-protocol-fixture\\n"; exit 0; fi\n' +
      `cat ${quote(stdoutFile)}\ncat ${quote(stderrFile)} >&2\nexit ${exitCode}\n`);
    await fs.chmod(executable, mode);
    const argumentsValue = { pattern: caseId === 'oversized-pattern' ? 'a'.repeat(140000) : caseId === 'nul-pattern' ? 'needle\0x' : '*' };
    if (caseId === 'nul-path') argumentsValue.path = 'file.txt\0suffix';
    if (caseId === 'limit-one-extra-output') argumentsValue.limit = 1;
    if (caseId === 'fractional-limit-output') argumentsValue.limit = 1.5;
    if (caseId === 'zero-limit-empty') argumentsValue.limit = 0;
    let result;
    let error;
    try { result = await createFindToolDefinition(workspace).execute(caseId, argumentsValue); }
    catch (failure) { error = failure.message.replaceAll(executable, '<executable>').replaceAll(workspace, '<workspace>'); }
    rows.push({ caseId, stdout, stderr, exitCode, mode, header,
      arguments: caseId === 'oversized-pattern' ? { pattern: { repeat: 'a', count: 140000 } } : argumentsValue,
      expected: { rejected: error !== undefined, content: result?.content ?? [{ type: 'text', text: error }], details: result?.details ?? null } });
  }
  await fs.writeFile(output, JSON.stringify({ schema: 1, repositoryMarker: '.git', pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/find.ts', fixture: 'tools/parity/builtin-coding-tool-find-protocol.mjs',
    dependency: 'Explicit synthetic fd process/protocol fixture; separate from real-fd differentials', cases: rows }, null, 2) + '\n');
} finally { await fs.rm(root, { recursive: true, force: true }); }
