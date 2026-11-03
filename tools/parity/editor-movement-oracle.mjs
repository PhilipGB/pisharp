#!/usr/bin/env node
/**
 * Generate the pinned-Pi editor movement/history oracle used by
 * EditorMovementParityTests.
 *
 * Usage:
 *   node tools/parity/editor-movement-oracle.mjs --pi /path/to/pinned/pi \
 *     --output docs/parity/fixtures/keys-editor-movement-pi-oracle-<date>.json
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const option = (name) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};
const piCheckout = option('--pi');
const output = option('--output');
const scenarioPath = option('--scenarios') ?? join(here, 'editor-movement-scenarios.json');
if (!piCheckout || !output) {
  console.error('Usage: node tools/parity/editor-movement-oracle.mjs --pi <checkout> --output <file>');
  process.exit(2);
}

verifyPiCheckout(piCheckout);
const importFromPi = (path) => import(pathToFileURL(join(piCheckout, path)).href);
const { Editor, wordWrapLine } = await importFromPi('packages/tui/src/components/editor.ts');
const { TuiMainScreen } = await importFromPi('packages/tui/src/tui-main-screen.ts');
const { VirtualTerminal } = await importFromPi('packages/tui/test/virtual-terminal.ts');
const { defaultEditorTheme } = await importFromPi('packages/tui/test/test-themes.ts');
const { KeybindingsManager, TUI_KEYBINDINGS, setKeybindings } =
  await importFromPi('packages/tui/src/keybindings.ts');
const { visibleWidth } = await importFromPi('packages/tui/src/utils.ts');

const CURSOR_MARKER = '\x1b_pi:c\x07';
const FAKE_CURSOR_START = '\x1b_pi:fc\x07';
const FAKE_CURSOR_END = '\x1b_pi:/fc\x07';
const ANSI = /\x1b\[[0-?]*[ -/]*[@-~]/g;
const OSC = /\x1b\][^\x07]*(?:\x07|\x1b\\)/g;
const APC = /\x1b_[^\x07]*\x07/g;

const keys = {
  left: '\x1b[D', right: '\x1b[C', up: '\x1b[A', down: '\x1b[B',
  'ctrl-b': '\x02', 'ctrl-f': '\x06',
  'alt-left': '\x1b[1;3D', 'ctrl-left': '\x1b[1;5D', 'alt-b': '\x1bb',
  'alt-right': '\x1b[1;3C', 'ctrl-right': '\x1b[1;5C', 'alt-f': '\x1bf',
  home: '\x1b[H', end: '\x1b[F', 'ctrl-a': '\x01', 'ctrl-e': '\x05',
  'ctrl-home': '\x1b[1;5H', 'ctrl-end': '\x1b[1;5F',
  pageup: '\x1b[5~', pagedown: '\x1b[6~',
  'ctrl-pageup': '\x1b[5;5~', 'ctrl-pagedown': '\x1b[6;5~',
  'jump-forward': '\x1d', 'jump-backward': '\x1b\x1d',
  'ctrl-p': '\x10', 'ctrl-n': '\x0e', 'ctrl-x': '\x18', 'ctrl-r': '\x12', 'ctrl-l': '\x0c',
  f7: '\x1b[18~', f8: '\x1b[19~', f9: '\x1b[20~', f10: '\x1b[21~',
  f11: '\x1b[23~', f12: '\x1b[24~', 'alt-j': '\x1bj', 'alt-k': '\x1bk'
};

const strip = (line) => line
  .replace(ANSI, '')
  .replace(OSC, '')
  .replace(APC, '')
  .replaceAll(FAKE_CURSOR_START, '')
  .replaceAll(FAKE_CURSOR_END, '')
  .replaceAll(CURSOR_MARKER, '');

const lineInfo = (text, offset) => {
  const before = text.slice(0, offset);
  const line = (before.match(/\n/g) || []).length;
  const col = offset - (before.lastIndexOf('\n') + 1);
  return { line, col };
};

const offsetOf = (text, cursor) => {
  const lines = text.split('\n');
  let offset = 0;
  for (let index = 0; index < cursor.line; index++) offset += (lines[index] ?? '').length + 1;
  return offset + cursor.col;
};

const visualPosition = (text, offset, width) => {
  const lines = text.split('\n');
  const cursor = lineInfo(text, offset);
  let row = 0;
  for (let index = 0; index < cursor.line; index++) row += visualChunks(lines[index] ?? '', width).length;
  const chunks = visualChunks(lines[cursor.line] ?? '', width);
  let chunkIndex = chunks.length - 1;
  for (let index = 0; index < chunks.length; index++) {
    const chunk = chunks[index];
    const length = chunk.endIndex - chunk.startIndex;
    const local = cursor.col - chunk.startIndex;
    const last = index === chunks.length - 1;
    if (local >= 0 && (local < length || (last && local === length))) {
      chunkIndex = index;
      break;
    }
  }
  const chunk = chunks[chunkIndex];
  const prefix = (lines[cursor.line] ?? '').slice(chunk.startIndex, Math.min(cursor.col, chunk.endIndex));
  return { row: row + chunkIndex, column: visibleWidth(prefix) };
};

const visualChunks = (line, width) => {
  if (line.length === 0) return [{ startIndex: 0, endIndex: 0, text: '' }];
  if (visibleWidth(line) <= width) return [{ startIndex: 0, endIndex: line.length, text: line }];
  return wordWrapLine(line, width);
};

const clickAt = (editor, text, cursor, width, rows) => {
  editor.render(width + 1);
  editor.handleInput('\x01');
  for (let index = 0; index < 512; index++) editor.handleInput('\x1b[A');
  editor.render(width + 1);
  const position = visualPosition(text, offsetOf(text, cursor), width);
  editor.handleMouse({
    type: 'click', button: 'left', x: position.column, y: position.row + 1,
    screenX: position.column, screenY: position.row + 1, width: width + 1, height: rows,
    shift: false, alt: false, ctrl: false, clickCount: 1
  });
  editor.render(width + 1);
  const actual = editor.getCursor();
  if (actual.line !== cursor.line || actual.col !== cursor.col) {
    throw new Error(`Could not position cursor at ${cursor.line}:${cursor.col}; got ${actual.line}:${actual.col}`);
  }
};

const state = (editor, width) => {
  editor.focused = true;
  const rendered = editor.render(width + 1);
  const content = rendered.slice(1, -1);
  let viewportRow = -1;
  let viewportColumn = 0;
  for (let index = 0; index < content.length; index++) {
    const marker = content[index].indexOf(CURSOR_MARKER);
    if (marker < 0) continue;
    viewportRow = index;
    viewportColumn = visibleWidth(content[index].slice(0, marker));
    break;
  }
  const text = editor.getText();
  const cursor = editor.getCursor();
  return {
    text,
    cursor,
    visual: visualPosition(text, offsetOf(text, cursor), width),
    viewportCursor: { row: viewportRow, column: viewportColumn },
    viewport: content.map((line) => strip(line).replace(/\s+$/u, '')),
    selection: null
  };
};

const scenarios = JSON.parse(readFileSync(scenarioPath, 'utf8'));
const results = {};
for (const [name, scenario] of Object.entries(scenarios)) {
  const width = scenario.width ?? 40;
  const rows = scenario.rows ?? 24;
  let currentWidth = width;
  const terminal = new VirtualTerminal(width + 1, rows);
  const editor = new Editor(new TuiMainScreen(terminal), defaultEditorTheme);
  setKeybindings(new KeybindingsManager(TUI_KEYBINDINGS));
  editor.setText(scenario.text);
  if (scenario.cursor) clickAt(editor, editor.getText(), scenario.cursor, width, rows);
  editor.render(currentWidth + 1);
  for (const entry of scenario.history ?? []) editor.addToHistory(entry);
  if (scenario.bindings) setKeybindings(new KeybindingsManager(TUI_KEYBINDINGS, scenario.bindings));

  const expected = [];
  for (const step of scenario.steps) {
    if (step.startsWith('type:')) {
      editor.handleInput(step.slice(5));
    } else if (step.startsWith('char:')) {
      editor.handleInput(step.slice(5));
    } else if (step.startsWith('set:')) {
      editor.setText(step.slice(4));
    } else if (step === 'submit') {
      const trimmed = editor.getText().trim();
      if (trimmed) editor.addToHistory(trimmed);
      editor.setText('');
    } else if (step.startsWith('resize:')) {
      currentWidth = Number(step.slice(7));
      terminal.resize(currentWidth + 1, rows);
      editor.render(currentWidth + 1);
    } else if (step.startsWith('rows:')) {
      const nextRows = Number(step.slice(5));
      terminal.resize(currentWidth + 1, nextRows);
      editor.render(currentWidth + 1);
    } else {
      const sequence = keys[step];
      if (sequence === undefined) throw new Error(`Unknown key token: ${step}`);
      editor.handleInput(sequence);
    }
    expected.push(state(editor, currentWidth));
  }
  results[name] = { ...scenario, expected };
}

const fixture = {
  capability: 'keys-editor-movement-history',
  piSha: execFileSync('git', ['-C', piCheckout, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
  source: [
    'packages/tui/src/components/editor.ts',
    'packages/tui/src/word-navigation.ts',
    'packages/tui/src/keybindings.ts',
    'packages/tui/src/utils.ts',
    'packages/coding-agent/src/modes/interactive/components/custom-editor.ts',
    'packages/coding-agent/src/core/keybindings.ts',
    'packages/coding-agent/docs/keybindings.md'
  ],
  scenarios: results,
  findings: [
    {
      id: 'cjk-word-segmentation',
      classification: 'out_of_matrix_unicode_dictionary_difference',
      required: false,
      piObservation: 'Pinned Pi delegates word boundaries to Intl.Segmenter (ICU); CJK runs break at dictionary boundaries.',
      pisharpObservation: 'EditorBuffer treats a contiguous Han run as one word for word movement.',
      rationale: 'The required matrix covers grapheme, combining, emoji and multiline boundaries; reproducing ICU dictionary segmentation would need an ICU dependency or embedded dictionary. The difference is retained rather than silently normalized.'
    },
    {
      id: 'keyboard-selection-extension',
      classification: 'pisharp_extension_not_supported_by_pinned_pi_editor',
      required: false,
      piObservation: 'Pinned Pi has no keyboard selection-extension actions; selection is screen-level mouse text selection and does not move the editor cursor.',
      pisharpObservation: 'PiSharp keeps its existing Shift+arrow selection actions. N14 verifies only Pi-supported selection effects and records keyboard selection as a PiSharp extension.',
      rationale: 'No pinned-Pi editor action exists to compare against, so no equivalence is claimed for keyboard selection extension.'
    }
  ]
};
writeFileSync(resolve(output), `${JSON.stringify(fixture, null, 2)}\n`);
console.error(`Wrote ${Object.keys(results).length} scenarios to ${output}`);
