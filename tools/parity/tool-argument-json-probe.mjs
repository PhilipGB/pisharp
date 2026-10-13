import { verifyPiCheckout } from './pi-reference.mjs';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const [pi, inputs, nativeFile] = process.argv.slice(2);
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { repairJson, parseStreamingJson } = await load('packages/ai/src/utils/json-parse.ts');
const parsed = JSON.parse(readFileSync(inputs, 'utf8')).map(input => ({ input, repaired: repairJson(input), parsed: parseStreamingJson(input) }));
const native = [];
for (const fixture of JSON.parse(readFileSync(nativeFile, 'utf8'))) {
  const { stream } = await load(`packages/ai/src/api/${fixture.protocol}.ts`);
  const model = { id: 'fixture-model', name: 'Fixture', api: fixture.protocol, provider: 'fixture', baseUrl: 'http://fixture.invalid',
    reasoning: false, input: ['text'], contextWindow: 128000, maxTokens: 128,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } };
  const result = await stream(model, { messages: [{ role: 'user', content: 'fixture', timestamp: 1 }] },
    { apiKey: 'fixture-key', fetch: async () => new Response(fixture.wire, { headers: { 'content-type': 'text/event-stream' } }) }).result();
  if (result.stopReason === 'error') throw new Error(result.errorMessage);
  const calls = result.content.filter(content => content.type === 'toolCall');
  if (calls.length !== 1) throw new Error(JSON.stringify(result));
  native.push({ protocol: fixture.protocol, input: fixture.input, arguments: calls[0].arguments });
}
console.log(JSON.stringify({ parsed, native }));
