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
  const root = path.join(process.argv[4], scenario.id);
  const paths = scenario.paths.map(file => path.join(root, file));
  const loader = new DefaultResourceLoader({ cwd: root, agentDir: root,
    settingsManager: SettingsManager.create(root, root, { projectTrusted: false }),
    additionalSkillPaths: scenario.kind === 'skill' ? paths : [],
    additionalPromptTemplatePaths: scenario.kind === 'prompt' ? paths : [],
    noSkills: true, noPromptTemplates: true, noExtensions: true, noThemes: true, noContextFiles: true });
  await loader.reload();
  const skills = loader.getSkills(), prompts = loader.getPrompts();
  results.push({ id: scenario.id, result: {
    skills: skills.skills.map(s => ({ name: s.name, description: s.description, path: path.relative(root, s.filePath),
      body: summary(parseFrontmatter(fs.readFileSync(s.filePath, 'utf8')).body.trim()) })),
    prompts: prompts.prompts.map(p => ({ name: p.name, description: p.description, path: path.relative(root, p.filePath), body: summary(p.content) })),
    diagnostics: [...skills.diagnostics, ...prompts.diagnostics].map(d => ({ type: d.type, path: path.relative(root, d.path),
      category: d.message }))
  }, error: null });
}
console.log(JSON.stringify(results));
