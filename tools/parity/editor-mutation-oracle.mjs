#!/usr/bin/env node
/**
 * Generate the pinned-Pi editor mutation oracle used by EditorMutationParityTests.
 *
 * Usage:
 *   node tools/parity/editor-mutation-oracle.mjs --pi /path/to/pinned/pi \
 *     --output docs/parity/fixtures/keys-editor-mutation-pi-oracle-<date>.json
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
const scenarioPath = option('--scenarios') ?? join(here, 'editor-mutation-scenarios.json');
if (!piCheckout || !output) {
  console.error('Usage: node tools/parity/editor-mutation-oracle.mjs --pi <checkout> --output <file>');
  process.exit(2);
}

verifyPiCheckout(piCheckout);
const importFromPi = (path) => import(pathToFileURL(join(piCheckout, path)).href);
const { Editor } = await importFromPi('packages/tui/src/components/editor.ts');
const { TuiMainScreen } = await importFromPi('packages/tui/src/tui-main-screen.ts');
const { VirtualTerminal } = await importFromPi('packages/tui/test/virtual-terminal.ts');
const { defaultEditorTheme } = await importFromPi('packages/tui/test/test-themes.ts');

const keys = {
  bs: '\x7f', del: '\x1b[3~', 'alt-bs': '\x1b\x7f', 'alt-del': '\x1b[3;3~',
  'ctrl-d': '\x04', 'ctrl-w': '\x17', 'alt-d': '\x1bd', 'ctrl-u': '\x15', 'ctrl-k': '\x0b',
  'ctrl-y': '\x19', 'alt-y': '\x1by', 'ctrl-minus': '\x1f', 'ctrl-a': '\x01', 'ctrl-e': '\x05',
  left: '\x1b[D', right: '\x1b[C', up: '\x1b[A', down: '\x1b[B', newline: '\x1b\r'
};

const position = (editor, offset) => {
  const before = editor.getText().slice(0, offset);
  const targetLine = (before.match(/\n/g) || []).length;
  const targetCol = offset - (before.lastIndexOf('\n') + 1);
  editor.handleInput('\x01');
  while (editor.getCursor().line > targetLine) editor.handleInput('\x1b[A');
  while (editor.getCursor().line < targetLine) editor.handleInput('\x1b[B');
  editor.handleInput('\x01');
  for (let index = 0; index < targetCol; index++) editor.handleInput('\x1b[C');
};

const scenarios = JSON.parse(readFileSync(scenarioPath, 'utf8'));
const results = {};
for (const [name, scenario] of Object.entries(scenarios)) {
  const editor = new Editor(new TuiMainScreen(new VirtualTerminal(80, 24)), defaultEditorTheme);
  editor.setText(scenario.text);
  if (scenario.offset !== undefined) position(editor, scenario.offset);
  const expected = [];
  for (const step of scenario.steps) {
    if (step.startsWith('type:')) {
      for (const character of step.slice(5)) editor.handleInput(character);
    } else if (step.startsWith('pos:')) {
      position(editor, Number(step.slice(4)));
    } else if (step.startsWith('set:')) {
      editor.setText(step.slice(4));
    } else {
      const sequence = keys[step];
      if (sequence === undefined) throw new Error(`Unknown key token: ${step}`);
      editor.handleInput(sequence);
    }
    expected.push({ text: editor.getText(), cursor: editor.getCursor() });
  }
  results[name] = { ...scenario, expected };
}

const fixture = {
  capability: 'keys-editor-mutation',
  piSha: execFileSync('git', ['-C', piCheckout, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
  source: [
    'packages/tui/src/components/editor.ts',
    'packages/tui/src/kill-ring.ts',
    'packages/tui/src/word-navigation.ts',
    'packages/coding-agent/src/core/keybindings.ts',
    'packages/coding-agent/docs/keybindings.md'
  ],
  scenarios: results,
  findings: [
    {
      id: 'cjk-word-segmentation',
      classification: 'out_of_matrix_unicode_dictionary_difference',
      required: false,
      piObservation: 'Pinned Pi delegates word boundaries to Intl.Segmenter (ICU). For 你好世界, Ctrl+W deletes 世界 and Alt+D deletes 你好.',
      pisharpObservation: 'EditorBuffer treats a contiguous Han run as one word, so Ctrl+W deletes all four characters and Alt+D deletes the whole run.',
      rationale: 'The required M12 contract covers grapheme clusters, combining characters and multiline boundaries. Reproducing ICU dictionary-based CJK word breaking would require an ICU word-segmentation dependency or an embedded dictionary; the difference is retained as a documented finding rather than silently normalized.'
    }
  ]
};
writeFileSync(resolve(output), `${JSON.stringify(fixture, null, 2)}\n`);
console.error(`Wrote ${Object.keys(results).length} scenarios to ${output}`);
