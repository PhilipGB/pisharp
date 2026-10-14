import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { Worker } from 'node:worker_threads';
import { verifyPiCheckout } from './pi-reference.mjs';

const pi = process.argv[2];
verifyPiCheckout(pi);
const { CodemodeSandbox } = await import(pathToFileURL(path.join(pi, 'packages/codemode/src/index.ts')).href);
const original = Worker.prototype.terminate;
let requested = false;
const exits = [];
Worker.prototype.terminate = function (...args) {
  requested = true;
  return original.apply(this, args);
};
const sandbox = new CodemodeSandbox({
  onWorkerStarted: exit => exits.push(exit)
});
try {
  const result = await sandbox.execute('return 1');
  if (!result.ok || result.value !== 1) throw new Error('The source guest did not complete normally');
  console.log(JSON.stringify({ terminationRequestedBeforePoolAvailable: requested }));
} finally {
  await sandbox.close();
  Worker.prototype.terminate = original;
  let timer;
  try {
    await Promise.race([Promise.all(exits), new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error('Source workers did not exit')), 3000);
    })]);
  } finally { clearTimeout(timer); }
}
