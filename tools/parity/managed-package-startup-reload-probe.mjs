import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {verifyPiCheckout} from './pi-reference.mjs';

const piRoot = path.resolve(process.argv[2]);
const root = path.resolve(process.argv[3]);
const scenario = process.argv[4];
verifyPiCheckout(piRoot);

const {SettingsManager} = await import(pathToFileURL(path.join(
  piRoot, 'packages/coding-agent/src/core/settings-manager.ts')).href);
const {DefaultResourceLoader} = await import(pathToFileURL(path.join(
  piRoot, 'packages/coding-agent/src/core/resource-loader.ts')).href);
const {stripFrontmatter} = await import(pathToFileURL(path.join(
  piRoot, 'packages/coding-agent/src/utils/frontmatter.ts')).href);

const agentDir = path.join(root, 'agent');
const cwd = path.join(root, 'project');
const packageRoot = scenario === 'git'
  ? path.join(agentDir, 'git', '127.0.0.1', 'fixture', 'repo')
  : path.join(root, 'package');
fs.mkdirSync(path.join(cwd, '.pi'), {recursive: true});
const source = scenario === 'git' ? 'git:git://127.0.0.1:54321/fixture/repo.git@main' : packageRoot;
const writePackage = (name, body) => {
  const skillDirectory = path.join(packageRoot, 'skills', 'guide');
  fs.mkdirSync(skillDirectory, {recursive: true});
  fs.writeFileSync(path.join(packageRoot, 'package.json'), JSON.stringify({pi: {skills: ['skills']}}));
  fs.writeFileSync(path.join(skillDirectory, 'SKILL.md'),
    `---\nname: ${name}\ndescription: ${name}\n---\n${body}\n`);
};
const writeSettings = (userPackages, projectPackages) => {
  fs.mkdirSync(agentDir, {recursive: true});
  fs.writeFileSync(path.join(agentDir, 'settings.json'), JSON.stringify({packages: userPackages}, null, 2));
  fs.writeFileSync(path.join(cwd, '.pi', 'settings.json'), JSON.stringify({packages: projectPackages}, null, 2));
};
writePackage('reload-resource', 'Initial managed package resource.');
writeSettings(scenario === 'trust-revoked' ? [] : [source],
  scenario === 'trust-revoked' ? [{source}] : []);

const settingsManager = SettingsManager.create(cwd, agentDir, {projectTrusted: true});
const loader = new DefaultResourceLoader({cwd, agentDir, settingsManager});
const snapshot = async () => {
  await loader.reload();
  const skills = loader.getSkills().skills;
  const selected = skills[0];
  const command = selected ? `/skill:${selected.name} args` : '/skill:reload-resource args';
  const invocation = selected
    ? (() => {
        const body = stripFrontmatter(fs.readFileSync(selected.filePath, 'utf-8')).trim();
        const block = `<skill name="${selected.name}" location="${selected.filePath}">\n` +
          `References are relative to ${selected.baseDir}.\n\n${body}\n</skill>`;
        return `${block}\n\nargs`;
      })()
    : command;
  return {
    skills: skills.map(skill => ({name: skill.name, description: skill.description}))
      .sort((left, right) => left.name.localeCompare(right.name)),
    prompts: loader.getPrompts().prompts.map(prompt => prompt.name).sort(),
    invocation: invocation.replaceAll(root, '<workspace>'),
  };
};

const before = await snapshot();
switch (scenario) {
  case 'local':
  case 'git':
    break;
  case 'updated-resource':
    writePackage('reload-resource-v2', 'Updated managed package resource.');
    break;
  case 'removed-resource':
    writeSettings([], []);
    fs.rmSync(packageRoot, {recursive: true, force: true});
    break;
  case 'disabled-resource':
    writeSettings([source], [{source, skills: []}]);
    break;
  case 'trust-revoked':
    settingsManager.setProjectTrusted(false);
    break;
  default:
    throw new Error(`Unknown startup/reload scenario: ${scenario}`);
}
const after = await snapshot();
console.log(JSON.stringify({scenario, before, after}));
