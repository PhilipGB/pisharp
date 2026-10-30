import fs from 'node:fs';
import path from 'node:path';
import { homedir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';

const pi = process.argv[3];
const { verifyPiCheckout } = await import(new URL('../pi-reference.mjs', import.meta.url).href);
verifyPiCheckout(pi);
const minimatchPackage = JSON.parse(fs.readFileSync(path.join(pi, 'node_modules/minimatch/package.json'), 'utf8'));
if (minimatchPackage.version !== '10.2.6') {
  throw new Error(`Unexpected Pi minimatch version: ${minimatchPackage.version}`);
}
const { minimatch } = await import(pathToFileURL(path.join(pi, 'node_modules/minimatch/dist/esm/index.js')).href);
const cases = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const root = path.join(homedir(), 'resource-glob-contract');
const normalizePattern = pattern => {
  let normalized = pattern;
  if (normalized === '~') normalized = homedir();
  else if (normalized.startsWith('~/')) normalized = path.join(homedir(), normalized.slice(2));
  else if (normalized.startsWith('file://')) normalized = fileURLToPath(normalized);
  return process.platform === 'win32' ? normalized.replaceAll('\\', '/') : normalized;
};
const candidatesFor = test => {
  const absolute = path.resolve(root, test.path);
  const candidates = [path.relative(root, absolute).split(path.sep).join('/'), path.basename(absolute), absolute.split(path.sep).join('/')];
  if (test.resourceType === 'skills' && path.basename(absolute) === 'SKILL.md') {
    const parent = path.dirname(absolute);
    candidates.push(path.relative(root, parent).split(path.sep).join('/'), path.basename(parent), parent.split(path.sep).join('/'));
  }
  return { absolute, candidates };
};
const exactMatches = (test, patterns) => {
  const { absolute } = candidatesFor(test);
  const relative = path.relative(root, absolute).split(path.sep).join('/');
  const isSkill = test.resourceType === 'skills' && path.basename(absolute) === 'SKILL.md';
  const parent = path.dirname(absolute);
  return patterns.some(pattern => {
    const withoutDot = pattern.startsWith('./') || pattern.startsWith('.\\') ? pattern.slice(2) : pattern;
    const normalized = normalizePattern(withoutDot.replaceAll('$BASE', root));
    return normalized === relative || normalized === absolute.split(path.sep).join('/') ||
      isSkill && (normalized === path.relative(root, parent).split(path.sep).join('/') ||
        normalized === parent.split(path.sep).join('/'));
  });
};
const results = cases.map(test => {
  let expected;
  if (test.overrides) {
    const excludes = test.overrides.filter(value => value.startsWith('!')).map(value => value.slice(1));
    const includes = test.overrides.filter(value => value.startsWith('+')).map(value => value.slice(1));
    const forceExcludes = test.overrides.filter(value => value.startsWith('-')).map(value => value.slice(1));
    const { candidates } = candidatesFor(test);
    expected = !excludes.some(pattern => candidates.some(candidate =>
      minimatch(candidate, normalizePattern(pattern.replaceAll('$BASE', root)))));
    if (exactMatches(test, includes)) expected = true;
    if (exactMatches(test, forceExcludes)) expected = false;
  } else {
    const { candidates } = candidatesFor(test);
    expected = candidates.some(candidate =>
      minimatch(candidate, normalizePattern(test.pattern.replaceAll('$BASE', root))));
  }
  return { ...test, expected, minimatchVersion: minimatchPackage.version };
});
process.stdout.write(`${JSON.stringify(results, null, 2)}\n`);
