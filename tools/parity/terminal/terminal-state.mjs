import xterm from '@xterm/headless';

const attributes = ['bold', 'dim', 'italic', 'underline', 'blink', 'inverse', 'invisible', 'strikethrough', 'overline'];

export class TerminalState {
  constructor({ columns = 80, rows = 24, foreground = '#dddddd', background = '#111111', keyboardProtocol = 'legacy' } = {}) {
    this.terminal = new xterm.Terminal({ cols: columns, rows, allowProposedApi: true, scrollback: 10000 });
    this.replies = '';
    this.renderFrames = [];
    this.privateModes = {};
    this.keyboard = { kittyFlags: 0, kittyStack: [], modifyOtherKeys: 0 };
    this.colors = { foreground, background };
    this.terminal.parser.registerCsiHandler({ prefix: '>', final: 'm' }, params => {
      if (params[0] === 4) this.keyboard.modifyOtherKeys = params[1] ?? 0;
      return false;
    });
    this.terminal.parser.registerCsiHandler({ prefix: '>', final: 'u' }, params => {
      if (keyboardProtocol !== 'kitty') return false;
      this.keyboard.kittyStack.push(this.keyboard.kittyFlags);
      this.keyboard.kittyFlags = params[0] ?? 0;
      return true;
    });
    this.terminal.parser.registerCsiHandler({ prefix: '<', final: 'u' }, params => {
      if (keyboardProtocol !== 'kitty') return false;
      for (let count = params[0] || 1; count > 0; count--) this.keyboard.kittyFlags = this.keyboard.kittyStack.pop() ?? 0;
      return true;
    });
    this.terminal.parser.registerCsiHandler({ prefix: '=', final: 'u' }, params => {
      if (keyboardProtocol !== 'kitty') return false;
      const [flags = 0, mode = 1] = params;
      this.keyboard.kittyFlags = mode === 2 ? this.keyboard.kittyFlags | flags
        : mode === 3 ? this.keyboard.kittyFlags & ~flags : flags;
      return true;
    });
    this.terminal.parser.registerCsiHandler({ prefix: '?', final: 'u' }, () => {
      if (keyboardProtocol === 'kitty') this.replies += `\x1b[?${this.keyboard.kittyFlags}u`;
      return true;
    });
    this.terminal.onData(data => { this.replies += data; });
    for (const final of ['h', 'l']) {
      this.terminal.parser.registerCsiHandler({ prefix: '?', final }, params => {
        for (const parameter of params) {
          this.privateModes[String(parameter)] = final === 'h';
          if (final === 'l' && parameter === 2026) {
            // xterm invokes custom handlers before its built-in mode update.
            // Record the completed synchronized frame with the closing mode
            // already reflected in the snapshot.
            this.renderFrames.push(this.renderSnapshot());
          }
        }
        return false;
      });
    }
    this.colors.palette = Object.fromEntries([
      '#000000', '#800000', '#008000', '#808000', '#000080', '#800080', '#008080', '#c0c0c0',
      '#808080', '#ff0000', '#00ff00', '#ffff00', '#0000ff', '#ff00ff', '#00ffff', '#ffffff'
    ].map((color, index) => [index, color]));
    this.terminal.parser.registerOscHandler(4, data => {
      const entries = data.split(';');
      for (let index = 0; index + 1 < entries.length; index += 2) {
        const slot = entries[index];
        if (entries[index + 1] === '?') this.replies += String.fromCharCode(27) + ']4;' + slot + ';' +
          this.rgb(this.colors.palette[slot]) + String.fromCharCode(27, 92);
        else this.colors.palette[slot] = entries[index + 1];
      }
      return true;
    });
    for (const code of [10, 11]) {
      this.terminal.parser.registerOscHandler(code, data => {
        if (data !== '?') {
          this.colors[code === 10 ? 'foreground' : 'background'] = data;
          return true;
        }
        this.replies += String.fromCharCode(27) + ']' + code + ';' +
          this.rgb(this.colors[code === 10 ? 'foreground' : 'background']) + String.fromCharCode(27, 92);
        return true;
      });
    }
  }

  async write(data) {
    await new Promise(resolve => this.terminal.write(data, resolve));
  }

  takeRenderFrames() {
    const frames = this.renderFrames;
    this.renderFrames = [];
    return frames;
  }

  rgb(color) {
    if (color?.startsWith('rgb:')) return color;
    return `rgb:${(color ?? '#000000').slice(1).match(/../g).map(component => component.repeat(2)).join('/')}`;
  }

  resize(columns, rows) {
    this.terminal.resize(columns, rows);
  }

  takeReplies() {
    const replies = this.replies;
    this.replies = '';
    return replies;
  }

  snapshot() {
    const terminal = this.terminal;
    const active = terminal.buffer.active;
    const buffers = Object.fromEntries(['normal', 'alternate'].map(name => [name, this.buffer(terminal.buffer[name])]));
    return {
      columns: terminal.cols, rows: terminal.rows,
      activeScreen: active.type,
      cursor: { x: active.cursorX, y: active.cursorY, visible: !terminal._core.coreService.isCursorHidden,
        style: terminal.options.cursorStyle, blink: terminal.options.cursorBlink },
      modes: { ...terminal.modes, private: { ...this.privateModes }, keyboard: structuredClone(this.keyboard) },
      defaultColors: structuredClone(this.colors),
      viewport: Array.from({ length: terminal.rows }, (_, row) => this.line(active.getLine(active.viewportY + row))),
      buffers
    };
  }

  renderSnapshot() {
    const frame = this.snapshot();
    frame.modes.private['2026'] = false;
    if (Object.hasOwn(frame.modes, 'synchronizedOutput')) frame.modes.synchronizedOutput = false;
    return frame;
  }

  buffer(buffer) {
    return { cursorX: buffer.cursorX, cursorY: buffer.cursorY, baseY: buffer.baseY, viewportY: buffer.viewportY,
      lines: Array.from({ length: buffer.length }, (_, row) => this.line(buffer.getLine(row))) };
  }

  line(line) {
    return { wrapped: line?.isWrapped ?? false, cells: Array.from({ length: this.terminal.cols }, (_, column) => {
      const cell = line?.getCell(column);
      if (!cell) return null;
      return { chars: cell.getChars(), width: cell.getWidth(),
        foreground: this.color(cell, 'Fg'), background: this.color(cell, 'Bg'),
        attributes: Object.fromEntries(attributes.map(name => [name, Boolean(cell[`is${name[0].toUpperCase()}${name.slice(1)}`]())])),
        underline: { style: cell.extended.underlineStyle, color: cell.extended.underlineColor },
        hyperlink: cell.extended.urlId ? this.terminal._core._inputHandler._oscLinkService.getLinkData(cell.extended.urlId)?.uri ?? null : null };
    }) };
  }

  color(cell, plane) {
    return { mode: cell[`is${plane}RGB`]() ? 'rgb' : cell[`is${plane}Palette`]() ? 'palette' : 'default',
      value: cell[`get${plane}Color`]() };
  }

  dispose() {
    this.terminal.dispose();
  }
}
