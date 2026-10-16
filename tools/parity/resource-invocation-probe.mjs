import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { AgentSession } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/agent-session.ts')).href);
const { loadSkills } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/skills.ts')).href);
const { loadPromptTemplates, expandPromptTemplate } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/prompt-templates.ts')).href);
const results = [];
for (const scenario of JSON.parse(fs.readFileSync(process.argv[3], 'utf8'))) {
  const root = path.join(process.argv[4], 'pi', scenario.id);
  const file = path.join(root, 'guide/SKILL.md');
  const skills = loadSkills({ cwd: root, agentDir: root, skillPaths: [file], includeDefaults: false });
  const prompts = loadPromptTemplates({ cwd: root, agentDir: root, promptPaths: [path.join(root, 'prompts')], includeDefaults: false });
  if (scenario.after === 'delete') fs.unlinkSync(file);
  if (scenario.after === 'invalid-yaml') fs.writeFileSync(file, '---\ndescription: [broken\n---\nbody');
  if (scenario.after === 'changed') fs.writeFileSync(file, '---\ndescription: Updated\n---\nchanged body');
  if (scenario.after === 'denied') fs.chmodSync(file, 0);
  const errors = [];
  const receiver = { resourceLoader: { getSkills: () => skills }, _extensionRunner: { emitError: error => errors.push({
    event: error.event, path: path.relative(root, error.extensionPath), message: error.error.replaceAll(root, '<workspace>') }) } };
  const expanded = AgentSession.prototype._expandSkillCommand.call(receiver, scenario.input);
  const text = expandPromptTemplate(expanded, prompts.templates).replaceAll(root, '<workspace>');
  results.push({ id: scenario.id, text, hash: createHash('sha256').update(text).digest('hex'), errors, exception: null });
}
console.log(JSON.stringify(results));
