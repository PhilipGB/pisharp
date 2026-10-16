import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { TuiAltScreen } = await load('packages/tui/src/tui-alt-screen.ts');
const { Text } = await load('packages/tui/src/components/text.ts');
const { MouseRegion } = await load('packages/tui/src/components/mouse-region.ts');
const { VirtualTerminal } = await load('packages/tui/test/virtual-terminal.ts');
const results = [];
for (const id of ['unhandled-editorpanel', 'unhandled-extension-input', 'unhandled-extension-batch', 'unhandled-inline-select', 'unhandled-progress', 'unhandled-search', 'empty-selection', 'handled-selection']) {
  const terminal = new VirtualTerminal(40, 12);
  const tui = new TuiAltScreen(terminal);
  tui.addChild(new Text(Array.from({ length: 40 }, (_, index) => `line ${index + 1}`).join('\n'), 0, 0));
  tui.start();
  await terminal.waitForRender();
  const inputs = [];
  const content = { focused: false, render: () => ['overlay'], invalidate: () => {}, handleInput: data => inputs.push(data) };
  const overlay = id === 'handled-selection' ? new MouseRegion(content, event => event.type === 'wheel' ? { handled: true } : undefined) : content;
  tui.showOverlay(overlay, { anchor: 'top-left', width: 10 });
  await terminal.waitForRender();
  const before = tui.viewportTop;
  terminal.sendInput('\x1b[<64;3;1M');
  await terminal.waitForRender();
  if (inputs.length !== 0) throw new Error('Wheel reached overlay keyboard input.');
  results.push({ id, scrolledUp: tui.viewportTop < before });
  tui.stop();
}
console.log(JSON.stringify(results));
