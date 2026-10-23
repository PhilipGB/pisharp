import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

export async function captureMutationOwnership(factories) {
  let group = process.getgid();
  const groupAttempts = [];
  const probe = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-group-probe-'));
  try {
    const target = path.join(probe, 'file');
    await fs.writeFile(target, 'fixture');
    for (const candidate of process.getgroups().filter(value => value !== process.getgid())) {
      try { await fs.chown(target, process.getuid(), candidate); group = candidate; break; }
      catch (error) { groupAttempts.push({ group: candidate, code: error.code }); }
    }
  } finally { await fs.rm(probe, { recursive: true, force: true }); }
  const cases = [];
  for (const tool of ['write', 'edit']) {
    for (const mode of [0o660, 0o4755, 0o2755, 0o2644]) {
      const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-ownership-'));
      try {
        const target = path.join(root, 'file.txt');
        await fs.writeFile(target, 'alpha\n');
        await fs.chown(target, process.getuid(), group);
        await fs.chmod(target, mode);
        const before = await fs.stat(target);
        const argumentsValue = tool === 'write' ? { path: 'file.txt', content: 'beta\n' }
          : { path: 'file.txt', edits: [{ oldText: 'alpha', newText: 'beta' }] };
        const result = await factories[tool](root).execute('ownership', argumentsValue);
        const after = await fs.stat(target);
        cases.push({ caseId: tool + '-ownership-' + mode.toString(8), tool, mode,
          arguments: argumentsValue, result,
          before: { uid: before.uid, gid: before.gid, mode: before.mode & 0o7777 },
          after: { uid: after.uid, gid: after.gid, mode: after.mode & 0o7777 },
          inodePreserved: before.ino === after.ino, finalText: await fs.readFile(target, 'utf8') });
      } finally { await fs.rm(root, { recursive: true, force: true }); }
    }
  }
  return { pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: ['packages/coding-agent/src/core/tools/write.ts', 'packages/coding-agent/src/core/tools/edit.ts'],
    fixture: 'tools/parity/builtin-coding-tool-mutation-cases.mjs',
    ownerParameters: 'Use an assignable supplementary group when available, otherwise record the environment limitation and use the primary group.',
    alternateGroupAssigned: group !== process.getgid(), groupAttempts, cases };
}

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
  for (const cancelledTool of ['write', 'edit']) {
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
      const argumentsValue = cancelledTool === 'write' ? { path: 'shared', content: 'must not commit' } :
        { path: 'shared', edits: [{ oldText: 'original', newText: 'must not commit' }] };
      const cancelled = factories[cancelledTool](root).execute('cancelled', argumentsValue, controller.signal)
        .then(result => ({ result }), error => ({ error: error.message })).finally(() => { cancelledSettled = true; });
      const following = factories.write(root).execute('following', { path: 'shared', content: 'following' });
      await factories.write(root).execute('unrelated', { path: 'other', content: 'independent' });
      controller.abort();
      const beforeRelease = { cancelledSettled, shared: await fs.readFile(target, 'utf8') };
      release.resolve();
      results.push({ caseId: cancelledTool === 'write' ? 'mutation-cancelled-waiter' : 'mutation-cancelled-edit-waiter', beforeRelease, cancelled: await cancelled,
        following: await following, finalContent: await fs.readFile(target, 'utf8') });
    } finally {
      release.resolve();
      await blocker;
      await fs.rm(root, { recursive: true, force: true });
    }
  }
  const removedRoot = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-removed-parent-'));
  try {
    const parent = path.join(removedRoot, 'dir');
    const target = path.join(parent, 'file');
    await fs.mkdir(parent);
    await fs.writeFile(target, 'original');
    const tool = factories.edit(removedRoot, { operations: {
      access: file => fs.access(file, 6),
      readFile: async file => {
        const bytes = await fs.readFile(file);
        await fs.rm(parent, { recursive: true });
        return bytes;
      },
      writeFile: (file, content) => fs.writeFile(file, content, 'utf8')
    } });
    const outcome = await tool.execute('removed-parent', { path: 'dir/file', edits: [{ oldText: 'original', newText: 'updated' }] })
      .then(result => ({ success: true, result }), error => ({ success: false, error: error.message.replaceAll(removedRoot, '<workspace>') }));
    await factories.write(removedRoot).execute('recovery', { path: 'other', content: 'continue' });
    results.push({ caseId: 'mutation-parent-removed-after-read', ...outcome,
      finalEntries: await fs.readdir(removedRoot), recoveryContent: await fs.readFile(path.join(removedRoot, 'other'), 'utf8') });
  } finally { await fs.rm(removedRoot, { recursive: true, force: true }); }
  return { pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/{write,edit,file-mutation-queue}.ts',
    scope: 'Real definitions/filesystem with deterministic queue barriers; separate integration evidence, not added to the direct corpus count.', results };
}
