import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

export async function captureBashProcessLifetime(factories) {
  if (process.platform !== 'linux') throw new Error('Physical Linux process evidence required');
  const quote = value => "'" + value.replaceAll("'", "'\\''") + "'";
  const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-bash-descendants-'));
  const controller = new AbortController();
  const ready = Promise.withResolvers();
  const childPath = path.join(root, 'child.pid');
  const grandchildPath = path.join(root, 'grandchild.pid');
  const grandchild = `printf '%s\\n' "$$" > ${quote(grandchildPath)}; printf 'detached-ready\\n'; exec sleep 60`;
  const child = `printf '%s\\n' "$$" > ${quote(childPath)}; setsid /bin/sh -c ${quote(grandchild)} & wait`;
  const command = `/bin/sh -c ${quote(child)} & wait`;
  let deadline;
  let running;
  const updates = [];
  const state = async pid => {
    try {
      const stat = await fs.readFile(`/proc/${pid}/stat`, 'utf8');
      return stat.slice(stat.lastIndexOf(')') + 2).split(' ')[0];
    } catch (error) {
      if (error.code === 'ENOENT' || error.code === 'ESRCH') return 'gone';
      throw error;
    }
  };
  try {
    const tool = factories.bash(root);
    running = tool.execute('detached-grandchild', { command }, controller.signal, update => {
      updates.push(update);
      if (update.content.some(block => block.type === 'text' && block.text.includes('detached-ready'))) ready.resolve();
    }).then(result => ({ result }), error => ({ error: error.message }));
    const timeout = new Promise((_, reject) => { deadline = setTimeout(() => reject(new Error('Bash descendant barrier timed out')), 10000); });
    await Promise.race([ready.promise, timeout]);
    const childId = Number(await fs.readFile(childPath, 'utf8'));
    const grandchildId = Number(await fs.readFile(grandchildPath, 'utf8'));
    controller.abort();
    const outcome = await Promise.race([running, timeout]);
    return { caseId: 'bash-cancel-detached-grandchild', platform: process.platform,
      command: command.replaceAll(root, '<workspace>'), outcome, updates,
      processesAtToolSettlement: { child: await state(childId), detachedGrandchild: await state(grandchildId) },
      continuation: await tool.execute('after-abort', { command: 'printf continued' }),
      cleanup: 'Both recorded fixture PIDs killed in finally after recording settlement states; temporary workspace removed' };
  } finally {
    clearTimeout(deadline);
    controller.abort();
    for (const file of [grandchildPath, childPath]) {
      try {
        const pid = Number(await fs.readFile(file, 'utf8'));
        if (Number.isSafeInteger(pid) && pid > 1) process.kill(pid, 'SIGKILL');
      } catch (error) { if (!['ENOENT', 'ESRCH'].includes(error.code)) throw error; }
    }
    await running;
    await fs.rm(root, { recursive: true, force: true });
  }
}
