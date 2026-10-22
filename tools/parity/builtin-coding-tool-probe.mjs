import { readFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, inputFile, outputFile] = process.argv.slice(2);
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
