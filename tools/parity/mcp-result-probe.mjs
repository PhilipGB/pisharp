import { verifyPiCheckout } from './pi-reference.mjs';
import { readFileSync, mkdirSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const [pi, root, input, fixture] = process.argv.slice(2);
verifyPiCheckout(pi);
const load = (file) => import(pathToFileURL(path.join(pi, file)).href);
const { McpServerConnection } = await load('packages/coding-agent/src/extensions/mcp/runtime.ts');
const { McpOAuthCredentialStore } = await load('packages/coding-agent/src/extensions/mcp/oauth.ts');
const { InMemoryAuthStorageBackend } = await load('packages/coding-agent/src/core/auth-storage.ts');
const { StdioTransport } = await load('packages/mcp/src/transports/stdio.ts');
const { convertMcpResult } = await load('packages/coding-agent/src/extensions/mcp/tools.ts');

function counts(log) {
  const methods = readFileSync(log, 'utf8').trim().split('\n');
  const count = (name) => methods.filter(method => method === name).length;
  return { tools: count('tools/list'), resources: count('resources/list'),
    templates: count('resources/templates/list'), calls: count('tools/call') };
}
const results = [];
for (const scenario of JSON.parse(readFileSync(input, 'utf8'))) {
  const cwd = path.join(root, `pi-${results.length}`);
  mkdirSync(cwd);
  const log = path.join(cwd, 'requests.log');
  const config = { command: 'python3', args: [fixture], env: {
    MCP_FIXTURE_REQUEST_LOG: log, MCP_FIXTURE_STRUCTURED_JSON: scenario.json,
    MCP_FIXTURE_OMIT_CONTENT: scenario.omitContent ? '1' : '0' } };
  const connection = new McpServerConnection({
    entry: { name: 'fixture', config, source: 'test' }, cwd,
    createTransport: () => new StdioTransport({ ...config, cwd }),
    credentials: new McpOAuthCredentialStore(new InMemoryAuthStorageBackend()), onTools: () => {},
  });
  try {
    const client = await connection.getClient();
    const atConnection = counts(log);
    const raw = await client.callTool('echo', { value: 'unused' });
    const result = await convertMcpResult('fixture', 'echo', raw);
    await client.listResources();
    await client.listResourceTemplates();
    results.push({ text: result.content.filter(block => block.type === 'text').map(block => block.text).join('\n'),
      structuredContent: result.structuredContent, isError: !!result.isError,
      atConnection, afterDemand: counts(log) });
  } finally { await connection.close(); }
}
console.log(JSON.stringify(results));
