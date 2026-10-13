import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';
import { stripVTControlCharacters } from 'node:util';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { formatToolDuration, startToolTimer, toolTimerText } = await load('packages/coding-agent/src/core/tools/render-utils.ts');
const { codemodeRenderers } = await load('packages/coding-agent/src/extensions/codemode/renderer.ts');
const { ToolExecutionComponent } = await load('packages/coding-agent/src/modes/interactive/components/tool-execution.ts');
const { initTheme } = await load('packages/coding-agent/src/modes/interactive/theme/theme.ts');
initTheme('dark');
const formatted = JSON.parse(readFileSync(process.argv[3], 'utf8')).map(formatToolDuration);
const originalDateNow = Date.now;
const originalInterval = globalThis.setInterval;
const originalClear = globalThis.clearInterval;
let now = 0;
let redraws = 0;
const intervals = new Map();
Date.now = () => now;
globalThis.setInterval = (callback, period) => {
  const timer = { callback, period, due: now + period };
  intervals.set(timer, timer);
  return timer;
};
globalThis.clearInterval = timer => intervals.delete(timer);
function advance(milliseconds) {
  const end = now + milliseconds;
  for (;;) {
    const next = [...intervals.values()].filter(timer => timer.due <= end).sort((a, b) => a.due - b.due)[0];
    if (!next) break;
    now = next.due;
    next.due += next.period;
    next.callback();
  }
  now = end;
}
const tui = { requestRender: () => redraws++ };
function timerLine(component) {
  return stripVTControlCharacters(component.render(120).join('\n')).split('\n').map(line => line.trim())
    .findLast(line => /^(Elapsed|Took) /.test(line)) ?? null;
}
try {
  const component = new ToolExecutionComponent('codemode', 'script', { code: '' }, {}, codemodeRenderers, tui, '/tmp');
  component.markExecutionStarted();
  component.updateResult({ content: [], details: { calls: [] }, isError: false }, true);
  const initial = timerLine(component);
  const initialRedraws = redraws;
  advance(3000);
  const running = timerLine(component);
  const refreshed = redraws > initialRedraws;
  component.updateResult({ content: [{ type: 'text', text: 'done' }], details: { calls: [] }, isError: false, durationMs: 5100 }, false);
  const final = timerLine(component);
  const finalRedraws = redraws;
  advance(10000);
  const settled = redraws === finalRedraws;
  const reloaded = new ToolExecutionComponent('codemode', 'script', { code: 'return 42' }, {}, codemodeRenderers, tui, '/tmp');
  reloaded.updateResult({ content: [{ type: 'text', text: '42' }], details: { calls: [] }, isError: false, durationMs: 5100 }, false);
  const reload = timerLine(reloaded);
  const context = { state: {}, executionStarted: true, invalidate: () => {} };
  startToolTimer(context);
  toolTimerText(context, true);
  advance(3000);
  context.isError = true;
  const frozenError = toolTimerText(context, true);
  advance(2000);
  context.isError = false;
  const frozenErrorLater = toolTimerText(context, true);
  const finalError = toolTimerText(context, false);
  console.log(JSON.stringify({ formatted, row: { initial, running, refreshed, final, settled, reload },
    partialError: { frozenError, frozenErrorLater, finalError } }));
} finally {
  Date.now = originalDateNow;
  globalThis.setInterval = originalInterval;
  globalThis.clearInterval = originalClear;
}
