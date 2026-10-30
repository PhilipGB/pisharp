#!/usr/bin/env python3
"""Copy frozen inputs and hash all raw outputs into a source-bound receipt."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

import acceptance

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent


def create_manifest(bundle, source_sha, ci_run_id, pr_head_sha=''):
    bundle = Path(bundle).resolve()
    bundle.mkdir(parents=True, exist_ok=True)
    indexes = {}
    index_names = ('cases.json', 'process_cases.json', 'theme_cases.json', 'selection_cases.json',
                   'path_cases.json', 'file_url_error_cases.json')
    for name in index_names:
        source = HERE / name
        target = bundle / 'contracts' / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        indexes[name] = hashlib.sha256(source.read_bytes()).hexdigest()
    files = []
    for path in sorted(item for item in bundle.rglob('*') if item.is_file() and item.name != 'manifest.json'):
        files.append(dict(path=path.relative_to(bundle).as_posix(), sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    report_inventory = [path for path in acceptance.expected_report_inventory() if (bundle / path).is_file()]
    manifest = dict(sourceSha=source_sha, piSha=(ROOT / 'tools/parity/pi-reference.txt').read_text().strip(),
                    baseSha='480f058a95713fbc51b6904d4450da68bcda886c',
                    prHeadSha=pr_head_sha, ciRunId=str(ci_run_id), caseIndexSha256=indexes,
                    reportInventory=report_inventory, files=files, requiredFiles=len(files))
    (bundle / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--ci-run-id', required=True)
    parser.add_argument('--pr-head-sha', default='')
    args = parser.parse_args()
    create_manifest(args.root, args.source_sha, args.ci_run_id, args.pr_head_sha)


if __name__ == '__main__':
    main()
