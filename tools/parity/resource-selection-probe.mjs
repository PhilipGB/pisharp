import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { DefaultResourceLoader } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/resource-loader.ts')).href);
const { SettingsManager } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/settings-manager.ts')).href);
const { parseFrontmatter } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/utils/frontmatter.ts')).href);
const summary = text => ({ hash: createHash('sha256').update(text).digest('hex'), characters: text.length });
const results = [];
for (const scenario of JSON.parse(fs.readFileSync(process.argv[3], 'utf8'))) {
  const root = path.join(process.argv[4], scenario.id), cwd = path.join(root, scenario.cwd || 'project'), agentDir = path.join(root, 'agent');
  const relative = value => value == null ? null : path.relative(root, value);
  const source = info => ({ path: relative(info.path), source: info.source, scope: info.scope,
    origin: info.origin, baseDir: relative(info.baseDir) });
  const diagnostic = d => ({ type: d.type, message: d.message, path: relative(d.path), collision: d.collision ? {
    resourceType: d.collision.resourceType, name: d.collision.name,
    winnerPath: relative(d.collision.winnerPath), loserPath: relative(d.collision.loserPath) } : null });
  const loader = new DefaultResourceLoader({ cwd, agentDir,
    settingsManager: SettingsManager.create(cwd, agentDir, { projectTrusted: !!scenario.trusted }),
    noSkills: !scenario.discover, noPromptTemplates: !scenario.discover,
    noExtensions: true, noThemes: true, noContextFiles: true,
    additionalSkillPaths: (scenario.skillPaths || []).map(p => path.join(root, p)),
    additionalPromptTemplatePaths: (scenario.promptPaths || []).map(p => path.join(root, p)) });
  await loader.reload();
  const extensionPaths = paths => (paths || []).map(p => ({ path: path.join(root, p),
    metadata: { source: 'fixture-extension', scope: 'user', origin: 'package', baseDir: path.join(root, 'outside') } }));
  loader.extendResources({ skillPaths: extensionPaths(scenario.extensionSkillPaths),
    promptPaths: extensionPaths(scenario.extensionPromptPaths) });
  const skills = loader.getSkills(), prompts = loader.getPrompts();
  results.push({ id: scenario.id,
    skills: skills.skills.map(s => ({ name: s.name, description: s.description, path: relative(s.filePath),
      body: summary(parseFrontmatter(fs.readFileSync(s.filePath, 'utf8')).body.trim()), sourceInfo: source(s.sourceInfo) })),
    prompts: prompts.prompts.map(p => ({ name: p.name, description: p.description, path: relative(p.filePath),
      body: summary(p.content), sourceInfo: source(p.sourceInfo) })),
    skillDiagnostics: skills.diagnostics.map(diagnostic), promptDiagnostics: prompts.diagnostics.map(diagnostic) });
}
console.log(JSON.stringify(results));
