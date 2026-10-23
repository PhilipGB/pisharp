import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, inputFile, outputFile, mode] = process.argv.slice(2);
verifyPiCheckout(pi);
// The upstream resolver prevents package imports from selecting stale dist files.
await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/experimental/source-resolver.ts')).href);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { validateToolArguments } = await load('packages/ai/src/utils/validation.ts');
const factories = {};
for (const name of ['read', 'write', 'edit', 'bash', 'grep', 'find', 'ls']) {
  const module = await load(`packages/coding-agent/src/core/tools/${name}.ts`);
  factories[name] = module[`create${name[0].toUpperCase()}${name.slice(1)}ToolDefinition`];
}

const declarations = Object.fromEntries(Object.entries(factories).map(([name, factory]) => {
  const tool = factory('/tmp');
  return [name, { name, description: tool.description, parameters: tool.parameters,
    outputSchema: tool.outputSchema ?? null, constrainedSampling: tool.constrainedSampling ?? null }];
}));

if (mode === '--bash-session-environment-only') {
  const { captureBashSessionEnvironment } = await import('./builtin-coding-tool-bash-process-cases.mjs');
  const result = await captureBashSessionEnvironment(factories);
  writeFileSync(outputFile, JSON.stringify(result, null, 2) + '\n');
  process.exit(0);
}

if (mode === '--bash-process-lifetime-only') {
  const { captureBashProcessLifetime } = await import('./builtin-coding-tool-bash-process-cases.mjs');
  const result = await captureBashProcessLifetime(factories);
  writeFileSync(outputFile, JSON.stringify(result, null, 2) + '\n');
  process.exit(0);
}

if (mode === '--mutation-coordination-only') {
  const { captureMutationCoordination } = await import('./builtin-coding-tool-mutation-cases.mjs');
  const result = await captureMutationCoordination(factories, load);
  writeFileSync(outputFile, JSON.stringify(result, null, 2) + '\n');
  process.exit(0);
}

if (mode === '--read-cancellation-only') {
  const fs = await import('node:fs/promises');
  const { tmpdir } = await import('node:os');
  const { detectSupportedImageMimeTypeFromFile } = await load('packages/coding-agent/src/utils/mime.ts');
  const fixture = JSON.parse(readFileSync(inputFile, 'utf8'));
  const results = [];
  for (const kind of ['text', 'image']) {
    const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-read-abort-'));
    const input = kind === 'image' ? Buffer.from(fixture.assets['png-rgb'].base64, 'base64') : Buffer.from('unchanged\n');
    const target = path.join(root, 'fixture');
    const entered = Promise.withResolvers();
    const release = Promise.withResolvers();
    const finished = Promise.withResolvers();
    let released = false;
    let readEntered = false;
    let deadline;
    try {
      await fs.writeFile(target, input);
      const controller = new AbortController();
      const updates = [];
      const tool = factories.read(root, { operations: {
        access: file => fs.access(file), detectImageMimeType: detectSupportedImageMimeTypeFromFile,
        readFile: async file => {
          const bytes = await fs.readFile(file);
          readEntered = true;
          entered.resolve();
          await release.promise;
          finished.resolve();
          return bytes;
        }
      } });
      const running = tool.execute('read-abort-' + kind, { path: 'fixture' }, controller.signal,
        update => updates.push(update)).then(value => ({ success: true, value }), error => ({ success: false, error: error.message }));
      const timeout = new Promise((_, reject) => { deadline = setTimeout(() => reject(new Error('Read cancellation barrier timed out')), 5000); });
      await Promise.race([entered.promise, timeout]);
      controller.abort();
      const outcome = await Promise.race([running, timeout]);
      results.push({ caseId: 'read-cancel-pending-' + kind, phase: 'Physical read completed; operations.readFile promise held before returning',
        ...outcome, settledBeforeRelease: !released, updates, inputUnchanged: input.equals(await fs.readFile(target)) });
    } finally {
      clearTimeout(deadline);
      released = true;
      release.resolve();
      if (readEntered) await finished.promise;
      await fs.rm(root, { recursive: true, force: true });
    }
  }
  writeFileSync(outputFile, JSON.stringify({ pinnedPiSha: 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
    source: 'packages/coding-agent/src/core/tools/read.ts', results }, null, 2) + '\n');
  process.exit(0);
}

if (mode === '--decode-images-only' || mode === '--sample-images-only') {
  const { loadPhoton } = await load('packages/coding-agent/src/utils/photon.ts');
  const photon = await loadPhoton();
  if (!photon) throw new Error('Pinned Photon is unavailable for image calibration');
  const fixture = JSON.parse(readFileSync(inputFile, 'utf8'));
  const results = [];
  const entries = mode === '--sample-images-only' ? fixture.samples.map(row => [row.caseId,
    { ...fixture.assets[row.asset], sample: row }]) : Object.entries(fixture.assets);
  for (const [caseId, asset] of entries) {
    let decoded;
    let resized;
    try {
      decoded = photon.PhotonImage.new_from_byteslice(Buffer.from(asset.base64, 'base64'));
      if (asset.sample) resized = photon.resize(decoded, asset.sample.width, asset.sample.height, photon.SamplingFilter.Lanczos3);
      const target = resized ?? decoded;
      const pixels = Buffer.from(target.get_raw_pixels());
      results.push({ caseId, width: target.get_width(), height: target.get_height(),
        rgbaBase64: pixels.toString('base64'), rgbaSha256: createHash('sha256').update(pixels).digest('hex') });
    } catch {
      results.push({ caseId, unsupported: true });
    } finally { resized?.free(); decoded?.free(); }
  }
  writeFileSync(outputFile, JSON.stringify({ results }, null, 2) + '\n');
  process.exit(0);
}

if (mode === '--truncate-only') {
  const { truncateHead, truncateTail } = await load('packages/coding-agent/src/core/tools/truncate.ts');
  const fixture = JSON.parse(readFileSync(inputFile, 'utf8'));
  const results = fixture.cases.map(row => ({ caseId: row.caseId,
    result: (row.mode === 'head' ? truncateHead : truncateTail)(row.content, row.options) }));
  writeFileSync(outputFile, JSON.stringify({ results }, null, 2) + '\n');
  process.exit(0);
}

if (mode === '--validate-only') {
  const fixture = JSON.parse(readFileSync(inputFile, 'utf8'));
  const results = [];
  for (const row of fixture.results ?? fixture.cases) {
    const input = row.input ?? row.arguments;
    const definition = factories[row.tool]('/tmp');
    const value = { caseId: row.caseId, tool: row.tool, input };
    try {
      const argumentsValue = structuredClone(input);
      const prepared = definition.prepareArguments?.(argumentsValue) ?? argumentsValue;
      value.validated = validateToolArguments(definition, { name: row.tool, arguments: prepared });
    } catch (error) {
      value.error = error.message;
    }
    results.push(value);
  }
  writeFileSync(outputFile, JSON.stringify({ results }, null, 2) + '\n');
  process.exit(0);
}

const results = [];
for (const request of JSON.parse(readFileSync(inputFile, 'utf8'))) {
  const updates = [];
  const controller = new AbortController();
  if (request.preCancelled) controller.abort();
  const tool = factories[request.tool](request.executionCwd ?? request.workspace, request.options ?? undefined);
  const result = { caseId: request.caseId, content: [], details: null, structuredContent: null,
    isError: false, rejected: false, updates };
  try {
    const prepared = tool.prepareArguments?.(structuredClone(request.arguments)) ?? request.arguments;
    const argumentsValue = validateToolArguments(tool, { id: request.caseId, name: request.tool,
      type: 'toolCall', arguments: prepared });
    const value = await tool.execute(request.caseId, argumentsValue, controller.signal, update => {
      updates.push({ content: update.content, details: update.details ?? null });
      if (request.acknowledgeEmptyUpdate && update.content?.some(item => item.text === ''))
        writeFileSync(path.join(request.workspace, 'ack'), 'go\n');
      if (request.cancelOnOutput && update.content?.some(item => item.text?.includes(request.cancelOnOutput)))
        controller.abort();
    }, request.context ? { ...request.context, cwd: request.executionCwd ?? request.workspace } : undefined);
    result.content = value.content;
    result.details = value.details ?? null;
    result.structuredContent = value.structuredContent ?? null;
    result.isError = value.isError === true;
  } catch (error) {
    result.isError = true;
    result.rejected = true;
    result.content = [{ type: 'text', text: error.message }];
  }
  results.push(result);
  // Persist each completed case so interruption cannot discard prior evidence.
  writeFileSync(outputFile, JSON.stringify({ declarations, results }, null, 2) + '\n');
}
