import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const pi = process.argv[2], parent = process.argv[3];
verifyPiCheckout(pi);
const { DefaultResourceLoader } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/resource-loader.ts').href);
const { SettingsManager } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/settings-manager.ts').href);
const results = [];
for (const id of ['disabled', 'enabled', 'untrusted']) {
  const root = path.join(parent, id), cwd = path.join(root, 'project'), agent = path.join(root, 'agent');
  const pkg = path.join(root, 'package'), file = path.join(pkg, 'extensions/index.ts'), trace = path.join(root, 'pi-trace.log');
  for (const directory of [path.join(cwd, '.pi'), agent, path.dirname(file)]) fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(file, `import {appendFileSync} from 'node:fs'; export default function(){appendFileSync(${JSON.stringify(trace)}, 'configured\\n');}`);
  fs.writeFileSync(path.join(pkg, 'package.json'), JSON.stringify({ pi: { extensions: ['extensions'] } }));
  fs.writeFileSync(path.join(agent, 'settings.json'), JSON.stringify({ packages: ['../package'] }));
  fs.writeFileSync(path.join(cwd, '.pi/settings.json'), JSON.stringify({ packages: [
    id === 'enabled' ? '../../package' : { source: '../../package', extensions: [] }
  ] }));
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
  results.push({ id, bootstrapCount, configurationTrace: fs.readFileSync(trace, 'utf8').trim().split('\n'),
    errors: extensions.errors, loaded: extensions.extensions.map(extension => ({
      path: relative(extension.path), sourceInfo: { path: relative(extension.sourceInfo.path),
        source: extension.sourceInfo.source, scope: extension.sourceInfo.scope, origin: extension.sourceInfo.origin,
        baseDir: relative(extension.sourceInfo.baseDir) }
    })) });
}
console.log(JSON.stringify(results));
