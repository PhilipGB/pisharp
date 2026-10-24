import * as fs from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, output] = process.argv.slice(2);
verifyPiCheckout(pi);
await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/experimental/source-resolver.ts')).href);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { Agent } = await load('packages/agent/src/index.ts');
const { createAssistantMessageEventStream, getModel } = await load('packages/ai/src/compat.ts');
const { createTool } = await load('packages/coding-agent/src/core/tools/index.ts');
const { SessionManager } = await load('packages/coding-agent/src/core/session-manager.ts');
const workspace = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-concurrent-tools-'));
const tools = ['read', 'bash', 'edit', 'write', 'grep', 'find', 'ls'];
const files = { 'data/first.txt': 'alpha\n', 'data/second.txt': 'beta\n' };
const plan = [
  ['bash', { command: "printf 'batch-ready\\n'; read -r release < gate; printf 'batch-released\\n'" }],
  ['read', { path: 'data/first.txt' }],
  ['read', { path: 'data/second.txt' }],
  ['grep', { pattern: 'alpha', path: 'data/first.txt' }],
  ['find', { pattern: '*.txt', path: 'data' }],
  ['ls', { path: 'data' }],
  ['write', { path: 'out/first.txt', content: 'first mutation\n' }],
  ['write', { path: 'out/second.txt', content: 'second mutation\n' }],
].map(([tool, argumentsValue], index) => ({ tool, arguments: argumentsValue, callId: 'parallel_' + index }));
const usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
  cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
const ready = Promise.withResolvers();
const writes = Promise.withResolvers();
const events = [];
const requests = [];
const model = getModel('openai', 'gpt-4o-mini');
let timer;
let agent;
let running;
try {
  for (const [name, text] of Object.entries(files)) {
    await fs.mkdir(path.dirname(path.join(workspace, name)), { recursive: true });
    await fs.writeFile(path.join(workspace, name), text);
  }
  execFileSync('mkfifo', [path.join(workspace, 'gate')]);
  agent = new Agent({ initialState: { model, tools: tools.map(name => createTool(name, workspace)) },
    streamFn: (_model, context) => {
      const first = requests.length === 0;
      requests.push(context.messages.filter(message => message.role === 'toolResult')
        .map(message => ({ callId: message.toolCallId, tool: message.toolName, content: message.content, isError: message.isError })));
      const stream = createAssistantMessageEventStream();
      queueMicrotask(() => stream.push({ type: 'done', reason: first ? 'toolUse' : 'stop', message: {
        role: 'assistant', content: first ? plan.map(item => ({ type: 'toolCall', id: item.callId, name: item.tool, arguments: item.arguments }))
          : [{ type: 'text', text: 'parallel tools complete' }],
        api: 'openai-completions', provider: 'openai', model: model.id, usage,
        stopReason: first ? 'toolUse' : 'stop', timestamp: Date.now(),
      } }));
      return stream;
    } });
  agent.subscribe(event => {
    if (!event.type.startsWith('tool_execution_')) return;
    events.push(event);
    if (event.type === 'tool_execution_update' && event.partialResult.content
      .some(part => part.type === 'text' && part.text.includes('batch-ready'))) ready.resolve();
    if (events.filter(item => item.type === 'tool_execution_end' && item.toolName === 'write').length === 2) writes.resolve();
  });
  const deadline = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Concurrent tools failed to reach barriers')), 15000); });
  running = agent.prompt('Run independent tools together while Bash waits.');
  await Promise.race([Promise.all([ready.promise, writes.promise]), deadline]);
  const beforeRelease = events.filter(event => event.type === 'tool_execution_end').map(event => event.toolCallId);
  if (beforeRelease.includes('parallel_0') || !beforeRelease.includes('parallel_6') || !beforeRelease.includes('parallel_7'))
    throw new Error('Independent writes did not finish while Bash was blocked');
  await fs.writeFile(path.join(workspace, 'gate'), 'release\n');
  await Promise.race([running, deadline]);
  const outcomes = events.filter(event => event.type === 'tool_execution_end').map(event =>
    ({ callId: event.toolCallId, tool: event.toolName, content: event.result.content, details: event.result.details ?? null, isError: event.isError }));
  if (outcomes.length !== plan.length || requests.length !== 2) throw new Error('Incomplete concurrent workflow');
  const finalFiles = {};
  for (const name of [...Object.keys(files), 'out/first.txt', 'out/second.txt']) finalFiles[name] = await fs.readFile(path.join(workspace, name), 'utf8');
  const session = SessionManager.create(workspace, path.join(workspace, 'state'));
  session.appendMessage({ role: 'user', content: 'Earlier unrelated conversation.', timestamp: Date.now() });
  const firstKeptEntryId = session.appendMessage({ role: 'assistant', content: [{ type: 'text', text: 'x'.repeat(100000) }],
    api: 'openai-completions', provider: 'openai', model: model.id, usage, stopReason: 'stop', timestamp: Date.now() });
  for (const message of agent.state.messages) if (message.role !== 'system') session.appendMessage(message);
  session.appendCompaction('Earlier unrelated turn summarized.', firstKeptEntryId, 25000);
  const retained = SessionManager.open(session.getSessionFile()).buildSessionContext().messages;
  const retainedResults = retained.filter(message => message.role === 'toolResult');
  if (retainedResults.length !== plan.length) throw new Error('Compaction lost concurrent tool outcomes');
  await fs.writeFile(output, JSON.stringify({ schema: 1, pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/agent/src/agent-loop.ts and actual pinned tools', fixture: 'tools/parity/builtin-coding-tool-concurrent-agent.mjs',
    barrier: 'Bash holds a real FIFO; two independent write results and the Bash-ready update must arrive before its writer is released.',
    tools, files, plan, beforeRelease, outcomes, requests, finalFiles,
    compaction: { firstKeptRole: 'assistant', actualSessionFileReload: true,
      retainedResultIds: retainedResults.map(message => message.toolCallId) },
  }, null, 2) + '\n');
} finally {
  clearTimeout(timer);
  agent?.abort();
  if (running) await running;
  await fs.rm(workspace, { recursive: true, force: true });
}
