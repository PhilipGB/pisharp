#!/usr/bin/env node
/** Run the pinned official MCP client suite against PiSharp's production McpRuntime. */
import { spawn } from 'node:child_process';
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';

const CONFORMANCE_PACKAGE = '@modelcontextprotocol/conformance@0.2.0-alpha.11';
const MODES = ['2025-03-26', '2025-06-18', '2025-11-25'];
const PI_ORACLE_COMMIT = '4270522473e677ddba7b093caa0064a3df51d459';
const SCENARIO_TIMEOUT_MS = 30_000;
const IGNORED_STATUSES = new Set(['INFO', 'SKIPPED']);
const ALLOWED_FAILURE_CLASSES = new Set([
  'in-scope-gap',
  'sdk-limitation',
  'intentional-scope-difference',
  'unsupported-future-protocol',
]);

const root = fileURLToPath(new URL('../../..', import.meta.url));
const here = fileURLToPath(new URL('.', import.meta.url));
const baselinePath = join(here, 'baseline.json');
const classificationsPath = join(here, 'failure-classifications.json');
const clientPath = join(root, 'tools/McpConformance/bin/Debug/net10.0/PiSharp.McpConformance.dll');

function runCommand(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env ?? process.env,
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (data) => { stdout += data.toString(); });
    child.stderr.on('data', (data) => { stderr += data.toString(); });
    const timer = options.timeoutMs === undefined ? undefined : setTimeout(() => {
      if (child.pid !== undefined) {
        try { process.kill(-child.pid, 'SIGKILL'); } catch {}
      }
    }, options.timeoutMs);
    child.on('error', reject);
    child.on('close', (code, signal) => {
      if (timer) clearTimeout(timer);
      resolve({ code: code ?? (signal ? 128 : 1), stdout, stderr });
    });
  });
}

function conformanceEnvironment() {
  return {
    ...process.env,
    npm_config_cache: process.env.npm_config_cache ?? join(tmpdir(), 'pisharp-mcp-conformance-npm-cache'),
    npm_config_ignore_scripts: 'true',
    npm_config_yes: 'true',
    DOTNET_PROCESSOR_COUNT: '2',
    UV_THREADPOOL_SIZE: '2',
    OPENBLAS_NUM_THREADS: '2',
    OMP_NUM_THREADS: '2',
    MKL_NUM_THREADS: '2',
  };
}

async function listScenarios(mode, workDir) {
  const result = await runCommand('npx', [
    '--yes', CONFORMANCE_PACKAGE, 'list', '--client', '--spec-version', mode,
  ], { cwd: workDir, env: conformanceEnvironment() });
  if (result.code !== 0)
    throw new Error(`Listing ${mode} scenarios failed:\n${result.stderr || result.stdout}`);
  return [...result.stdout.matchAll(/^\s+- (\S+)/gm)].map((match) => match[1]);
}

function findFile(dir, name) {
  if (!existsSync(dir)) return undefined;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isFile() && entry.name === name) return path;
    if (entry.isDirectory()) {
      const found = findFile(path, name);
      if (found) return found;
    }
  }
  return undefined;
}

function record(result, id, status, message) {
  if (result.checks.get(id) !== 'fail') result.checks.set(id, status);
  if (status === 'fail' && message && !result.messages.has(id)) result.messages.set(id, message);
}

async function runScenario(mode, scenario, launcher, workDir, verbose) {
  const scenarioDir = join(workDir, mode, scenario.replace(/[^A-Za-z0-9_.-]+/g, '-'));
  mkdirSync(scenarioDir, { recursive: true });
  const reportPath = join(scenarioDir, 'pisharp-client.json');
  const result = { mode, scenario, checks: new Map(), messages: new Map() };
  const run = await runCommand('npx', [
    '--yes', CONFORMANCE_PACKAGE,
    'client',
    '--command', launcher,
    '--scenario', scenario,
    '--spec-version', mode,
    '--timeout', String(SCENARIO_TIMEOUT_MS),
    '--output-dir', scenarioDir,
  ], {
    cwd: workDir,
    env: { ...conformanceEnvironment(), PI_MCP_CONFORMANCE_REPORT: reportPath },
    timeoutMs: SCENARIO_TIMEOUT_MS + 30_000,
  });
  if (verbose) process.stderr.write(run.stderr);

  const checksPath = findFile(scenarioDir, 'checks.json');
  if (!checksPath) {
    record(result, 'runner', 'fail', `No checks.json (runner exit ${run.code}): ${run.stderr.trim().slice(-2000)}`);
    return result;
  }
  const checks = JSON.parse(readFileSync(checksPath, 'utf8'));
  for (const check of checks) {
    const status = String(check.status ?? 'FAILURE').toUpperCase();
    if (IGNORED_STATUSES.has(status)) continue;
    const id = String(check.id ?? check.name ?? 'unnamed');
    const message = String(check.errorMessage ?? check.description ?? '');
    record(result, id, status === 'SUCCESS' ? 'pass' : 'fail', message);
  }

  let client = { error: 'PiSharp client wrote no report' };
  if (existsSync(reportPath)) client = JSON.parse(readFileSync(reportPath, 'utf8'));
  record(result, 'pisharp-client', client.success === true ? 'pass' : 'fail', String(client.error ?? ''));
  return result;
}

function toBaseline(results) {
  const baseline = {
    conformance: CONFORMANCE_PACKAGE,
    oracle: {
      repository: 'https://github.com/earendil-works/pi',
      commit: PI_ORACLE_COMMIT,
      verifiedAt: '2026-10-12',
    },
    modes: {},
  };
  for (const result of results) {
    const ids = [...result.checks.keys()].sort();
    baseline.modes[result.mode] ??= {};
    baseline.modes[result.mode][result.scenario] = {
      passing: ids.filter((id) => result.checks.get(id) === 'pass'),
      failing: ids.filter((id) => result.checks.get(id) === 'fail'),
    };
  }
  return baseline;
}

function failureKey(mode, scenario, id) {
  return `${mode}|${scenario}|${id}`;
}

function evaluate(results, baseline, classifications) {
  const regressions = [];
  const fixed = [];
  for (const result of results) {
    const expected = baseline.modes[result.mode]?.[result.scenario];
    const label = `${result.mode} ${result.scenario}`;
    if (!expected) {
      regressions.push(`${label}: scenario is not in the baseline`);
      continue;
    }
    for (const [id, status] of result.checks) {
      const message = result.messages.get(id);
      const wasPassing = expected.passing.includes(id);
      const wasFailing = expected.failing.includes(id);
      if (!wasPassing && !wasFailing) {
        regressions.push(`${label}: ${id} is an unbaselined ${status}${message ? `: ${message}` : ''}`);
      } else if (status === 'fail' && wasPassing) {
        regressions.push(`${label}: ${id} regressed${message ? `: ${message}` : ''}`);
      } else if (status === 'pass' && wasFailing) {
        fixed.push(`${label}: ${id}`);
      }
    }
    for (const id of expected.passing) {
      if (!result.checks.has(id)) regressions.push(`${label}: passing check ${id} was not reported`);
    }
    for (const id of expected.failing) {
      if (!result.checks.has(id)) regressions.push(`${label}: known failing check ${id} was not reported; review the baseline`);
      const classification = classifications.failures[failureKey(result.mode, result.scenario, id)];
      if (!classification || !ALLOWED_FAILURE_CLASSES.has(classification.classification) ||
          typeof classification.reason !== 'string' || classification.reason.trim().length === 0)
        regressions.push(`${label}: known failure ${id} has no valid classification`);
    }
  }
  for (const [mode, scenarios] of Object.entries(baseline.modes)) {
    for (const scenario of Object.keys(scenarios)) {
      if (!results.some((result) => result.mode === mode && result.scenario === scenario))
        regressions.push(`${mode} ${scenario}: scenario did not run`);
    }
  }
  if (fixed.length > 0)
    console.log(`\nFixed since the baseline (review and update it):\n${fixed.map((line) => `  ${line}`).join('\n')}`);
  if (regressions.length > 0)
    console.log(`\nRegressions or unclassified results:\n${regressions.map((line) => `  ${line}`).join('\n')}`);
  else console.log('\nNo regressions against the baseline; all known failures are classified.');
  return regressions.length === 0;
}

function printResults(results) {
  for (const mode of MODES) {
    const modeResults = results.filter((result) => result.mode === mode);
    if (modeResults.length === 0) continue;
    console.log(`\n${mode}`);
    for (const result of modeResults) {
      const statuses = [...result.checks.values()];
      const failed = statuses.filter((status) => status === 'fail').length;
      console.log(`  ${failed === 0 ? 'pass' : 'FAIL'} ${result.scenario} (${statuses.length - failed} passed, ${failed} failed)`);
      for (const [id, status] of result.checks) {
        if (status !== 'fail') continue;
        const message = result.messages.get(id)?.replace(/\s+/g, ' ').slice(0, 300);
        console.log(`         ${id}${message ? `: ${message}` : ''}`);
      }
    }
  }
}

function shellQuote(value) {
  return `'${value.replaceAll("'", "'\\''")}'`;
}

async function main() {
  const { values } = parseArgs({
    options: {
      mode: { type: 'string', multiple: true },
      scenario: { type: 'string', multiple: true },
      'update-baseline': { type: 'boolean', default: false },
      'keep-results': { type: 'boolean', default: false },
      'no-compare': { type: 'boolean', default: false },
      verbose: { type: 'boolean', default: false },
      help: { type: 'boolean', short: 'h', default: false },
    },
  });
  if (values.help) {
    console.log(`Usage: node tools/parity/mcp-conformance/run.mjs [options]

Options:
  --mode <version>     Protocol mode (repeatable): ${MODES.join(', ')}
  --scenario <name>    Run only a named scenario (repeatable)
  --update-baseline    Write results from a full run to baseline.json
  --keep-results       Keep official checks and PiSharp client reports
  --no-compare         Run a focused development subset without baseline checks
  --verbose            Print the conformance runner's client logs`);
    return 0;
  }
  if (!existsSync(clientPath)) {
    console.error(`PiSharp conformance client is missing: ${clientPath}\nBuild PiSharp.slnx first.`);
    return 1;
  }
  const modes = values.mode ?? [...MODES];
  for (const mode of modes) {
    if (!MODES.includes(mode)) {
      console.error(`Unsupported protocol mode ${mode}. Supported: ${MODES.join(', ')}`);
      return 1;
    }
  }
  const partial = values.mode !== undefined || values.scenario !== undefined;
  if (values['update-baseline'] && partial) {
    console.error('--update-baseline requires a full run without --mode or --scenario.');
    return 1;
  }
  if (values['update-baseline'] && values['no-compare']) {
    console.error('--update-baseline cannot be combined with --no-compare.');
    return 1;
  }

  const workDir = mkdtempSync(join(tmpdir(), 'pisharp-mcp-conformance-'));
  try {
    const launcher = join(workDir, 'pisharp-client');
    writeFileSync(launcher,
      `#!/bin/sh\nexec dotnet ${shellQuote(clientPath)} "$@"\n`);
    chmodSync(launcher, 0o755);

    const results = [];
    for (const mode of modes) {
      const listed = await listScenarios(mode, workDir);
      const scenarios = listed.filter((scenario) => !values.scenario || values.scenario.includes(scenario));
      for (const scenario of scenarios) {
        process.stderr.write(`${mode} ${scenario}\n`);
        results.push(await runScenario(mode, scenario, launcher, workDir, values.verbose));
      }
    }
    if (results.length === 0) {
      console.error('No scenarios matched.');
      return 1;
    }
    printResults(results);

    if (values['update-baseline']) {
      writeFileSync(baselinePath, `${JSON.stringify(toBaseline(results), null, 2)}\n`);
      console.log(`\nWrote ${baselinePath}; review failures and update failure-classifications.json.`);
      return 0;
    }
    if (values['no-compare']) return 0;

    if (!existsSync(baselinePath)) {
      console.error(`\nNo baseline.json at ${baselinePath}. Run a full --update-baseline and review it.`);
      return 1;
    }
    if (!existsSync(classificationsPath)) {
      console.error(`\nNo failure-classifications.json at ${classificationsPath}. Classify every known failure.`);
      return 1;
    }
    const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
    const classifications = JSON.parse(readFileSync(classificationsPath, 'utf8'));
    if (baseline.conformance !== CONFORMANCE_PACKAGE || classifications.conformance !== CONFORMANCE_PACKAGE) {
      console.error(`\nBaseline/classifications must pin ${CONFORMANCE_PACKAGE}.`);
      return 1;
    }
    const scopedBaseline = partial ? {
      conformance: baseline.conformance,
      modes: Object.fromEntries(modes.map((mode) => [mode,
        Object.fromEntries(Object.entries(baseline.modes[mode] ?? {})
          .filter(([scenario]) => !values.scenario || values.scenario.includes(scenario))),
      ])),
    } : baseline;
    return evaluate(results, scopedBaseline, classifications) ? 0 : 1;
  } finally {
    if (values['keep-results']) console.log(`\nResults kept in ${workDir}`);
    else rmSync(workDir, { recursive: true, force: true });
  }
}

process.exit(await main());
