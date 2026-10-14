import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { DefaultResourceLoader } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/resource-loader.ts')).href);
const { SettingsManager } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/settings-manager.ts')).href);
const { parseArgs } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/cli/args.ts')).href);
const results = [];
const originalDirectory = process.cwd();
const originalError = console.error;
try {
  for (const scenario of JSON.parse(fs.readFileSync(process.argv[3], 'utf8'))) {
    const root = path.join(process.argv[4], 'pi', scenario.id), cwd = path.join(root, 'project'), agentDir = path.join(root, 'agent');
    fs.mkdirSync(path.join(cwd, '.pi'), { recursive: true });
    fs.mkdirSync(agentDir, { recursive: true });
    for (const [relative, value] of Object.entries(scenario.files)) {
      const file = path.join(root, relative);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      if (value.directory) { fs.mkdirSync(file, { recursive: true }); continue; }
      fs.writeFileSync(file, Buffer.from(value.bytes, 'base64'));
      if (value.denied) fs.chmodSync(file, 0);
    }
    process.chdir(cwd);
    const warnings = [];
    console.error = message => {
      const match = String(message).match(/^(Warning: Could not read .+? file .+?): /);
      if (!match) throw new Error(String(message));
      warnings.push(match[1].replaceAll(root, '<workspace>'));
    };
    const arguments_ = [];
    if ('system' in scenario) arguments_.push('--system-prompt', scenario.system);
    if ('append' in scenario) for (const input of scenario.append) arguments_.push('--append-system-prompt', input);
    const options = parseArgs(arguments_);
    const loader = new DefaultResourceLoader({ cwd, agentDir,
      settingsManager: SettingsManager.create(cwd, agentDir, { projectTrusted: scenario.trusted }),
      noExtensions: true, noSkills: true, noPromptTemplates: true, noThemes: true, noContextFiles: true,
      systemPrompt: options.systemPrompt, appendSystemPrompt: options.appendSystemPrompt });
    await loader.reload();
    const summary = text => {
      if (text === undefined) return null;
      text = text.replaceAll(root, '<workspace>');
      return { hash: createHash('sha256').update(text).digest('hex'), characters: text.length };
    };
    results.push({ id: scenario.id, result: { system: summary(loader.getSystemPrompt()),
      append: summary(loader.getAppendSystemPrompt().join('\n\n')),
      systemPath: loader.getSystemPromptSource() ? path.relative(root, loader.getSystemPromptSource().path) : null,
      appendPaths: loader.getAppendSystemPromptSources().map(source => path.relative(root, source.path)) }, error: null, warnings });
  }
  console.log(JSON.stringify(results));
} finally { process.chdir(originalDirectory); console.error = originalError; }
