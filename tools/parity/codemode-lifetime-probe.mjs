import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { Worker } from 'node:worker_threads';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { CodemodeSandbox } = await load('packages/codemode/src/index.ts');
const { executeCodemode } = await load('packages/coding-agent/src/extensions/codemode/execute.ts');
const results = [];
for (const how of ['abort', 'timeout']) {
  const entered = Promise.withResolvers();
  const release = Promise.withResolvers();
  let hostCanceled = false;
  const sandbox = new CodemodeSandbox({ timeoutMs: how === 'timeout' ? 500 : Infinity,
    tools: [{ name: 'hold', execute: async (_, { signal }) => {
      signal.addEventListener('abort', () => { hostCanceled = true; }, { once: true });
      entered.resolve();
      return release.promise;
    } }] });
  const controller = new AbortController();
  const pending = sandbox.execute("text('partial'); store('uncommitted', 1); await tools.hold({});", { signal: controller.signal });
  await entered.promise;
  const started = performance.now();
  if (how === 'abort') controller.abort();
  try {
    const result = await pending;
    results.push({ id: how, failed: !result.ok,
      partial: JSON.stringify(result.output) === JSON.stringify([{ type: 'text', text: 'partial' }]),
      settledPromptly: performance.now() - started < 1000, hostCanceled,
      storeCommitted: result.ok && Object.keys(result.storeWrites.set).length !== 0 });
  } finally { release.resolve('late'); await sandbox.close(); }
}
for (const how of ['abort', 'timeout']) {
  const entered = Promise.withResolvers();
  let exited;
  const sandbox = new CodemodeSandbox({ timeoutMs: how === 'timeout' ? 2000 : Infinity,
    onWorkerStarted: promise => { exited = promise; },
    tools: [{ name: 'hold', execute: () => { entered.resolve(); return 'ready'; } }] });
  const controller = new AbortController();
  const launched = performance.now();
  const pending = sandbox.execute("await tools.hold({}); return 'a'.repeat(300000).indexOf('a'.repeat(10000) + 'b');", { signal: controller.signal });
  await entered.promise;
  await new Promise(resolve => setTimeout(resolve, 100));
  const started = performance.now();
  if (how === 'abort') controller.abort();
  try {
    const result = await pending;
    const prompt = how === 'timeout' ? performance.now() - launched < 3000 : performance.now() - started < 1000;
    const settled = performance.now();
    await waitForExit(exited);
    results.push({ id: 'busy-' + how, failed: !result.ok, settledPromptly: prompt,
      exitedPromptly: performance.now() - settled < 1000 });
  } finally { await sandbox.close(); }
}
let entered = 0;
const bothEntered = Promise.withResolvers();
const ctx = { tools: [{ name: 'hold', label: 'hold', description: '', parameters: { type: 'object', properties: {} } }],
  sessionManager: { getBranch: () => [] }, executeTool: async (_, __, { signal }) => {
    if (++entered === 2) bothEntered.resolve();
    await new Promise(resolve => signal.addEventListener('abort', resolve, { once: true }));
    return { isError: true, toolCall: { id: 'hold' }, result: { content: [{ type: 'text', text: 'cancelled' }] } };
  } };
const controller = new AbortController();
const run = (code, signal, context) => executeCodemode('script', { code }, signal, undefined, context);
const once = Worker.prototype.once;
const exits = [];
Worker.prototype.once = function (event, listener) {
  if (event === 'exit') exits.push(new Promise(resolve => once.call(this, 'exit', resolve)));
  return once.call(this, event, listener);
};
const first = run('await tools.hold({})', controller.signal, ctx);
const second = run('await tools.hold({})', controller.signal, ctx);
const limitText = 'Codemode worker limit reached (2 workers are still running or shutting down). Retry after they exit.';
const denied = result => result.content.some(item => item.type === 'text' && item.text.includes(limitText));
try {
  await bothEntered.promise;
  const blocked = await run('return 3');
  const aborted = await run('return 4', AbortSignal.abort());
  results.push({ id: 'shared-admission', denied: denied(blocked),
    cancellationWins: aborted.content.some(item => item.type === 'text' && item.text.includes('Script aborted:')), workers: entered });
} finally {
  controller.abort();
  await Promise.all([first, second]);
  Worker.prototype.once = once;
}
await waitForExit(Promise.all(exits));
const terminate = Worker.prototype.terminate;
const terminations = new Map();
Worker.prototype.terminate = function () {
  this.ref();
  const held = Promise.withResolvers();
  terminations.set(this, held);
  return held.promise;
};
try {
  await run('return 1');
  await run('return 2');
  const blocked = await run('return 3');
  const [worker, held] = [...terminations][0];
  held.resolve(await terminate.call(worker));
  terminations.delete(worker);
  const replacement = await run('return 42');
  results.push({ id: 'exit-accounting', heldUntilExit: denied(blocked),
    replacement: !replacement.isError && replacement.content.some(item => item.type === 'text' && item.text === '42') });
} finally {
  Worker.prototype.terminate = terminate;
  await Promise.all([...terminations].map(async ([worker, held]) => held.resolve(await terminate.call(worker))));
}
console.log(JSON.stringify(results));

async function waitForExit(exited) {
  let timer;
  try {
    // Pi deliberately unreferences retiring workers; this probe stays alive to observe their exit.
    await Promise.race([exited, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('Worker exit was not observed within 5 seconds.')), 5000);
    })]);
  } finally { clearTimeout(timer); }
}
