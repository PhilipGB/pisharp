import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
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
  const tool = factories[request.tool](request.workspace, request.options ?? undefined);
  const result = { caseId: request.caseId, content: [], details: null, structuredContent: null,
    isError: false, rejected: false, updates };
  try {
    const prepared = tool.prepareArguments?.(structuredClone(request.arguments)) ?? request.arguments;
    const argumentsValue = validateToolArguments(tool, { id: request.caseId, name: request.tool,
      type: 'toolCall', arguments: prepared });
    const value = await tool.execute(request.caseId, argumentsValue, controller.signal, update => {
      updates.push({ content: update.content, details: update.details ?? null });
      if (request.cancelOnOutput && update.content?.some(item => item.text?.includes(request.cancelOnOutput)))
        controller.abort();
    }, request.context ? { ...request.context, cwd: request.workspace } : undefined);
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
