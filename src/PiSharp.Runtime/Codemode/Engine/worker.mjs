import { createInterface } from 'node:readline';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { QuickJS, MAX_STACK_SIZE } from './quickjs/index.js';

// Only this trusted adapter runs in Node. Model code runs in a fresh QuickJS WASM VM.
const send = value => process.stdout.write(JSON.stringify(value) + '\n');
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
const iterator = lines[Symbol.asyncIterator]();
const first = await iterator.next();
if (first.done) process.exit(1);
const request = JSON.parse(first.value);
const wasm = await WebAssembly.compile(await readFile(fileURLToPath(new URL('./quickjs/quickjs.wasm', import.meta.url))));
const vm = await QuickJS.create({
  wasm,
  memoryLimit: request.memoryLimitBytes,
  maxStackSize: MAX_STACK_SIZE,
  wasi: memory => ({ fd_write(_fd, iovs, count, written) {
    const view = new DataView(memory.buffer);
    let total = 0;
    for (let i = 0; i < count; i++) total += view.getUint32(iovs + i * 8 + 4, true);
    view.setUint32(written, total, true);
    return 0;
  } })
});

const bridge = vm.newFunction('bridge', (kind, id, name, payload) => {
  const type = kind.toString();
  if (type === 'call' || type === 'global') send({ type, id: id.toNumber(), name: name.toString(), args: payload?.isUndefined ? null : payload?.toString() });
  else if (type === 'output') send({ type, value: id.toString() });
  else if (type === 'image') send({ type, data: id.toString(), mimeType: name.toString() });
  else if (type === 'done') send({ type, ok: id.toBoolean(), value: name?.isUndefined ? null : name?.toString(), store: payload?.toString() });
  return vm.undefined;
});

const prelude = `(function(bridge, toolsJson, storeJson, hasModels) {
  'use strict';
  const pending = new Map();
  const values = new Map(Object.entries(JSON.parse(storeJson)).map(([key, value]) => [key, JSON.stringify(value)]));
  let nextId = 0;
  let complete = false;
  let outputCount = 0;
  const EXIT = Object.freeze({});
  const tools = Object.create(null);
  function output(value) {
    if (++outputCount > 256) throw new RangeError('Codemode output item limit exceeded');
    let rendered = typeof value === 'string' ? value : JSON.stringify(value);
    if (rendered === undefined) rendered = String(value);
    bridge('output', rendered);
  }
  for (const item of JSON.parse(toolsJson)) {
    const invoke = args => new Promise((resolve, reject) => {
      const id = ++nextId;
      pending.set(id, { resolve, reject });
      bridge('call', id, item.name, JSON.stringify(args));
    });
    tools[item.name] = invoke;
    if (!(item.jsName in tools)) tools[item.jsName] = invoke;
  }
  Object.freeze(tools);
  Object.defineProperty(globalThis, 'ALL_TOOLS', { value: Object.freeze(JSON.parse(toolsJson).map(item => Object.freeze({ name: item.jsName, description: item.description }))) });
  Object.defineProperty(globalThis, 'text', { value: output });
  Object.defineProperty(globalThis, 'console', { value: Object.freeze({
    log: (...args) => output(args.map(value => typeof value === 'string' ? value : JSON.stringify(value)).join(' ')),
    info: (...args) => output(args.map(value => typeof value === 'string' ? value : JSON.stringify(value)).join(' ')),
    warn: (...args) => output(args.map(value => typeof value === 'string' ? value : JSON.stringify(value)).join(' ')),
    error: (...args) => output(args.map(value => typeof value === 'string' ? value : JSON.stringify(value)).join(' '))
  }) });
  Object.defineProperty(globalThis, 'exit', { value: () => { throw EXIT; } });
  const imageSignatures = [
    ['image/png', data => data.startsWith('iVBORw0KGg')],
    ['image/jpeg', data => data.startsWith('/9j/') && data[4] !== '9'],
    ['image/gif', data => data.startsWith('R0lGODlh') || data.startsWith('R0lGODdh')],
    ['image/webp', data => data.startsWith('UklG') && data.slice(12, 16) === 'RUJQ']
  ];
  Object.defineProperty(globalThis, 'image', { value: value => {
    const url = typeof value === 'string' ? value : value?.image_url;
    if (typeof url !== 'string') throw new TypeError('image expects a base64 data URL');
    const comma = url.indexOf(',');
    const header = comma < 0 ? '' : url.slice(0, comma).toLowerCase();
    if (!header.startsWith('data:') || !header.slice(5).split(';').includes('base64'))
      throw new TypeError('image expects a supported base64 data URL up to 4 MiB');
    const data = url.slice(comma + 1).replace(/\\s+/g, '');
    if (data.length > 4194304) throw new TypeError('image expects a supported base64 data URL up to 4 MiB');
    if (data.length === 0 || data.length % 4 !== 0) throw new TypeError('image data is not valid base64');
    let padding = false;
    let padCount = 0;
    let dataCharacters = 0;
    for (const character of data) {
      if (character === '=') {
        padding = true;
        if (++padCount > 2) throw new TypeError('image data is not valid base64');
      } else {
        const code = character.charCodeAt(0);
        if (padding || !(code >= 65 && code <= 90 || code >= 97 && code <= 122 ||
            code >= 48 && code <= 57 || character === '+' || character === '/'))
          throw new TypeError('image data is not valid base64');
        dataCharacters++;
      }
    }
    if (dataCharacters === 0) throw new TypeError('image data is not valid base64');
    const detected = imageSignatures.find(([, matches]) => matches(data));
    if (!detected) throw new TypeError('image data is not a PNG, JPEG, GIF, or WebP image');
    bridge('image', data, detected[0]);
  } });
  Object.defineProperty(globalThis, 'searchTools', { value: (query, options) => new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    bridge('global', id, 'searchTools', JSON.stringify([query, options]));
  }) });
  Object.defineProperty(globalThis, 'describeTool', { value: name => new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    bridge('global', id, 'describeTool', JSON.stringify(name));
  }) });
  Object.defineProperty(globalThis, 'describeNamespace', { value: name => new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    bridge('global', id, 'describeNamespace', JSON.stringify(name));
  }) });
  if (hasModels) {
    const models = Object.create(null);
    for (const method of ['getModelsOfType', 'getAvailableOfType', 'getModelOfType', 'classify']) {
      models[method] = (...args) => new Promise((resolve, reject) => {
        const id = ++nextId;
        pending.set(id, { resolve, reject });
        bridge('global', id, 'models.' + method, JSON.stringify(args));
      });
    }
    Object.defineProperty(globalThis, 'models', { value: Object.freeze(models) });
  }
  Object.defineProperty(globalThis, 'store', { value: (key, value) => {
    if (typeof key !== 'string' || key.length > 256) throw new TypeError('store key must be a string of at most 256 characters');
    const json = JSON.stringify(value);
    if (json !== undefined && json.length > 262144) throw new RangeError('store value too large');
    const previous = values.get(key);
    if (json === undefined) values.delete(key); else values.set(key, json);
    if (JSON.stringify(Object.fromEntries(values)).length > 1048576) {
      if (previous === undefined) values.delete(key); else values.set(key, previous);
      throw new RangeError('store is full');
    }
  } });
  Object.defineProperty(globalThis, 'load', { value: key => values.has(key) ? JSON.parse(values.get(key)) : undefined });
  function done(ok, value) {
    if (complete) return;
    complete = true;
    bridge('done', ok, value, JSON.stringify(Object.fromEntries([...values].map(([key, json]) => [key, JSON.parse(json)]))));
  }
  function run(fn) {
    Promise.resolve().then(() => fn(tools)).then(value => done(true, JSON.stringify(value)),
      error => error === EXIT ? done(true, undefined) : done(false,
        error instanceof Error ? (error.name + ': ' + error.message + (error.stack ? '\\n' + error.stack : '')) : String(error)));
  }
  function settle(id, ok, payload) {
    const call = pending.get(id);
    if (!call) return;
    pending.delete(id);
    if (ok) call.resolve(payload === undefined ? undefined : JSON.parse(payload));
    else call.reject(new Error(payload));
  }
  return { run, settle };
})`;

try {
  const factory = vm.evalCode(prelude, 'codemode-prelude.js');
  const api = vm.callFunction(factory, vm.undefined, bridge,
    vm.newString(JSON.stringify(request.tools)), vm.newString(JSON.stringify(request.store)), request.hasModels ? vm.true : vm.false);
  const run = api.getProp('run');
  const settle = api.getProp('settle');
  const fn = vm.evalCode(`(async tools => {${request.code}\n})`, 'codemode.js');
  vm.callFunction(run, api, fn).dispose();
  vm.executePendingJobs();
  for await (const line of iterator) {
    const reply = JSON.parse(line);
    vm.withScope(() => vm.callFunction(settle, api, vm.newNumber(reply.id), reply.ok ? vm.true : vm.false,
      reply.payload === null ? vm.undefined : vm.newString(reply.payload)));
    vm.executePendingJobs();
  }
} catch (error) {
  send({ type: 'done', ok: false, value: String(error?.stack || error), store: '{}' });
}
