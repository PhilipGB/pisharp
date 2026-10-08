import readline from 'node:readline';
import { TerminalState } from './terminal-state.mjs';

let terminal;
for await (const line of readline.createInterface({ input: process.stdin })) {
  try {
    const request = JSON.parse(line);
    switch (request.op) {
      case 'start': terminal = new TerminalState(request.options); break;
      case 'write': await terminal.write(Buffer.from(request.data, 'base64')); break;
      case 'resize': terminal.resize(request.columns, request.rows); break;
      case 'snapshot': break;
      default: throw new Error(`Unknown operation: ${request.op}`);
    }
    const result = { replies: Buffer.from(terminal.takeReplies()).toString('base64'),
      renderFrames: terminal.takeRenderFrames() };
    if (request.op === 'snapshot') result.frame = terminal.snapshot();
    process.stdout.write(JSON.stringify(result) + '\n');
  } catch (error) {
    process.stdout.write(JSON.stringify({ error: error.stack }) + '\n');
  }
}
terminal?.dispose();
