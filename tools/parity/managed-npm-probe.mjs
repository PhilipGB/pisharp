import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const piRoot = path.resolve(process.argv[2]);
const root = path.resolve(process.argv[3]);
const source = process.argv[4];
verifyPiCheckout(piRoot);

const { SettingsManager } = await import(pathToFileURL(path.join(piRoot,
  'packages/coding-agent/src/core/settings-manager.ts')).href);
const { DefaultPackageManager } = await import(pathToFileURL(path.join(piRoot,
  'packages/coding-agent/src/core/package-manager.ts')).href);
const { DefaultResourceLoader } = await import(pathToFileURL(path.join(piRoot,
  'packages/coding-agent/src/core/resource-loader.ts')).href);

const agentDir = path.join(root, 'agent');
const cwd = path.join(root, 'project');
const packageRoot = path.join(agentDir, 'npm', 'node_modules', 'managed-parity');
fs.mkdirSync(agentDir, { recursive: true });
fs.mkdirSync(cwd, { recursive: true });
fs.writeFileSync(path.join(agentDir, 'settings.json'), JSON.stringify({ packages: [source] }));
const settingsManager = SettingsManager.create(cwd, agentDir, { projectTrusted: false });
const manager = new DefaultPackageManager({ cwd, agentDir, settingsManager });

const relative = value => value == null ? null : path.relative(packageRoot, value).replaceAll('\\', '/');
const normalize = values => values.map(value => ({
  path: relative(value.path),
  enabled: value.enabled,
  source: value.metadata.source,
  scope: value.metadata.scope,
  origin: value.metadata.origin,
  baseDir: relative(value.metadata.baseDir),
})).sort((left, right) => left.path.localeCompare(right.path));
const startup = await manager.resolve();
const reloaded = await manager.resolve();

const loader = new DefaultResourceLoader({ cwd, agentDir, settingsManager });
await loader.reload();
const startupLoad = {
  skills: loader.getSkills().skills.map(skill => skill.name).sort(),
  prompts: loader.getPrompts().prompts.map(prompt => prompt.name).sort(),
  themes: loader.getThemes().themes.map(theme => theme.name).sort(),
  extensions: loader.getExtensions().extensions.map(extension => ({
    path: relative(extension.path),
    source: extension.sourceInfo.source,
    scope: extension.sourceInfo.scope,
    origin: extension.sourceInfo.origin,
    baseDir: relative(extension.sourceInfo.baseDir),
  })),
};
await loader.reload();
const reloadLoad = {
  skills: loader.getSkills().skills.map(skill => skill.name).sort(),
  prompts: loader.getPrompts().prompts.map(prompt => prompt.name).sort(),
  themes: loader.getThemes().themes.map(theme => theme.name).sort(),
  extensions: loader.getExtensions().extensions.map(extension => ({
    path: relative(extension.path),
    source: extension.sourceInfo.source,
    scope: extension.sourceInfo.scope,
    origin: extension.sourceInfo.origin,
    baseDir: relative(extension.sourceInfo.baseDir),
  })),
};

console.log(JSON.stringify({
  resolved: {
    startup: {
      skills: normalize(startup.skills), prompts: normalize(startup.prompts),
      themes: normalize(startup.themes), extensions: normalize(startup.extensions),
    },
    reloaded: {
      skills: normalize(reloaded.skills), prompts: normalize(reloaded.prompts),
      themes: normalize(reloaded.themes), extensions: normalize(reloaded.extensions),
    },
  },
  startupLoad,
  reloadLoad,
  dependencyInstalled: fs.existsSync(path.join(agentDir, 'npm', 'node_modules', 'managed-parity-dependency')),
  peerInstalled: fs.existsSync(path.join(agentDir, 'npm', 'node_modules', 'pisharp-host-api-must-not-install')),
}));
