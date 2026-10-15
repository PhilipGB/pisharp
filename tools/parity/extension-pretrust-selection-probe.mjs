import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const pi = process.argv[2], parent = process.argv[3];
verifyPiCheckout(pi);
const { DefaultResourceLoader } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/resource-loader.ts').href);
const { SettingsManager } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/settings-manager.ts').href);
const results = [];
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
  const result = { id, bootstrapCount, configurationTrace: fs.readFileSync(trace, 'utf8').trim().split('\n'),
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
