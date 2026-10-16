import { verifyPiCheckout } from './pi-reference.mjs';
import { pathToFileURL } from 'node:url';
import { createRequire, syncBuiltinESMExports } from 'node:module';
import { stripVTControlCharacters } from 'node:util';
import * as fs from 'node:fs';
import path from 'node:path';

const pi = process.argv[2];
verifyPiCheckout(pi);
const load = file => import(pathToFileURL(path.join(pi, file)).href);
const { shareSession, exportSessionForShare } = await load('packages/coding-agent/src/modes/interactive/session-share.ts');
const { SessionManager } = await load('packages/coding-agent/src/core/session-manager.ts');
const { SettingsManager } = await load('packages/coding-agent/src/core/settings-manager.ts');
const { exportSessionToHtml } = await load('packages/coding-agent/src/core/export-html/index.ts');
const { initTheme } = await load('packages/coding-agent/src/modes/interactive/theme/theme.ts');
initTheme('dark');
const inputs = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
const work = process.argv[4];
const require = createRequire(import.meta.url);
const mutableFs = require('node:fs');
const originalTemp = mutableFs.mkdtempSync;
const originalFetch = globalThis.fetch;
const originalViewer = process.env.PI_SHARE_VIEWER_URL;
const originalFixture = process.env.PISHARP_SHARE_FIXTURE_ROOT;
const originalPath = process.env.PATH;
const normalize = jsonl => jsonl.trim().split('\n').map(line => {
  const record = JSON.parse(line);
  if (record.type === 'session' || record.customType === 'pi.share') record.timestamp = '<export-time>';
  if (record.customType === 'pi.share') record.id = '<share-id>';
  return record;
});
const flows = [];
try {
  for (const scenario of inputs.flows) {
    const root = path.join(work, 'pi', scenario.id);
    fs.mkdirSync(root, { recursive: true });
    fs.writeFileSync(path.join(root, 'scenario.json'), JSON.stringify(scenario));
    process.env.PISHARP_SHARE_FIXTURE_ROOT = root;
    process.env.PATH = scenario.missingGithub ? root : originalPath;
    process.env.PI_SHARE_VIEWER_URL = scenario.viewer ?? 'https://pi.dev/session/';
    const sessionFile = path.join(root, 'session.jsonl');
    fs.writeFileSync(sessionFile, inputs.sessionJsonl);
    const manager = SessionManager.open(sessionFile, root);
    let jsonlExports = 0, htmlExports = 0, credentialReads = 0;
    const statuses = [], errors = [], progress = [], requests = [], tempDirs = [];
    const editor = {};
    let focused = editor;
    let cancelTimer;
    mutableFs.mkdtempSync = (prefix, ...rest) => {
      const directory = originalTemp(prefix, ...rest);
      if (String(prefix).includes('pi-share-')) tempDirs.push(directory);
      return directory;
    };
    syncBuiltinESMExports();
    const getBranch = manager.getBranch.bind(manager);
    manager.getBranch = () => {
      jsonlExports++;
      if (scenario.exportError) throw new Error(scenario.exportError);
      return getBranch();
    };
    globalThis.fetch = async (url, options) => {
      const body = Buffer.from(options.body).toString('utf8');
      requests.push({ path: new URL(url).pathname + new URL(url).search, method: options.method,
        authorization: options.headers.Authorization, contentType: options.headers['Content-Type'],
        correctLength: Number(options.headers['Content-Length']) === Buffer.byteLength(body), records: normalize(body) });
      if (scenario.cancel === 'radius') {
        focused.handleInput('\u001b');
        throw Object.assign(new Error('aborted'), { name: 'AbortError' });
      }
      if (scenario.networkError) throw new Error(scenario.networkError);
      const status = scenario.status ?? 200;
      return new Response(scenario.body ?? '{"artifact":{"canonical_url":"https://radius.example/shared"}}',
        { status, statusText: status === 403 ? 'Forbidden' : 'OK', headers: { 'Content-Type': 'application/json' } });
    };
    let thrown = null;
    try {
      await shareSession({
        session: {
          sessionManager: manager, state: inputs.presentation,
          settingsManager: { getSessionShareDestination: () => scenario.destination },
          modelRuntime: {
            getProvider: () => {
              credentialReads++;
              if (scenario.credentialError) throw new Error(scenario.credentialError);
              return scenario.token ? { id: 'radius' } : undefined;
            },
            getAuth: async (_provider, options) => {
              if (options.minOAuthValidityMs !== 300000) throw new Error('wrong OAuth validity window');
              return { auth: { apiKey: scenario.token } };
            },
          },
          exportToHtml: async file => {
            htmlExports++;
            if (scenario.htmlError) throw new Error(scenario.htmlError);
            fs.writeFileSync(file, 'fixture html');
          },
        },
        ui: {
          requestRender() {},
          setFocus(component) {
            focused = component;
            if (component === editor) return;
            const rendered = stripVTControlCharacters(component.render(100).join('\n'));
            const label = ['Uploading to Radius...', 'Creating gist...'].find(value => rendered.includes(value));
            if (label) progress.push(label);
            if (scenario.cancel === 'gist') {
              cancelTimer = setInterval(() => {
                if (fs.existsSync(path.join(root, 'gist-pid'))) {
                  clearInterval(cancelTimer);
                  component.handleInput('\u001b');
                }
              }, 5);
            }
          },
        },
        editorContainer: { clear() {}, addChild() {} }, editor,
        showStatus: message => statuses.push(stripVTControlCharacters(message)),
        showError: message => errors.push(stripVTControlCharacters(message)),
      });
    } catch (error) { thrown = error.message; }
    finally { if (cancelTimer) clearInterval(cancelTimer); }
    if (statuses.length + errors.length > 1) throw new Error('multiple terminal share results');
    const commandFile = path.join(root, 'commands.jsonl');
    const commands = fs.existsSync(commandFile) ? fs.readFileSync(commandFile, 'utf8').trim().split('\n').map(line => {
      const command = JSON.parse(line);
      if (command[0] === 'gist') command[command.length - 1] = '<export>/session.html';
      return command;
    }) : [];
    let cancelledChildExited = true;
    if (scenario.cancel === 'gist') {
      cancelledChildExited = false;
      if (fs.existsSync(path.join(root, 'gist-pid'))) {
        const pid = Number(fs.readFileSync(path.join(root, 'gist-pid'), 'utf8'));
        try { process.kill(pid, 0); } catch (error) { if (error.code === 'ESRCH') cancelledChildExited = true; else throw error; }
      }
    }
    flows.push({ id: scenario.id, message: errors[0] ?? statuses[0] ?? null, isError: errors.length > 0,
      thrown, credentialReads, jsonlExports, htmlExports, progress, requests, commands,
      cleaned: tempDirs.length === 1 && tempDirs.every(directory => !fs.existsSync(directory)), cancelledChildExited });
  }
  const branches = [];
  for (const head of inputs.heads) {
    const manager = SessionManager.open(path.join(work, 'branch.jsonl'), work);
    if (head === null) manager.resetLeaf(); else manager.branch(head);
    const target = path.join(work, 'branch-export.jsonl');
    exportSessionForShare(target, { sessionManager: manager, state: inputs.presentation });
    const htmlTarget = path.join(work, 'pi-viewer-' + (head ?? 'empty') + '.html');
    await exportSessionToHtml(manager, inputs.presentation, { outputPath: htmlTarget, themeName: 'dark' });
    const html = fs.readFileSync(htmlTarget, 'utf8');
    const encoded = html.match(/<script id="session-data" type="application\/json">([^<]+)<\/script>/)[1];
    branches.push({ head, records: normalize(fs.readFileSync(target, 'utf8')),
      viewerData: JSON.parse(Buffer.from(encoded, 'base64').toString('utf8')) });
  }
  const settings = [];
  for (const vector of inputs.settings) {
    const root = path.join(work, 'pi-settings', vector.id);
    fs.mkdirSync(path.join(root, 'agent'), { recursive: true });
    fs.mkdirSync(path.join(root, '.pi'), { recursive: true });
    fs.writeFileSync(path.join(root, 'agent/settings.json'), JSON.stringify(vector.user));
    fs.writeFileSync(path.join(root, '.pi/settings.json'), JSON.stringify(vector.project));
    const manager = SettingsManager.create(root, path.join(root, 'agent'));
    const initial = manager.getSessionShareDestination();
    manager.setSessionShareDestination('radius');
    await manager.flush();
    settings.push({ id: vector.id, initial, afterSave: manager.getSessionShareDestination(),
      user: JSON.parse(fs.readFileSync(path.join(root, 'agent/settings.json'), 'utf8')) });
  }
  console.log(JSON.stringify({ flows, branches, settings }));
} finally {
  mutableFs.mkdtempSync = originalTemp;
  syncBuiltinESMExports();
  globalThis.fetch = originalFetch;
  for (const [key, value] of [['PI_SHARE_VIEWER_URL', originalViewer], ['PISHARP_SHARE_FIXTURE_ROOT', originalFixture], ['PATH', originalPath]]) {
    if (value === undefined) delete process.env[key]; else process.env[key] = value;
  }
}
