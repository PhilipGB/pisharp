import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { stream } = await import(pathToFileURL(path.join(pi, 'packages/ai/src/api/anthropic-messages.ts')).href);
const results = [];
for (const scenario of JSON.parse(readFileSync(process.argv[3], 'utf8'))) {
  const payloads = [];
  let hooks = 0;
  const messages = structuredClone(scenario.messages);
  const original = JSON.stringify(messages);
  const errorBody = JSON.stringify({ type: 'error', error: { type: scenario.errorType, message: scenario.errorMessage } });
  const options = { apiKey: 'fixture-key', maxRetries: 0, cacheRetention: 'short',
    onPayload: payload => {
      hooks++;
      if (scenario.hookError) {
        const { default: Anthropic } = createSDK();
        throw Anthropic.APIError.generate(400, { error: { type: 'invalid_request_error', message: scenario.errorMessage } }, scenario.errorMessage, new Headers());
      }
      return { ...payload, max_tokens: 123 };
    }, fetch: async (_, init) => {
      payloads.push(JSON.parse(init.body));
      if (scenario.streamError) return new Response('event: error\ndata: ' + errorBody + '\n\n', { headers: { 'content-type': 'text/event-stream' } });
      if (payloads.length === 1 && scenario.status !== 200 || scenario.secondError)
        return new Response(errorBody, { status: scenario.status, headers: { 'content-type': 'application/json' } });
      const events = [
        { type: 'message_start', message: { id: 'msg_fixture', model: 'fixture', usage: { input_tokens: 4, output_tokens: 0 } } },
        { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } },
        { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'ok' } },
        { type: 'content_block_stop', index: 0 },
        { type: 'message_delta', delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 2 } },
        { type: 'message_stop' }
      ];
      return new Response(events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join(''), { headers: { 'content-type': 'text/event-stream' } });
    } };
  const model = { id: 'fixture', name: 'Fixture', api: 'anthropic-messages', provider: 'fixture', baseUrl: 'https://fixture.invalid',
    reasoning: false, input: ['text'], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 128000, maxTokens: 100,
    compat: { supportsStrictTools: scenario.supported, supportsEagerToolInputStreaming: true,
      supportsMidConvoSystemMessages: scenario.native, supportsMidConvoToolChanges: scenario.native } };
  const result = await stream(model, { messages }, options).result();
  if (scenario.repeat) await stream(model, { messages }, options).result();
  results.push({ id: scenario.id, succeeded: result.stopReason === 'stop', hooks,
    diagnostic: result.diagnostics?.some(item => item.type === 'anthropic_strict_tools_fallback') ?? false,
    traces: payloads.map(payload => ({ tools: payload.tools,
      additions: payload.messages.flatMap(message => Array.isArray(message.content)
        ? message.content.filter(block => block.type === 'tool_addition').map(block => block.tool.definition) : []), maxTokens: payload.max_tokens })),
    historyUnchanged: JSON.stringify(messages) === original });
}
console.log(JSON.stringify(results));

// Use the SDK installed in the pinned checkout for synthetic hook errors too.
import { createRequire } from 'node:module';
function createSDK() {
  return createRequire(path.join(pi, 'packages/ai/package.json'))('@anthropic-ai/sdk');
}
