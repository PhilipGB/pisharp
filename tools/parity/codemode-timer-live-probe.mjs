import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { stripVTControlCharacters } from 'node:util';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { createCodemodeToolDefinition } = await load('packages/coding-agent/src/extensions/codemode/tool.ts');
const { wrapToolDefinition } = await load('packages/coding-agent/src/core/tools/tool-definition-wrapper.ts');
const { runToolCall } = await load('packages/agent/src/agent-loop.ts');
const { ToolExecutionComponent } = await load('packages/coding-agent/src/modes/interactive/components/tool-execution.ts');
const { initTheme } = await load('packages/coding-agent/src/modes/interactive/theme/theme.ts');
const { formatToolDuration } = await load('packages/coding-agent/src/core/tools/render-utils.ts');
initTheme('dark');
const frames = [];
let nestedEntered = false;
let initialBeforeNested = false;
const definition = createCodemodeToolDefinition();
let component;
const draw = () => {
  if (component) frames.push(stripVTControlCharacters(component.render(120).join('\n')));
};
component = new ToolExecutionComponent('codemode', 'script', { code: '' }, {}, definition, { requestRender: draw }, '/tmp');
component.markExecutionStarted();
const ctx = { tools: [{ name: 'hold', label: 'hold', description: 'hold', parameters: { type: 'object', properties: {} } }],
  sessionManager: { getBranch: () => [] }, executeTool: async () => {
    nestedEntered = true;
    await new Promise(resolve => setTimeout(resolve, 1300));
    return { toolCall: { id: 'script/hold' }, isError: false,
      result: { content: [{ type: 'text', text: 'held' }] } };
  } };
const tool = wrapToolDefinition(definition, () => ctx);
const call = { type: 'toolCall', id: 'script', name: 'codemode', arguments: { code: 'await tools.hold({}); return 42;' } };
const outcome = await runToolCall(call, { tools: [tool], assistantMessage: { role: 'assistant', content: [call] },
  context: { messages: [], tools: [tool] }, onUpdate: result => {
    if (!nestedEntered && result.details?.calls.length === 0) initialBeforeNested = true;
    component.updateResult(result, true);
    draw();
  } });
component.updateResult({ ...outcome.result, durationMs: outcome.durationMs }, false);
draw();
const total = 'Took ' + formatToolDuration(outcome.durationMs);
console.log(JSON.stringify({
  initialBeforeNested,
  initialDisplayed: frames.some(frame => frame.includes('Elapsed 0.0s')),
  elapsedRedrawn: frames.some(frame => frame.includes('Elapsed 1.0s')),
  finalRecorded: frames.at(-1).includes(total),
  noFinalElapsed: !frames.at(-1).includes('Elapsed'),
  successful: !outcome.isError,
  durationRecorded: outcome.durationMs >= 1300
}));
