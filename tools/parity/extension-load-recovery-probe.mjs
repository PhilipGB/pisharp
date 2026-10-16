import { pathToFileURL } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';
import { verifyPiCheckout } from './pi-reference.mjs';

const pi = process.argv[2], root = process.argv[3];
verifyPiCheckout(pi);
const { loadExtensions } = await import(pathToFileURL(path.join(pi, 'packages/coding-agent/src/core/extensions/loader.ts')).href);
fs.writeFileSync(path.join(root, 'bad.ts'), `export default function(pi) {
 pi.registerCommand('partial_command', {description:'Uncommitted', handler:async()=>{}});
 pi.registerTool({name:'partial_tool',label:'Partial',description:'Uncommitted',parameters:{type:'object',properties:{}},execute:async()=>({content:[]})});
 throw new Error('fixture extension configuration failure');
}`);
fs.writeFileSync(path.join(root, 'guide.ts'), 'export default function() {}');
const result = await loadExtensions([path.join(root, 'bad.ts'), path.join(root, 'guide.ts')], root);
console.log(JSON.stringify({
  loaded: result.extensions.map(extension => path.basename(extension.path, '.ts')),
  errors: result.errors.map(error => ({module: path.basename(error.path, '.ts'), error: error.error})),
  partialCommandPresent: result.extensions.some(extension => extension.commands.has('partial_command')),
  partialToolPresent: result.extensions.some(extension => extension.tools.has('partial_tool'))
}));
