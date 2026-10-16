import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
import { mkdtempSync, mkdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const require = createRequire(path.join(pi, 'packages/coding-agent/package.json'));
const { Type } = require('typebox');
const { getModel } = await load('packages/ai/dist/compat.js');
const { createAgentSession } = await load('packages/coding-agent/dist/core/sdk.js');
const { SessionManager } = await load('packages/coding-agent/dist/core/session-manager.js');
const { SettingsManager } = await load('packages/coding-agent/dist/core/settings-manager.js');
const { DefaultResourceLoader } = await load('packages/coding-agent/dist/core/resource-loader.js');
const results = [];
for (const id of JSON.parse(process.argv[3])) {
  const root = mkdtempSync(path.join(tmpdir(), 'pi-tool-resume-'));
  const agentDir = path.join(root, 'agent');
  mkdirSync(agentDir);
  let session;
  try {
    const settingsManager = SettingsManager.inMemory(id === 'settings' ? { defaultTools: ['read'] } : {});
    const resourceLoader = new DefaultResourceLoader({ cwd: root, agentDir, settingsManager,
      extensionFactories: [pi => {
        for (const name of ['inactive_tool', 'active_tool']) pi.registerTool({ name, label: name,
          description: name, parameters: Type.Object({}), defaultActive: name === 'active_tool',
          execute: async () => ({ content: [{ type: 'text', text: name }], details: {} }) });
      }] });
    await resourceLoader.reload();
    const sessionManager = SessionManager.inMemory(root);
    if (!id.startsWith('fresh')) {
      sessionManager.appendMessage({ role: 'system', content: 'instructions', timestamp: 1,
        toolsAdded: (id === 'empty' ? [] : ['read', 'grep', 'inactive_tool'])
          .map(name => ({ name, description: name, parameters: Type.Object({}) })) });
      if (id === 'update') sessionManager.appendMessage({ role: 'system', content: 'updated', timestamp: 2,
        toolsRemoved: [{ name: 'grep' }], toolsAdded: [{ name: 'write', description: 'write', parameters: Type.Object({}) }] });
    }
    const options = id === 'explicit' ? { tools: ['read', 'write'] } :
      id === 'modifiers' ? { tools: ['-grep', '+write'] } :
      id === 'excluded' ? { excludeTools: ['inactive_tool'] } :
      id === 'disabled' ? { noTools: 'all' } :
      id === 'fresh-modifiers' ? { tools: ['-write', '-active_tool'] } : {};
    ({ session } = await createAgentSession({ cwd: root, agentDir,
      model: getModel('anthropic', 'claude-sonnet-4-5'), settingsManager, resourceLoader, sessionManager, ...options }));
    results.push({ id, active: session.getActiveToolNames().sort() });
  } finally {
    session?.dispose();
    rmSync(root, { recursive: true, force: true });
  }
}
console.log(JSON.stringify(results));
