import * as fs from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

export async function captureBashSessionEnvironment(factories) {
  const names = ['PI_SESSION_ID', 'PI_SESSION_FILE', 'PI_PROVIDER', 'PI_MODEL', 'PI_REASONING_LEVEL'];
  const inherited = Object.fromEntries(names.map(name => [name, process.env[name]]));
  const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-bash-environment-'));
  const command = "printf '%s|%s|%s|%s|%s' \"${PI_SESSION_ID-unset}\" \"${PI_SESSION_FILE-unset}\" \"${PI_PROVIDER-unset}\" \"${PI_MODEL-unset}\" \"${PI_REASONING_LEVEL-unset}\"";
  try {
    for (const name of names) process.env[name] = 'stale-fixture-value';
    const context = { cwd: root, model: { provider: 'old-provider', id: 'old-model' }, thinkingLevel: 'low',
      sessionManager: { getSessionId: () => 'session-one', getSessionFile: () => 'fixture.session.json' } };
    const tool = factories.bash(root);
    const cases = [];
    for (const caseId of ['initial-selection', 'changed-selection', 'new-session-without-options', 'no-tool-context']) {
      if (caseId === 'changed-selection') {
        context.model = { provider: 'new-provider', id: 'new-model' };
        context.thinkingLevel = 'high';
      } else if (caseId === 'new-session-without-options') {
        context.model = undefined;
        context.thinkingLevel = undefined;
        context.sessionManager = { getSessionId: () => 'session-two', getSessionFile: () => undefined };
      }
      const ctx = caseId === 'no-tool-context' ? undefined : context;
      const result = await tool.execute(caseId, { command }, undefined, undefined, ctx);
      cases.push({ caseId, context: ctx ? { sessionId: ctx.sessionManager.getSessionId(),
        sessionFile: ctx.sessionManager.getSessionFile() ?? null, model: ctx.model ?? null,
        thinkingLevel: ctx.thinkingLevel ?? null } : null, result });
    }
    return { command, inheritedFixtureValues: 'All five names set to stale-fixture-value; original host values restored without logging', cases };
  } finally {
    for (const name of names) {
      if (inherited[name] === undefined) delete process.env[name];
      else process.env[name] = inherited[name];
    }
    await fs.rm(root, { recursive: true, force: true });
  }
}

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
