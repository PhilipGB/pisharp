import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifyPiCheckout } from './pi-reference.mjs';

const [pi, input] = process.argv.slice(2);
verifyPiCheckout(pi);
const { adjustMaxTokensForThinking } = await import(pathToFileURL(
  path.join(pi, 'packages/ai/src/api/simple-options.ts')).href);
const answerRoom = 1024;
const results = JSON.parse(readFileSync(input, 'utf8')).map(item => {
  const baseMaxTokens = item.baseMaxTokens === null ? undefined : item.baseMaxTokens;
  const adjusted = adjustMaxTokensForThinking(baseMaxTokens, item.modelMaxTokens, item.level, item.budgets);
  return {
    id: item.id,
    maxTokens: adjusted.maxTokens,
    thinkingBudget: Math.min(adjusted.thinkingBudget, Math.max(0, adjusted.maxTokens - answerRoom)),
  };
});
console.log(JSON.stringify(results));
