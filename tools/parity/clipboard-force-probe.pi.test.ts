import { writeFileSync } from 'node:fs';
import { it, vi } from 'vitest';
import { copyToClipboard } from '../src/utils/clipboard.ts';

const mocks = vi.hoisted(() => ({ platform: vi.fn(), command: vi.fn(), clipboard: { setText: vi.fn() } }));
vi.mock('@earendil-works/pi-tui', () => ({ getNativeClipboard: () => mocks.clipboard }));
vi.mock('../src/utils/clipboard-command.ts', () => ({ runClipboardCommand: mocks.command }));
vi.mock('node:os', async () => ({ ...(await vi.importActual('node:os')), platform: mocks.platform }));

it('captures force-OSC52 copies after native writes', async () => {
  const results = [];
  const original = process.stdout.write.bind(process.stdout);
  try {
    for (const scenario of JSON.parse(process.env.PISHARP_CLIPBOARD_SCENARIOS!)) {
      for (const name of ['SSH_CONNECTION', 'SSH_CLIENT', 'MOSH_CONNECTION', 'WAYLAND_DISPLAY',
                         'DISPLAY', 'TERMUX_VERSION', 'WSL_DISTRO_NAME', 'WSLENV', 'WT_SESSION']) vi.stubEnv(name, '');
      vi.stubEnv('PI_CLIPBOARD_OSC52', scenario.force);
      if (scenario.platform === 'linux') vi.stubEnv('DISPLAY', ':99');
      mocks.platform.mockReturnValue(scenario.platform);
      const order: string[] = [];
      mocks.clipboard.setText.mockImplementation(async () => { order.push('native'); });
      mocks.command.mockImplementation(async () => { order.push('native'); return Buffer.alloc(0); });
      process.stdout.write = ((chunk: string) => {
        if (typeof chunk === 'string' && chunk.startsWith('\x1b]52;c;')) { order.push(chunk); return true; }
        return original(chunk);
      }) as typeof process.stdout.write;
      await copyToClipboard('hello 😀');
      results.push({ platform: scenario.platform, force: scenario.force, order });
    }
  } finally { process.stdout.write = original; vi.unstubAllEnvs(); }
  writeFileSync(process.env.PISHARP_CLIPBOARD_OUTPUT!, JSON.stringify(results));
});
