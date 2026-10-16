import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { loadProjectContextFiles } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/resource-loader.ts')).href);
const escape = value => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&apos;');
const results = [];
for (const scenario of JSON.parse(fs.readFileSync(process.argv[3], 'utf8'))) {
  const root = path.join(process.argv[4], 'pi', scenario.id);
  fs.mkdirSync(root, { recursive: true });
  for (const [relative, value] of Object.entries(scenario.files)) {
    const file = path.join(root, relative);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    if (value.fifo) {
      const { execFileSync } = await import('node:child_process');
      execFileSync('mkfifo', [file]);
      continue;
    }
    fs.writeFileSync(file, value.gitDirectory ? 'gitdir: ' + path.join(root, value.gitDirectory) + '\n' : Buffer.from(value.bytes, 'base64'));
    if (value.denied) fs.chmodSync(file, 0);
  }
  const cwd = path.join(root, scenario.cwd), agentDir = path.join(root, scenario.agent);
  fs.mkdirSync(cwd, { recursive: true });
  fs.mkdirSync(agentDir, { recursive: true });
  const warnings = [];
  const originalError = console.error;
  let files;
  console.error = message => {
    const match = String(message).match(/Warning: Could not read (.+?): /);
    if (!match) throw new Error('unexpected diagnostic: ' + message);
    warnings.push(path.relative(root, match[1]).replaceAll('\\', '/'));
  };
  try { files = loadProjectContextFiles({ cwd, agentDir }); }
  finally { console.error = originalError; }
  const text = files.map(file => `<project_instructions path="${escape(file.path)}">\n${file.content}\n</project_instructions>\n\n`).join('')
    .replaceAll(escape(root), '<workspace>');
  results.push({ id: scenario.id, paths: files.map(file => path.relative(root, file.path).replaceAll('\\', '/')),
    hash: createHash('sha256').update(text).digest('hex'), error: null, warnings });
}
console.log(JSON.stringify(results));
