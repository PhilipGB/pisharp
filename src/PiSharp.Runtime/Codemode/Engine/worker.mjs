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

const prelude = `(function(bridge, toolsJson, storeJson) {
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
  Object.defineProperty(globalThis, 'image', { value: value => {
    const url = typeof value === 'string' ? value : value?.image_url;
    if (typeof url !== 'string') throw new TypeError('image expects a base64 data URL');
    const match = new RegExp('^data:(image/(?:png|jpeg|gif|webp));base64,([A-Za-z0-9+/=]+)$').exec(url);
    if (!match || match[2].length > 4194304) throw new TypeError('image expects a supported base64 data URL up to 4 MiB');
    bridge('image', match[2], match[1]);
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
    vm.newString(JSON.stringify(request.tools)), vm.newString(JSON.stringify(request.store)));
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
