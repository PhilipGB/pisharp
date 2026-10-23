import * as fs from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import childProcess from 'node:child_process';
import { syncBuiltinESMExports } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, output] = process.argv.slice(2);
verifyPiCheckout(pi);
const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-fd-cancel-'));
const workspace = path.join(root, 'workspace');
const directory = path.join(workspace, 'x'.repeat(180));
process.env.PI_CODING_AGENT_DIR = path.join(root, 'agent');
process.env.PI_OFFLINE = '1';
await fs.mkdir(directory, { recursive: true });
const count = 20050;
for (let index = 0; index < count; index++) await fs.writeFile(path.join(directory, `${String(index).padStart(6, '0')}.txt`), '');
await fs.writeFile(path.join(workspace, 'recovery.txt'), 'unchanged');
await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/experimental/source-resolver.ts')).href);
const { createFindToolDefinition } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/tools/find.ts')).href);
const original = childProcess.spawn;
const controller = new AbortController();
const closed = Promise.withResolvers();
let child;
let atFirstOutput;
let timer;
const state = pid => {
  try { const text = readFileSync(`/proc/${pid}/stat`, 'utf8'); return text.slice(text.lastIndexOf(')') + 2).split(' ')[0]; }
  catch (error) { if (['ENOENT', 'ESRCH'].includes(error.code)) return 'gone'; throw error; }
};
childProcess.spawn = function(command, ...argumentsValue) {
  const processValue = original.call(this, command, ...argumentsValue);
  if (['fd', 'fdfind'].includes(path.basename(command))) {
    child = processValue;
    child.once('close', () => closed.resolve());
    child.stdout.once('data', bytes => {
      atFirstOutput = { processState: state(child.pid), bytes: bytes.length };
      controller.abort();
    });
  }
  return processValue;
};
syncBuiltinESMExports();
try {
  const tool = createFindToolDefinition(workspace);
  const deadline = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Live fd traversal did not settle')), 10000); });
  const outcome = await Promise.race([tool.execute('cancel-live-fd', { pattern: '*', limit: 0 }, controller.signal)
    .then(result => ({ result }), error => ({ error: error.message })), deadline]);
  const stateAtSettlement = child ? state(child.pid) : null;
  if (child) await Promise.race([closed.promise, deadline]);
  childProcess.spawn = original;
  syncBuiltinESMExports();
  if (!atFirstOutput || ['gone', 'Z'].includes(atFirstOutput.processState)) throw new Error('Cancellation was not observed during live fd execution');
  const continuation = await tool.execute('after-fd-abort', { pattern: 'recovery.txt', limit: 1 });
  await fs.writeFile(output, JSON.stringify({ schema: 1, pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/find.ts', fixture: 'tools/parity/builtin-coding-tool-find-cancellation.mjs',
    dependency: 'Real fd/fdfind; parent spawn observer subscribes to first stdout data without changing executable, arguments or output',
    version: childProcess.execFileSync(child.spawnfile, ['--version'], { encoding: 'utf8' }).trim(),
    setup: { files: count, directoryNameLength: 180, pattern: '*', limit: 0 },
    pipeMaximumBytes: Number(readFileSync('/proc/sys/fs/pipe-max-size', 'utf8').trim()),
    atFirstOutput, outcome, stateAtSettlement, stateAfterClose: state(child.pid), continuation,
    inputFilesAfter: (await fs.readdir(directory)).length, recoveryFileAfter: await fs.readFile(path.join(workspace, 'recovery.txt'), 'utf8') }, null, 2) + '\n');
} finally {
  clearTimeout(timer);
  childProcess.spawn = original;
  syncBuiltinESMExports();
  if (child && child.exitCode === null) child.kill('SIGKILL');
  await fs.rm(root, { recursive: true, force: true });
}
