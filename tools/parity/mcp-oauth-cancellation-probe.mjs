import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { createServer } from 'node:http';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { McpClient, StreamableHttpTransport } = await load('packages/mcp/src/index.ts');
const { McpOAuthCredentialStore, createMcpAuthProvider } = await load('packages/coding-agent/src/extensions/mcp/oauth.ts');
const { InMemoryAuthStorageBackend } = await load('packages/coding-agent/src/core/auth-storage.ts');
let refreshes = 0;
let origin;
const executed = [];
let releaseRefresh;
let refreshStarted;
const refreshGate = new Promise(resolve => { releaseRefresh = resolve; });
const started = new Promise(resolve => { refreshStarted = resolve; });
const json = (response, payload) => response.writeHead(200, { 'content-type': 'application/json' }).end(JSON.stringify(payload));
const server = createServer(async (request, response) => {
  try {
    if (request.url.includes('oauth-protected-resource')) {
      json(response, { resource: `${origin}/mcp`, authorization_servers: [origin] });
      return;
    }
    if (request.url.includes('oauth-authorization-server')) {
      json(response, { issuer: origin, authorization_endpoint: `${origin}/authorize`, token_endpoint: `${origin}/token`,
        response_types_supported: ['code'], grant_types_supported: ['authorization_code', 'refresh_token'],
        code_challenge_methods_supported: ['S256'], token_endpoint_auth_methods_supported: ['none'] });
      return;
    }
    if (request.url === '/token') {
      refreshes++;
      refreshStarted();
      await refreshGate;
      json(response, { access_token: 'new', refresh_token: 'refresh-new', token_type: 'Bearer', expires_in: 3600 });
      return;
    }
    if (request.method !== 'POST') { response.writeHead(405).end(); return; }
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    const message = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (message.method === 'tools/call' && request.headers.authorization !== 'Bearer new') {
      response.writeHead(401, { 'www-authenticate': `Bearer resource_metadata="${origin}/.well-known/oauth-protected-resource/mcp"` }).end();
      return;
    }
    if (!('id' in message)) { response.writeHead(202).end(); return; }
    if (message.method === 'initialize') {
      json(response, { jsonrpc: '2.0', id: message.id, result: { protocolVersion: message.params.protocolVersion,
        capabilities: { tools: {} }, serverInfo: { name: 'oauth-recovery', version: '1' } } });
    } else if (message.method === 'tools/call') {
      executed.push(message.params.arguments.value);
      json(response, { jsonrpc: '2.0', id: message.id, result: { content: [{ type: 'text', text: message.params.arguments.value }] } });
    } else {
      json(response, { jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Method not found' } });
    }
  } catch (error) { response.destroy(error); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
origin = `http://127.0.0.1:${server.address().port}`;
const store = new McpOAuthCredentialStore(new InMemoryAuthStorageBackend()).forServer('protected', `${origin}/mcp`);
store.save({ serverUrl: `${origin}/mcp`, clientInformation: { client_id: 'fixture-client' },
  tokens: { access_token: 'old', refresh_token: 'refresh-old', token_type: 'Bearer', expires_in: 3600 },
  tokensExpireAt: Date.now() + 3600000 });
const authProvider = createMcpAuthProvider({ serverUrl: `${origin}/mcp`, store,
  settings: () => ({ clientId: 'fixture-client' }), onChallenge: () => {} });
const client = new McpClient({ name: 'fixture', version: '1' });
const bound = setTimeout(() => { releaseRefresh(); server.closeAllConnections(); }, 15000);
try {
  await client.connect(new StreamableHttpTransport({ url: `${origin}/mcp`, openGetStream: false, authProvider }));
  const controller = new AbortController();
  const aborted = client.callTool('echo', { value: 'aborted' }, { signal: controller.signal }).catch(error => error);
  await started;
  const expired = client.callTool('echo', { value: 'timed-out' }, { timeoutMs: 100 }).catch(error => error);
  const kept = client.callTool('echo', { value: 'kept' });
  controller.abort();
  const settledBeforeRelease = [(await aborted).name === 'AbortError', (await expired).name === 'McpTimeoutError'];
  if (executed.length !== 0) throw new Error('Call executed before refresh release.');
  releaseRefresh();
  const result = await kept;
  const cached = await store.load();
  console.log(JSON.stringify({ settledBeforeRelease, executed, refreshes, keptText: result.content[0].text,
    storedAccess: cached.tokens.access_token, storedRefresh: cached.tokens.refresh_token,
    bindingPreserved: cached.clientInformation.client_id === 'fixture-client' }));
} finally {
  releaseRefresh();
  await client.close();
  await authProvider.settled();
  clearTimeout(bound);
  server.closeAllConnections();
  await new Promise(resolve => server.close(resolve));
}
