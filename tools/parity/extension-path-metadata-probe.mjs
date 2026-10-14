import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const pi = process.argv[2], parent = process.argv[3];
verifyPiCheckout(pi);
const { DefaultResourceLoader } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/resource-loader.ts').href);
const { SettingsManager } = await import(pathToFileURL(pi + '/packages/coding-agent/src/core/settings-manager.ts').href);
const results = [];
for (const id of ['plain', 'file-url', 'alias']) {
  const root = path.join(parent, id), cwd = path.join(root, 'project'), agent = path.join(root, 'agent');
  const pkg = path.join(root, 'package'), file = path.join(pkg, 'extensions/index.ts');
  for (const directory of [cwd, agent, path.dirname(file)]) fs.mkdirSync(directory, { recursive: true });
  fs.writeFileSync(file, 'export default function() {}');
  fs.writeFileSync(path.join(pkg, 'package.json'), JSON.stringify({ pi: { extensions: ['extensions'] } }));
  fs.writeFileSync(path.join(agent, 'settings.json'), JSON.stringify({ packages: ['../package'] }));
  const alias = path.join(root, 'alias.ts');
  fs.symlinkSync(file, alias);
  const entry = id === 'alias' ? alias : id === 'file-url' ? pathToFileURL(file).href : file;
  const loader = new DefaultResourceLoader({ cwd, agentDir: agent,
    settingsManager: SettingsManager.create(cwd, agent, { projectTrusted: false }),
    additionalExtensionPaths: [entry], noSkills: true, noPromptTemplates: true, noThemes: true, noContextFiles: true });
  await loader.reload();
  const relative = value => value == null ? null : path.relative(root, value).replace(/\.ts$/, '.<module>');
  const extensions = loader.getExtensions();
  results.push({ id, errors: extensions.errors, loaded: extensions.extensions.map(extension => ({
    path: relative(extension.path), sourceInfo: { path: relative(extension.sourceInfo.path),
      source: extension.sourceInfo.source, scope: extension.sourceInfo.scope, origin: extension.sourceInfo.origin,
      baseDir: relative(extension.sourceInfo.baseDir) }
  })) });
}
console.log(JSON.stringify(results));
