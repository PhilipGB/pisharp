import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, input] = process.argv.slice(2);
verifyPiCheckout(pi);
const results = [];
for (const item of JSON.parse(readFileSync(input, 'utf8'))) {
  const api = item.vertex ? 'google-vertex' : 'google-generative-ai';
  const { streamSimple } = await import(pathToFileURL(path.join(pi, `packages/ai/src/api/${api}.ts`)).href);
  let payload;
  const model = { id: item.model, name: item.model, api, provider: 'fixture', baseUrl: 'https://example.invalid/v1beta',
    reasoning: true, input: ['text'], contextWindow: 128000, maxTokens: 4096,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } };
  const response = await streamSimple(model, { messages: [{ role: 'user', content: 'hello', timestamp: 0 }] }, {
    apiKey: 'fixture', reasoning: item.level === 'off' ? undefined : item.level,
    onPayload: value => { payload = value; throw new Error('payload captured'); },
  }).result();
  if (!payload || !response.errorMessage?.includes('payload captured')) throw new Error(JSON.stringify(response));
  results.push({ ...item, thinking: payload.config.thinkingConfig });
}
console.log(JSON.stringify(results));
