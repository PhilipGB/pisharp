import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, output] = process.argv.slice(2);
verifyPiCheckout(pi);
await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/experimental/source-resolver.ts')).href);
const { createLsToolDefinition } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/tools/ls.ts')).href);
const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-ls-cancel-'));
const ready = Promise.withResolvers();
const release = Promise.withResolvers();
const drained = Promise.withResolvers();
const controller = new AbortController();
let timer;
try {
  await fs.writeFile(path.join(root, 'first.txt'), 'unchanged');
  await fs.writeFile(path.join(root, 'second.txt'), 'also unchanged');
  let statCount = 0;
  const tool = createLsToolDefinition(root, { operations: {
    exists: async target => fs.access(target).then(() => true, () => false),
    stat: async target => {
      const result = await fs.stat(target);
      if (target !== root && ++statCount === 2) drained.resolve();
      return result;
    },
    readdir: async target => {
      const names = await fs.readdir(target);
      ready.resolve();
      await release.promise;
      return names;
    },
  } });
  const outcome = tool.execute('ls-during-read', {}, controller.signal)
    .then(result => ({ result }), error => ({ error: error.message }));
  const deadline = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Ls cancellation failed to settle')), 10000); });
  await Promise.race([ready.promise, deadline]);
  const independent = await createLsToolDefinition(root).execute('independent-ls', {});
  controller.abort();
  const settledBeforeReadReleased = await Promise.race([outcome, deadline]);
  release.resolve();
  await Promise.race([drained.promise, deadline]);
  const continuation = await createLsToolDefinition(root).execute('after-ls-abort', {});
  await fs.writeFile(output, JSON.stringify({ schema: 1, pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/ls.ts', fixture: 'tools/parity/builtin-coding-tool-ls-cancellation.mjs',
    barrier: 'Real readdir snapshot held before fulfillment through the pinned operations hook; abort settles before release.',
    independent, outcome: settledBeforeReadReleased, continuation,
    filesAfter: Object.fromEntries(await Promise.all(['first.txt', 'second.txt'].map(async name => [name, await fs.readFile(path.join(root, name), 'utf8')]))),
  }, null, 2) + '\n');
} finally {
  clearTimeout(timer);
  controller.abort();
  release.resolve();
  await fs.rm(root, { recursive: true, force: true });
}
