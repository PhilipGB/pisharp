#!/usr/bin/env python3
"""Copy frozen inputs and hash all raw outputs into a source-bound receipt."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--ci-run-id', required=True)
    parser.add_argument('--pr-head-sha', default='')
    args = parser.parse_args()
    bundle = args.root.resolve()
    bundle.mkdir(parents=True, exist_ok=True)
    indexes = {}
    for name in ('cases.json', 'process_cases.json', 'theme_cases.json', 'selection_cases.json', 'path_cases.json'):
        source = HERE / name
        target = bundle / 'contracts' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        indexes[name] = hashlib.sha256(source.read_bytes()).hexdigest()
    files = []
    for path in sorted(item for item in bundle.rglob('*') if item.is_file() and item.name != 'manifest.json'):
        files.append(dict(path=path.relative_to(bundle).as_posix(), sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    manifest = dict(sourceSha=args.source_sha, piSha=(ROOT / 'tools/parity/pi-reference.txt').read_text().strip(),
                    baseSha='480f058a95713fbc51b6904d4450da68bcda886c',
                    prHeadSha=args.pr_head_sha, ciRunId=str(args.ci_run_id), caseIndexSha256=indexes,
                    files=files, requiredFiles=len(files))
    (bundle / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')


if __name__ == '__main__':
    main()
