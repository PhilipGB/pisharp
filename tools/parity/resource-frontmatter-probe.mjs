import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { loadSkills } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/skills.ts')).href);
const { loadPromptTemplates } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/prompt-templates.ts')).href);
const { parseFrontmatter } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/utils/frontmatter.ts')).href);
const summary = text => ({ hash: createHash('sha256').update(text).digest('hex'), characters: text.length });
const results = [];
for (const scenario of JSON.parse(fs.readFileSync(process.argv[3], 'utf8'))) {
  const root = path.join(process.argv[4], scenario.id);
  const paths = scenario.paths.map(file => path.join(root, file));
  const skills = loadSkills({ cwd: root, agentDir: root, skillPaths: scenario.kind === 'skill' ? paths : [], includeDefaults: false });
  const prompts = loadPromptTemplates({ cwd: root, agentDir: root, promptPaths: scenario.kind === 'prompt' ? paths : [], includeDefaults: false });
  results.push({ id: scenario.id, result: {
    skills: skills.skills.map(s => ({ name: s.name, description: s.description, explicitOnly: s.disableModelInvocation, path: path.relative(root, s.filePath),
      body: summary(parseFrontmatter(fs.readFileSync(s.filePath, 'utf8')).body.trim()) })),
    prompts: prompts.templates.map(p => ({ name: p.name, description: p.description, argumentHint: p.argumentHint ?? null, path: path.relative(root, p.filePath), body: summary(p.content) })),
    diagnostics: [...skills.diagnostics, ...prompts.diagnostics].map(d => ({ type: d.type, path: path.relative(root, d.path),
      category: d.message.startsWith('name ') || d.message.startsWith('description ') ? d.message : 'parse warning' }))
  }, error: null, warningMessages: [...skills.diagnostics, ...prompts.diagnostics].map(d => d.message) });
}
console.log(JSON.stringify(results));
