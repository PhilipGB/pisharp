#!/usr/bin/env python3
"""Validate repository links against tracked paths, independent of local artifacts."""
import argparse
import fnmatch
from pathlib import Path
import posixpath
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit

LINK = re.compile(r'!?\[[^\]]*\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)')
REFERENCE = re.compile(r'^\s*\[[^\]]+\]:\s*(<[^>]+>|\S+)', re.MULTILINE)
CODE = re.compile(r'`([^`\n]+)`')
ROOT_PATH = re.compile(r'(?<![\w/])(?:src|tests|tools|docs|\.github)/[\w./*?\-]+')
ROOT_FILES = {'AGENTS.md', 'README.md', 'TODO.md', 'PiSharp.slnx'}

def tracked_files(repo):
    return set(subprocess.check_output(['git', 'ls-files', '-z'], cwd=repo).decode().split('\0')) - {''}

def references(text, parity=False):
    for pattern in [LINK, REFERENCE]:
        for match in pattern.finditer(text):
            yield match.start(), match.group(1).strip('<>'), False
    for match in CODE.finditer(text):
        value = match.group(1)
        for path in ROOT_PATH.finditer(value):
            target = path.group()
            # Parity inventories also document upstream Pi paths and synthetic fixture inputs.
            if parity and not target.startswith(('src/PiSharp', 'tests/PiSharp', 'tests/ResourceDiscoveryExtension', 'tools/', 'docs/parity/', 'docs/capabilities/', 'docs/architecture', 'docs/development/')):
                continue
            yield match.start() + path.start(), target, True
        if value in ROOT_FILES:
            yield match.start(), value, True

def validate(repo, tracked=None):
    tracked = tracked_files(repo) if tracked is None else tracked
    documents = sorted(path for path in tracked if path == 'AGENTS.md' or
        path.endswith('.md') and (path == 'docs/architecture.md' or
        path.startswith(('docs/architecture/', 'docs/capabilities/', 'docs/development/', 'docs/parity/'))))
    errors = []
    checked = 0
    for document in documents:
        text = (repo / document).read_text(encoding='utf-8')
        for offset, target, root_relative in references(text, document.startswith('docs/parity/')):
            parts = urlsplit(target)
            if parts.scheme or parts.netloc or not parts.path or parts.path.startswith('/'):
                continue
            path = unquote(parts.path)
            if not root_relative:
                path = posixpath.join(posixpath.dirname(document), path)
            path = posixpath.normpath(path)
            checked += 1
            exists = path in tracked or any(name.startswith(path.rstrip('/') + '/') for name in tracked)
            if any(character in path for character in '*?'):
                exists = any(fnmatch.fnmatchcase(name, path) for name in tracked)
            if not exists:
                errors.append(f'{document}:{text.count(chr(10), 0, offset) + 1}: untracked or missing path: {target}')
    return errors, len(documents), checked

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    repo = parser.parse_args().repo.resolve()
    errors, documents, checked = validate(repo)
    for error in errors:
        print(error, file=sys.stderr)
    if errors:
        return 1
    print(f'Documentation paths verified: {documents} documents, {checked} references')
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
