import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

const [pi, indexPath] = process.argv.slice(2);
const { normalizePath } = await import(pathToFileURL(`${pi}/packages/coding-agent/src/utils/paths.ts`).href);
const cases = JSON.parse(fs.readFileSync(indexPath, 'utf8'));
const results = cases.map(test => {
  try {
    return { id: test.id, actual: { value: normalizePath(test.pattern) } };
  } catch (error) {
    return { id: test.id, actual: { error: {
      name: error.name,
      code: error.code ?? null,
      message: error.message
    } } };
  }
});
process.stdout.write(`${JSON.stringify(results, null, 2)}\n`);
