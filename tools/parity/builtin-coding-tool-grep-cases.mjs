import * as fs from 'node:fs/promises';
import { tmpdir, availableParallelism } from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

export async function captureGrepFileOrdering(factories, fixture) {
  const rows = [];
  const selected = fixture.orderingCases ?? ['grep-rgignore-precedence'];
  for (const item of fixture.cases.filter(item => selected.includes(item.caseId))) {
    const observations = [];
    for (let iteration = 0; iteration < (fixture.orderingIterations ?? 8); iteration++) {
      const root = await fs.mkdtemp(path.join(tmpdir(), 'pisharp-pinned-grep-order-'));
      try {
        for (const [name, text] of Object.entries(item.setup.files)) {
          const target = path.join(root, name);
          await fs.mkdir(path.dirname(target), { recursive: true });
          await fs.writeFile(target, text);
        }
        for (const name of item.setup.gitDirectories ?? [])
          execFileSync('git', ['init', '-q', path.join(root, name)]);
        const result = await factories[item.setup.tool](root).execute('order-' + iteration, item.setup.arguments);
        observations.push(result);
      } finally { await fs.rm(root, { recursive: true, force: true }); }
    }
    rows.push({ caseId: item.caseId, setup: item.setup, observations });
  }
  return { availableParallelism: availableParallelism(),
    cpuProfile: (await fs.readFile('/proc/self/status', 'utf8')).split('\n').find(line => line.startsWith('Cpus_allowed_list:')),
    normalization: 'None. Each complete observed result is retained in its actual file order.', cases: rows };
}
