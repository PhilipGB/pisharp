import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

export async function captureMutationCoordination(factories, load) {
  const { withFileMutationQueue } = await load('packages/coding-agent/src/core/tools/file-mutation-queue.ts');
  const results = [];
  for (const [firstTool, secondTool] of [['write', 'write'], ['write', 'edit'], ['edit', 'write'], ['edit', 'edit']]) {
    const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-mutations-'));
    const target = path.join(root, 'shared');
    const entered = Promise.withResolvers();
    const release = Promise.withResolvers();
    let blocker;
    try {
      await fs.writeFile(target, 'alpha\nbeta\n');
      blocker = withFileMutationQueue(target, async () => { entered.resolve(); await release.promise; });
      await entered.promise;
      let firstSettled = false, secondSettled = false;
      const firstArgs = firstTool === 'write' ? { path: 'shared', content: 'first\n' } :
        { path: 'shared', edits: [{ oldText: 'alpha', newText: 'ALPHA' }] };
      const secondArgs = secondTool === 'write' ? { path: 'shared', content: 'second\n' } :
        { path: 'shared', edits: [{ oldText: firstTool === 'write' ? 'first' : 'beta', newText: firstTool === 'write' ? 'edited' : 'BETA' }] };
      const first = factories[firstTool](root).execute('first', firstArgs).finally(() => { firstSettled = true; });
      const second = factories[secondTool](root).execute('second', secondArgs).finally(() => { secondSettled = true; });
      await factories.write(root).execute('unrelated', { path: 'unrelated', content: 'independent' });
      const held = { firstSettled, secondSettled, shared: await fs.readFile(target, 'utf8'),
        unrelated: await fs.readFile(path.join(root, 'unrelated'), 'utf8') };
      release.resolve();
      const [firstResult, secondResult] = await Promise.all([first, second]);
      results.push({ caseId: 'mutation-order-' + firstTool + '-' + secondTool, firstTool, secondTool,
        firstArguments: firstArgs, secondArguments: secondArgs, held, firstResult, secondResult,
        finalContent: await fs.readFile(target, 'utf8') });
    } finally {
      release.resolve();
      await blocker;
      await fs.rm(root, { recursive: true, force: true });
    }
  }
  const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-cancelled-mutation-'));
  const target = path.join(root, 'shared');
  const entered = Promise.withResolvers(), release = Promise.withResolvers();
  let blocker;
  try {
    await fs.writeFile(target, 'original');
    blocker = withFileMutationQueue(target, async () => { entered.resolve(); await release.promise; });
    await entered.promise;
    const controller = new AbortController();
    let cancelledSettled = false;
    const cancelled = factories.write(root).execute('cancelled', { path: 'shared', content: 'must not commit' }, controller.signal)
      .then(result => ({ result }), error => ({ error: error.message })).finally(() => { cancelledSettled = true; });
    const following = factories.write(root).execute('following', { path: 'shared', content: 'following' });
    await factories.write(root).execute('unrelated', { path: 'other', content: 'independent' });
    controller.abort();
    const beforeRelease = { cancelledSettled, shared: await fs.readFile(target, 'utf8') };
    release.resolve();
    results.push({ caseId: 'mutation-cancelled-waiter', beforeRelease, cancelled: await cancelled,
      following: await following, finalContent: await fs.readFile(target, 'utf8') });
  } finally {
    release.resolve();
    await blocker;
    await fs.rm(root, { recursive: true, force: true });
  }
  return { pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/{write,edit,file-mutation-queue}.ts',
    scope: 'Real definitions/filesystem with deterministic queue barriers; separate integration evidence, not added to the direct corpus count.', results };
}
