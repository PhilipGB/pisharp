#!/usr/bin/env python3
"""Acquire and verify the repository's single current Pi parity reference."""
import argparse
from pathlib import Path
import re
import subprocess
import sys

PIN_PATH = Path(__file__).with_name('pi-reference.txt')
PI_REPOSITORY = 'https://github.com/earendil-works/pi.git'


class PiReferenceError(RuntimeError):
    """A checkout cannot supply the configured parity reference."""


def read_pi_reference():
    try:
        sha = PIN_PATH.read_text().strip()
    except OSError as error:
        raise PiReferenceError(f'Cannot read Pi reference {PIN_PATH}: {error}') from error
    if not re.fullmatch('[0-9a-f]{40}', sha):
        raise PiReferenceError(f'Pi reference {PIN_PATH} must contain one full lowercase commit SHA.')
    return sha


def git_output(checkout, *arguments):
    try:
        result = subprocess.run(['git', '-C', str(checkout), *arguments],
                                check=True, capture_output=True, text=True)
    except (OSError, subprocess.CalledProcessError) as error:
        detail = error.stderr.strip() if isinstance(error, subprocess.CalledProcessError) else str(error)
        raise PiReferenceError(f'Cannot inspect Pi checkout {checkout}: {detail}') from error
    return result.stdout.strip()


def verify_pi_checkout(checkout):
    checkout = Path(checkout).resolve()
    expected = read_pi_reference()
    actual = git_output(checkout, 'rev-parse', 'HEAD')
    if actual != expected:
        raise PiReferenceError(f'Pi checkout SHA mismatch: expected {expected}, found {actual} at {checkout}. '
                               'Prepare the configured reference; do not move the pin to make verification pass.')
    root = Path(git_output(checkout, 'rev-parse', '--show-toplevel')).resolve()
    if root != checkout:
        raise PiReferenceError(f'Pi checkout must be its repository root: use {root}, not {checkout}.')
    result = subprocess.run(['git', '-C', str(checkout), 'diff', '--quiet', 'HEAD', '--'],
                            capture_output=True, text=True)
    if result.returncode != 0:
        raise PiReferenceError(f'Pi checkout has tracked changes or cannot be inspected at {checkout}. '
                               'Parity requires unchanged source at the configured SHA.')
    return actual


def obtain_pi_checkout(destination):
    destination = Path(destination).resolve()
    if destination.exists():
        raise PiReferenceError(f'Destination already exists: {destination}. Use verify for an existing checkout.')
    expected = read_pi_reference()
    try:
        subprocess.run(['git', 'clone', '--filter=blob:none', '--no-checkout', PI_REPOSITORY, str(destination)], check=True)
        subprocess.run(['git', '-C', str(destination), 'fetch', 'origin', expected], check=True)
        subprocess.run(['git', '-C', str(destination), 'checkout', '--detach', expected], check=True)
    except (OSError, subprocess.CalledProcessError) as error:
        raise PiReferenceError(f'Cannot obtain frozen Pi {expected}: {error}') from error
    return verify_pi_checkout(destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    verify = commands.add_parser('verify', help='Reject mismatched or tracked-dirty Pi source')
    verify.add_argument('--pi', type=Path, required=True)
    checkout = commands.add_parser('checkout', help='Obtain a new checkout at the configured commit')
    checkout.add_argument('--destination', type=Path, required=True)
    args = parser.parse_args()
    try:
        sha = verify_pi_checkout(args.pi) if args.command == 'verify' else obtain_pi_checkout(args.destination)
    except PiReferenceError as error:
        print(f'Pi reference error: {error}', file=sys.stderr)
        return 2
    print(sha)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
