import assert from 'node:assert/strict';
import test from 'node:test';
import { TerminalState } from './terminal-state.mjs';

async function state(output, columns = 12, rows = 4) {
  const terminal = new TerminalState({ columns, rows });
  await terminal.write(Buffer.from(output));
  return terminal;
}

test('keeps characters, padding and wide continuation cells', async () => {
  const terminal = await state('A  界e\u0301');
  const frame = terminal.snapshot();
  assert.equal(frame.viewport[0].cells.length, 12);
  assert.deepEqual(frame.viewport[0].cells.slice(0, 6).map(cell => [cell.chars, cell.width]),
    [['A', 1], [' ', 1], [' ', 1], ['界', 2], ['', 0], ['é', 1]]);
  terminal.dispose();
});

test('records foreground, background and every text attribute', async () => {
  const terminal = await state('\x1b[1;2;3;4;5;7;8;9;53;38;2;12;34;56;48;5;42mX');
  const cell = terminal.snapshot().viewport[0].cells[0];
  assert.deepEqual(cell.foreground, { mode: 'rgb', value: 0x0c2238 });
  assert.deepEqual(cell.background, { mode: 'palette', value: 42 });
  assert.deepEqual(cell.attributes, { bold: true, dim: true, italic: true, underline: true,
    blink: true, inverse: true, invisible: true, strikethrough: true, overline: true });
  terminal.dispose();
});

test('preserves cursor visibility, position, style and blink', async () => {
  const terminal = await state('\x1b[3;7H\x1b[?25l\x1b[5 q');
  assert.deepEqual(terminal.snapshot().cursor, { x: 6, y: 2, visible: false, style: 'bar', blink: true });
  terminal.dispose();
});

test('keeps normal and alternate screen contents through mode transitions', async () => {
  const terminal = await state('normal\x1b[?1049h\x1b[Halternate');
  let frame = terminal.snapshot();
  assert.equal(frame.activeScreen, 'alternate');
  assert.equal(frame.buffers.normal.lines[0].cells[0].chars, 'n');
  assert.equal(frame.viewport[0].cells[0].chars, 'a');
  await terminal.write(Buffer.from('\x1b[?1049l'));
  frame = terminal.snapshot();
  assert.equal(frame.activeScreen, 'normal');
  assert.equal(frame.viewport[0].cells[0].chars, 'n');
  terminal.dispose();
});

test('records input, focus, mouse, wrap and synchronized output modes', async () => {
  const terminal = await state('\x1b[?1;1004;1002;1006;2004;2026h\x1b[?7l\x1b[4h');
  const modes = terminal.snapshot().modes;
  assert.equal(modes.applicationCursorKeysMode, true);
  assert.equal(modes.bracketedPasteMode, true);
  assert.equal(modes.mouseTrackingMode, 'drag');
  assert.equal(modes.sendFocusMode, true);
  assert.equal(modes.wraparoundMode, false);
  assert.equal(modes.insertMode, true);
  assert.equal(modes.private['1006'], true);
  assert.equal(modes.private['2026'], true);
  terminal.dispose();
});

test('answers cursor and terminal color queries with controlled values', async () => {
  const terminal = await state('abc\x1b[6n\x1b]10;?\x07\x1b]11;?\x1b\\');
  assert.ok(terminal.takeReplies().includes('\x1b[1;4R'));
  const colors = await state('\x1b]10;?\x07\x1b]11;?\x07');
  const replies = colors.takeReplies();
  assert.ok(replies.includes('\x1b]10;rgb:dddd/dddd/dddd\x1b\\'));
  assert.ok(replies.includes('\x1b]11;rgb:1111/1111/1111\x1b\\'));
  colors.dispose();
  terminal.dispose();
});

test('captures deterministic intermediate frames and resize', async () => {
  const terminal = await state('first');
  const first = terminal.snapshot();
  await terminal.write(Buffer.from('\rsecond'));
  const second = terminal.snapshot();
  assert.equal(first.viewport[0].cells[0].chars, 'f');
  assert.equal(second.viewport[0].cells[0].chars, 's');
  terminal.resize(7, 3);
  assert.equal(terminal.snapshot().viewport.length, 3);
  assert.equal(terminal.snapshot().viewport[0].cells.length, 7);
  terminal.dispose();
});

test('captures every completed synchronized render in stream order', async () => {
  const terminal = await state('\x1b[?2026h\x1b[Hloading\x1b[?2026l\x1b[?2026h\x1b[H50%\x1b[?2026l');
  const renders = terminal.takeRenderFrames();
  assert.equal(renders.length, 2);
  assert.equal(renders[0].viewport[0].cells.slice(0, 7).map(cell => cell.chars).join(''), 'loading');
  assert.equal(renders[1].viewport[0].cells.slice(0, 3).map(cell => cell.chars).join(''), '50%');
  assert.equal(renders[0].modes.private['2026'], false);
  assert.deepEqual(terminal.takeRenderFrames(), []);
  terminal.dispose();
});

test('retains wrapping, truncation, erasure and scrollback', async () => {
  const terminal = await state('abcdefghij', 6, 2);
  let frame = terminal.snapshot();
  assert.equal(frame.viewport[1].wrapped, true);
  assert.equal(frame.viewport[1].cells[0].chars, 'g');
  await terminal.write(Buffer.from('\r\nnext\r\nlast\x1b[2K'));
  frame = terminal.snapshot();
  assert.ok(frame.buffers.normal.baseY > 0);
  assert.equal(frame.viewport[1].cells[0].chars, '');
  terminal.dispose();
});

test('preserves extended underline styles and colors', async () => {
  const terminal = await state('\x1b[4:3;58:2::12:34:56mX');
  const cell = terminal.snapshot().viewport[0].cells[0];
  assert.equal(cell.underline.style, 3);
  assert.equal(cell.underline.color, 0x030c2238);
  terminal.dispose();
});

test('retains hyperlinks without depending on allocated IDs', async () => {
  const terminal = await state('\x1b]8;;https://example.test/item\x1b\\link\x1b]8;;\x1b\\');
  assert.equal(terminal.snapshot().viewport[0].cells[0].hyperlink, 'https://example.test/item');
  terminal.dispose();
});

test('a UTF-8 character split across PTY reads remains one character', async () => {
  const terminal = new TerminalState({ columns: 12, rows: 4 });
  const encoded = Buffer.from('界');
  await terminal.write(encoded.subarray(0, 1));
  await terminal.write(encoded.subarray(1));
  assert.equal(terminal.snapshot().viewport[0].cells[0].chars, '界');
  terminal.dispose();
});

for (const [name, output] of [
  ['space', ' X'], ['border', '─'], ['wrapping', 'X\r\n'], ['foreground', '\x1b[31mX'],
  ['background', '\x1b[44mX'], ['bold', '\x1b[1mX'], ['cursor', 'X\x1b[D'],
  ['visibility', 'X\x1b[?25l'], ['mode', 'X\x1b[?2004h'], ['screen', '\x1b[?1049hX']
]) {
  test(`does not normalize ${name}`, async () => {
    const original = await state('X');
    const changed = await state(output);
    assert.notDeepEqual(original.snapshot(), changed.snapshot());
    original.dispose();
    changed.dispose();
  });
}


test('records Kitty keyboard push, modification, query and restoration', async () => {
  const terminal = new TerminalState({ columns: 12, rows: 4, keyboardProtocol: 'kitty' });
  await terminal.write(Buffer.from('\x1b[>1u\x1b[=2;2u\x1b[?u\x1b[>4;2m'));
  assert.deepEqual(terminal.snapshot().modes.keyboard, { kittyFlags: 3, kittyStack: [0], modifyOtherKeys: 2 });
  assert.equal(terminal.takeReplies(), '\x1b[?3u');
  await terminal.write(Buffer.from('\x1b[<u'));
  assert.equal(terminal.snapshot().modes.keyboard.kittyFlags, 0);
  terminal.dispose();
});


test('answers palette queries and records changed default and palette colors', async () => {
  const terminal = await state('\x1b]4;1;?\x07\x1b]10;#123456\x07\x1b]4;1;rgb:1111/2222/3333\x07');
  assert.equal(terminal.takeReplies(), String.fromCharCode(27) + ']4;1;rgb:8080/0000/0000' + String.fromCharCode(27, 92));
  const colors = terminal.snapshot().defaultColors;
  assert.equal(colors.foreground, '#123456');
  assert.equal(colors.palette['1'], 'rgb:1111/2222/3333');
  terminal.dispose();
});
