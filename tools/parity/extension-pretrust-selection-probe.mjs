import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const pi = process.argv[2], parent = process.argv[3];
verifyPiCheckout(pi);
const { DefaultResourceLoader } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/resource-loader.ts').href);
const { SettingsManager } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/settings-manager.ts').href);
const results = [];
if (process.argv[4] === '--context') {
  const root = path.join(parent, 'context'), cwd = path.join(root, 'project'), agent = path.join(root, 'agent');
  fs.mkdirSync(cwd, { recursive: true });
  fs.mkdirSync(agent, { recursive: true });
  const events = [], resources = [];
  const state = { model: { id: 'stale-model', provider: 'stale-provider' }, idle: true, trusted: false };
  const loader = new DefaultResourceLoader({ cwd, agentDir: agent, noSkills: true, noPromptTemplates: true,
    noThemes: true, noContextFiles: true, extensionFactories: [{ name: 'context-fixture', factory: pi => {
      pi.on('resources_discover', event => { resources.push(event.reason); });
      pi.on('tool_call', event => { if (event.toolName === 'extension_context_probe') events.push('tool_start'); });
      pi.on('tool_result', event => { if (event.toolName === 'extension_context_probe') events.push('tool_result'); });
      pi.registerTool({ name: 'extension_context_probe', label: 'context probe', description: 'Context probe',
        parameters: { type: 'object', properties: {}, additionalProperties: false },
        execute: async (_id, _params, _signal, onUpdate, ctx) => {
          const snapshot = {
            workingDirectory: ctx.cwd,
            mode: ctx.mode,
            hasUserInterface: ctx.hasUI,
            isProjectTrusted: ctx.isProjectTrusted(),
            isIdle: ctx.isIdle(),
            currentModelId: ctx.model?.id,
            currentProviderId: ctx.model?.provider,
            activeToolNames: ['extension_context_probe']
          };
          onUpdate?.({ content: [{ type: 'text', text: 'extension context progress' }] });
          return { content: [{ type: 'text', text: JSON.stringify(snapshot) }] };
        } });
    } }] });
  await loader.reload();
  const loaded = loader.getExtensions();
  const load = file => import(pathToFileURL(pi + '/packages/coding-agent/src/core/' + file + '.ts').href);
  const [{ ExtensionRunner }, { SessionManager }, { ModelRuntime }, { ModelRegistry }, { AuthStorage }] =
    await Promise.all(['extensions/runner', 'session-manager', 'model-runtime', 'model-registry', 'auth-storage'].map(load));
  const models = await ModelRuntime.create({ credentials: AuthStorage.inMemory(), modelsPath: null, refreshOnCreate: false });
  const runner = new ExtensionRunner(loaded.extensions, loaded.runtime, cwd, SessionManager.inMemory(cwd), new ModelRegistry(models));
  const noopActions = new Proxy({}, { get: (_target, _property) => () => undefined });
  runner.bindCore(noopActions, {
    getModel: () => state.model, getScopedModels: () => [], isIdle: () => state.idle,
    isProjectTrusted: () => state.trusted, getSignal: () => undefined, abort: () => {},
    hasPendingMessages: () => false, shutdown: () => {}, getContextUsage: () => undefined,
    compact: () => {}, getSystemPrompt: () => '', getCallableTools: () => []
  });
  runner.setUIContext(undefined, 'rpc');
  const lazyContext = runner.createContext();
  state.model = { id: 'extension-context-model', provider: 'fixture' };
  state.idle = false;
  state.trusted = true;
  await runner.emitResourcesDiscover(cwd, 'startup');
  await runner.emitResourcesDiscover(cwd, 'reload');
  const tool = loaded.extensions[0].tools.get('extension_context_probe');
  await runner.emitToolCall({ type: 'tool_call', toolCallId: 'context-call', toolName: 'extension_context_probe', input: {} });
  const result = await tool.definition.execute('context-call', {}, undefined, () => events.push('tool_update'),
    runner.createToolContext('context-call', undefined));
  await runner.emitToolResult({ type: 'tool_result', toolCallId: 'context-call', toolName: 'extension_context_probe',
    input: {}, content: result.content, details: undefined, isError: false });
  const stateSnapshot = JSON.parse(result.content[0].text);
  if (lazyContext.model?.id !== 'extension-context-model' || lazyContext.isIdle() !== false ||
      lazyContext.isProjectTrusted() !== true || lazyContext.mode !== 'rpc' || lazyContext.hasUI !== false)
    throw new Error('Pi extension context did not resolve current bindings when read.');
  console.log(JSON.stringify({ state: stateSnapshot, events, resources }));
  process.exit(0);
}
if (process.argv[4] === '--conflicts') {
  const root = path.join(parent, 'conflicts'), cwd = path.join(root, 'project'), agent = path.join(root, 'agent');
  fs.mkdirSync(cwd, { recursive: true });
  fs.mkdirSync(agent, { recursive: true });
  const factory = owner => api => {
    api.registerTool({ name: 'shared_tool', label: owner, description: `${owner} tool`,
      parameters: { type: 'object', properties: {} }, execute: async () => ({ content: [{ type: 'text', text: owner }] }) });
    api.registerCommand('deploy', { description: `${owner} command`, handler: async () => {} });
    if (owner === 'project') api.registerCommand('mcp', { description: 'project mcp command', handler: async () => {} });
    else api.registerCommand('user_only', { description: 'user command', handler: async () => {} });
  };
  const loader = new DefaultResourceLoader({ cwd, agentDir: agent, extensionFactories: [
    { name: 'project', factory: factory('project') },
    { name: 'user', factory: factory('user') },
    { name: 'mcp', builtin: true, replaceable: true, factory: api => {
      api.registerTool({ name: 'builtin_marker', label: 'builtin', parameters: { type: 'object', properties: {} },
        execute: async () => ({ content: [{ type: 'text', text: 'builtin' }] }) });
      api.registerCommand('mcp', { description: 'built-in mcp command', handler: async () => {} });
    } }
  ] });
  await loader.reload();
  const extensions = loader.getExtensions();
  const load = file => import(pathToFileURL(pi + '/packages/coding-agent/src/core/' + file + '.ts').href);
  const [{ ExtensionRunner }, { SessionManager }, { ModelRuntime }, { ModelRegistry }, { AuthStorage }] =
    await Promise.all(['extensions/runner', 'session-manager', 'model-runtime', 'model-registry', 'auth-storage'].map(load));
  const models = await ModelRuntime.create({ credentials: AuthStorage.inMemory(), modelsPath: null, refreshOnCreate: false });
  const runner = new ExtensionRunner(extensions.extensions, extensions.runtime, cwd, SessionManager.inMemory(cwd), new ModelRegistry(models));
  const result = {
    loaded: extensions.extensions.map(extension => extension.path),
    tool: (() => { const definition = runner.getToolDefinition('shared_tool'); return definition && { name: definition.name, description: definition.description }; })(),
    tools: runner.getAllRegisteredTools().map(tool => tool.definition.name).sort(),
    commands: runner.getRegisteredCommands().map(command => ({ name: command.invocationName, description: command.description }))
      .sort((left, right) => left.name.localeCompare(right.name)),
    errors: extensions.errors,
    warnings: extensions.warnings ?? []
  };
  console.log(JSON.stringify(result));
  process.exit(0);
}
for (const id of process.argv[4] === '--ordering' ? ['project-added'] : ['disabled', 'enabled', 'untrusted']) {
  const root = path.join(parent, id), cwd = path.join(root, 'project'), agent = path.join(root, 'agent');
  const pkg = path.join(root, 'package'), file = path.join(pkg, 'extensions/index.ts'), trace = path.join(root, 'pi-trace.log');
  for (const directory of [path.join(cwd, '.pi'), agent, path.dirname(file)]) fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(file, `import {appendFileSync} from 'node:fs'; export default function(pi){appendFileSync(${JSON.stringify(trace)}, 'configured\\n');${id === 'project-added' ? `pi.on('resources_discover',()=>({skillPaths:[${JSON.stringify(path.join(cwd, '.pi/extension-resources/startup/skills'))}]}));` : ''}}`);
  fs.writeFileSync(path.join(pkg, 'package.json'), JSON.stringify({ pi: { extensions: ['extensions'] } }));
  fs.writeFileSync(path.join(agent, 'settings.json'), JSON.stringify({ packages: ['../package'] }));
  fs.writeFileSync(path.join(cwd, '.pi/settings.json'), JSON.stringify({ packages: [
    id === 'enabled' ? '../../package' : { source: '../../package', extensions: [] }
  ] }));
  if (id === 'project-added') {
    const projectPackage = path.join(root, 'project-package');
    fs.mkdirSync(path.join(projectPackage, 'extensions'), { recursive: true });
    fs.copyFileSync(file, path.join(projectPackage, 'extensions/index.ts'));
    fs.copyFileSync(path.join(pkg, 'package.json'), path.join(projectPackage, 'package.json'));
    fs.writeFileSync(path.join(cwd, '.pi/settings.json'), JSON.stringify({ packages: ['../../project-package'] }));
  }
  const loader = new DefaultResourceLoader({ cwd, agentDir: agent,
    settingsManager: SettingsManager.create(cwd, agent, { projectTrusted: false }),
    noSkills: true, noPromptTemplates: true, noThemes: true, noContextFiles: true });
  let bootstrapCount;
  await loader.reload({ resolveProjectTrust: ({ extensionsResult }) => {
    bootstrapCount = extensionsResult.extensions.length;
    return id !== 'untrusted';
  } });
  const relative = value => value == null ? null : path.relative(root, value).replace(/\.ts$/, '.<module>');
  const extensions = loader.getExtensions();
  const result = { id, bootstrapCount, configurationTrace: fs.existsSync(trace) ? fs.readFileSync(trace, 'utf8').trim().split('\n') : [],
    errors: extensions.errors, loaded: extensions.extensions.map(extension => ({
      path: relative(extension.path), sourceInfo: { path: relative(extension.sourceInfo.path),
        source: extension.sourceInfo.source, scope: extension.sourceInfo.scope, origin: extension.sourceInfo.origin,
        baseDir: relative(extension.sourceInfo.baseDir) }
    })) };
  if (id === 'project-added') {
    const load = file => import(pathToFileURL(pi + '/packages/coding-agent/src/core/' + file + '.ts').href);
    const [{ ExtensionRunner }, { SessionManager }, { ModelRuntime }, { ModelRegistry }, { AuthStorage }] =
      await Promise.all(['extensions/runner', 'session-manager', 'model-runtime', 'model-registry', 'auth-storage'].map(load));
    const models = await ModelRuntime.create({ credentials: AuthStorage.inMemory(), modelsPath: null, refreshOnCreate: false });
    const runner = new ExtensionRunner(extensions.extensions, extensions.runtime, cwd, SessionManager.inMemory(cwd), new ModelRegistry(models));
    const discovered = await runner.emitResourcesDiscover(cwd, 'startup');
    result.discoveryBaseDirs = discovered.skillPaths.map(entry => relative(path.dirname(entry.extensionPath)));
  }
  results.push(result);
}
console.log(JSON.stringify(results));
