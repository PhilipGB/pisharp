import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createRequire, syncBuiltinESMExports } from 'node:module';
import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { FileAuthStorageBackend } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/auth-storage.ts')).href);
if (process.argv[3] === 'hold' || process.argv[3] === 'write') {
  const backend = new FileAuthStorageBackend(process.argv[4]);
  const write = name => backend.withLockAsync(async current => ({ result: undefined,
    next: JSON.stringify({ ...JSON.parse(current ?? '{}'), [name]: { type: 'api_key', key: 'fixture-key' } }) }));
  if (process.argv[3] === 'hold') {
    await write('first');
    await backend.withLockAsync(async () => {
      console.log('ready');
      await new Promise(resolve => process.stdin.once('data', resolve));
      process.stdin.pause();
      return { result: undefined };
    });
  } else {
    console.log('writing');
    await write('second');
    console.log(JSON.stringify(await backend.withLockAsync(async current => ({ result: Object.keys(JSON.parse(current)).sort() }))));
  }
} else {
  const require = createRequire(path.join(pi, 'packages/coding-agent/package.json'));
  const lockfile = require('proper-lockfile');
  const timers = require('node:timers/promises');
  const originalSleep = timers.setTimeout;
  const originalLock = lockfile.lock;
  const originalPlatform = process.platform;
  const originalRandom = Math.random;
  const results = [];
  try {
    Math.random = () => 0;
    for (const scenario of JSON.parse(readFileSync(process.argv[3], 'utf8'))) {
      const root = mkdtempSync(path.join(tmpdir(), 'pi-auth-policy-'));
      try {
        const backend = new FileAuthStorageBackend(path.join(root, 'auth.json'));
        Object.defineProperty(process, 'platform', { value: scenario.platform, configurable: true });
        const controller = new AbortController();
        if (scenario.cancel === 'before') controller.abort();
        let attempts = 0;
        let updates = 0;
        let releases = 0;
        let succeeded = false;
        const codes = [...scenario.codes];
        const delays = [];
        timers.setTimeout = async milliseconds => { delays.push(milliseconds); };
        syncBuiltinESMExports();
        lockfile.lock = async () => {
          attempts++;
          const code = codes.shift();
          if (code) {
            if (scenario.cancel === 'rejection') controller.abort();
            throw Object.assign(new Error('Fixture native lock error.'), { code });
          }
          if (scenario.cancel === 'acquired') controller.abort();
          return async () => { releases++; };
        };
        try {
          await backend.withLockAsync(async () => { updates++; return { result: undefined }; }, { signal: controller.signal });
          succeeded = true;
        } catch (error) {
          if (!controller.signal.aborted && !error.code) throw error;
        }
        results.push({ id: scenario.id, succeeded, canceled: controller.signal.aborted, attempts, updates, releases, delays });
      } finally { rmSync(root, { recursive: true, force: true }); }
    }
  } finally {
    lockfile.lock = originalLock;
    timers.setTimeout = originalSleep;
    syncBuiltinESMExports();
    Object.defineProperty(process, 'platform', { value: originalPlatform, configurable: true });
    Math.random = originalRandom;
  }
  console.log(JSON.stringify(results));
}
