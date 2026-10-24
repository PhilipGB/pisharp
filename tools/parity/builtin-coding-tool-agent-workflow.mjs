import * as fs from 'node:fs/promises';
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
const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-agent-tools-'));
const workspace = path.join(root, 'workspace');
const files = { 'src/value.txt': 'value=old\n', 'src/related.txt': 'related value\n' };
const tools = ['read', 'bash', 'edit', 'write', 'grep', 'find', 'ls'];
const plan = [
  ['read', { path: 'src/value.txt' }],
  ['find', { pattern: '*.txt', path: 'src' }],
  ['grep', { pattern: 'value', path: 'src/value.txt' }],
  ['ls', { path: 'src' }],
  ['edit', { path: 'src/value.txt', edits: [{ oldText: 'value=old', newText: 'value=new' }] }],
  ['edit', { path: 'src/value.txt', edits: [{ oldText: 'missing text', newText: 'must not appear' }] }],
  ['write', { path: 'generated/result.txt', content: 'created\n' }],
  ['bash', { command: 'test "$(cat src/value.txt)" = value=new && test "$(cat generated/result.txt)" = created && printf \'verified\\n\'' }],
  ['read', { path: 'src/value.txt' }],
].map(([tool, argumentsValue], index) => ({ tool, arguments: argumentsValue, callId: 'workflow_' + index }));
const requests = [];
const events = [];
const model = getModel('openai', 'gpt-4o-mini');
const usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0,
  cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } };
const projectResults = messages => messages.filter(message => message.role === 'toolResult')
  .map(message => ({ callId: message.toolCallId, tool: message.toolName, content: message.content, isError: message.isError }));
let timer;
try {
  for (const [name, text] of Object.entries(files)) {
    const target = path.join(workspace, name);
    await fs.mkdir(path.dirname(target), { recursive: true });
    await fs.writeFile(target, text);
  }
  const session = SessionManager.create(workspace, path.join(root, 'sessions'));
  const streamFn = (_model, context) => {
    const index = requests.length;
    requests.push(projectResults(context.messages));
    const item = plan[index];
    const stream = createAssistantMessageEventStream();
    queueMicrotask(() => {
      const message = { role: 'assistant', content: item
        ? [{ type: 'toolCall', id: item.callId, name: item.tool, arguments: item.arguments }]
        : [{ type: 'text', text: 'workflow complete' }],
      api: 'openai-completions', provider: 'openai', model: model.id, usage,
      stopReason: item ? 'toolUse' : 'stop', timestamp: Date.now() };
      stream.push({ type: 'done', reason: message.stopReason, message });
    });
    return stream;
  };
  const agent = new Agent({ initialState: { model, tools: tools.map(name => createTool(name, workspace)) }, streamFn });
  agent.subscribe(event => {
    if (event.type === 'message_end' && event.message.role !== 'system') session.appendMessage(event.message);
    if (event.type.startsWith('tool_execution_')) events.push(event);
  });
  timer = setTimeout(() => agent.abort(), 15000);
  await agent.prompt('Complete the fixture coding workflow.');
  clearTimeout(timer);
  if (events.filter(event => event.type === 'tool_execution_end').length !== plan.length)
    throw new Error('Pinned workflow did not execute every planned tool');
  const restored = SessionManager.open(session.getSessionFile()).buildSessionContext().messages;
  let resumedResults;
  const resumed = new Agent({
    initialState: { model, tools: tools.map(name => createTool(name, workspace)), messages: restored },
    streamFn: (_model, context) => {
      resumedResults = projectResults(context.messages);
      const stream = createAssistantMessageEventStream();
      queueMicrotask(() => stream.push({ type: 'done', reason: 'stop', message: {
        role: 'assistant', content: [{ type: 'text', text: 'history retained' }],
        api: 'openai-completions', provider: 'openai', model: model.id, usage, stopReason: 'stop', timestamp: Date.now(),
      } }));
      return stream;
    },
  });
  await resumed.prompt('Inspect the saved outcomes without repeating tools.');
  const failures = [];
  const failureInputs = [
    ['read', { path: 'absent' }],
    ['write', { path: 'src/value.txt/child', content: 'must not appear' }],
    ['edit', { path: 'absent', edits: [{ oldText: 'old', newText: 'new' }] }],
    ['bash', {}],
    ['grep', { pattern: 'value', path: 'absent' }],
    ['find', { pattern: '*', path: 'absent' }],
    ['ls', { path: 'absent' }],
  ];
  for (const [tool, argumentsValue] of failureInputs) {
    const item = { tool, arguments: argumentsValue, callId: 'failure_' + tool };
    let requests = 0;
    let outcome;
    let providerResults;
    const failing = new Agent({
      initialState: { model, tools: tools.map(name => createTool(name, workspace)) },
      streamFn: (_model, context) => {
        const first = requests++ === 0;
        if (!first) providerResults = projectResults(context.messages);
        const stream = createAssistantMessageEventStream();
        queueMicrotask(() => stream.push({ type: 'done', reason: first ? 'toolUse' : 'stop', message: {
          role: 'assistant', content: first
            ? [{ type: 'toolCall', id: item.callId, name: tool, arguments: argumentsValue }]
            : [{ type: 'text', text: 'error observed' }],
          api: 'openai-completions', provider: 'openai', model: model.id, usage,
          stopReason: first ? 'toolUse' : 'stop', timestamp: Date.now(),
        } }));
        return stream;
      },
    });
    failing.subscribe(event => {
      if (event.type === 'tool_execution_end') outcome = { callId: event.toolCallId, tool: event.toolName,
        result: event.result, isError: event.isError };
    });
    await failing.prompt('Observe a failed tool without changing files.');
    if (!outcome?.isError || requests !== 2) throw new Error('Invalid failure observation: ' + tool);
    failures.push({ item, outcome, providerResults });
  }
  const finalFiles = {};
  for (const name of [...Object.keys(files), 'generated/result.txt'])
    finalFiles[name] = { text: await fs.readFile(path.join(workspace, name), 'utf8'), mode: (await fs.stat(path.join(workspace, name))).mode & 0o777 };
  const normalize = value => JSON.parse(JSON.stringify(value).replaceAll(workspace, '<workspace>'));
  await fs.writeFile(output, JSON.stringify(normalize({
    schema: 1, pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: ['packages/agent/src/agent.ts', 'packages/agent/src/agent-loop.ts', 'packages/coding-agent/src/core/session-manager.ts'],
    fixture: 'tools/parity/builtin-coding-tool-agent-workflow.mjs',
    scope: 'Actual pinned Agent tool loop and SessionManager file save/open, deterministic stream provider; physical HTTP is a separate gate.',
    files, tools, plan, requests,
    outcomes: events.filter(event => event.type === 'tool_execution_end').map(event => ({
      callId: event.toolCallId, tool: event.toolName, result: event.result, isError: event.isError,
    })),
    toolEvents: events.map(event => ({ type: event.type, callId: event.toolCallId, tool: event.toolName,
      ...(event.type === 'tool_execution_update' ? { partialResult: event.partialResult } : {}) })),
    restoredResults: projectResults(restored), resumedResults, failures, finalFiles,
    sessionFileWritten: (await fs.stat(session.getSessionFile())).size > 0,
  }), null, 2) + '\n');
} finally {
  clearTimeout(timer);
  await fs.rm(root, { recursive: true, force: true });
}
