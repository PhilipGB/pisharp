/** The Python verifier owns pin parsing and checkout validation for every driver. */
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export function verifyPiCheckout(checkout) {
  try {
    execFileSync('python3', [fileURLToPath(new URL('./pi_reference.py', import.meta.url)),
      'verify', '--pi', checkout], { encoding: 'utf8', stdio: 'pipe' });
  } catch (error) {
    throw new Error(error.stderr?.toString().trim() || error.message);
  }
}
