import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { stream } = await load('packages/ai/src/api/anthropic-messages.ts');
const { normalizeContext } = await load('packages/ai/src/utils/transcript.ts');
const { fauxAssistantMessage } = await load('packages/ai/src/index.ts');
const png = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1cAAAAASUVORK5CYII=';
const model = { id: 'claude-opus-5', name: 'Fixture', provider: 'fixture', api: 'anthropic-messages',
  baseUrl: 'http://fixture.invalid', reasoning: false, input: ['text', 'image'],
  contextWindow: 128000, maxTokens: 128, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } };
const results = [];
for (const isError of [false, true]) {
  const transcript = normalizeContext({ tools: [], messages: [
    { role: 'user', content: 'before', timestamp: 1 },
    fauxAssistantMessage('', { content: [{ type: 'toolCall', id: 'call_image', name: 'read', arguments: { path: 'image.png' } }] }),
    { role: 'toolResult', toolCallId: 'call_image', toolName: 'read', isError, timestamp: 2,
      content: [{ type: 'text', text: 'tool text' }, { type: 'image', mimeType: 'image/png', data: png }] },
  ] });
  const original = JSON.stringify(transcript);
  let payload;
  const result = await stream(model, transcript, { apiKey: 'fixture-key', fetch: async (_url, init) => {
    payload = JSON.parse(init.body);
    const events = [
      { type: 'message_start', message: { id: 'msg_fixture', model: model.id, usage: { input_tokens: 4, output_tokens: 0 } } },
      { type: 'message_delta', delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 2 } },
      { type: 'message_stop' },
    ];
    return new Response(events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join(''),
      { headers: { 'content-type': 'text/event-stream' } });
  } }).result();
  if (result.stopReason !== 'stop') throw new Error(result.errorMessage || result.stopReason);
  const tool = payload.messages.flatMap(message => message.content).find(block => block.type === 'tool_result');
  results.push({ isError, resultIsError: tool.is_error, blocks: tool.content, historyUnchanged: original === JSON.stringify(transcript) });
}
console.log(JSON.stringify(results));
