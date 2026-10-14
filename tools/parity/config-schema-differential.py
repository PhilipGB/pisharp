#!/usr/bin/env python3
"""Verify shipped schema sources are byte-identical to the audited Pi pin."""
import argparse
import hashlib
import json
from pathlib import Path

from pi_reference import verify_pi_checkout

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
pi_sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
results = []
for name in ['models', 'settings', 'keybindings', 'theme']:
    filename = name + '.schema.json'
    source = (args.pi / 'packages/coding-agent/schemas' / filename).read_bytes()
    native = (repo / 'src/PiSharp.Cli/Configuration/Schemas' / filename).read_bytes()
    results.append(dict(name=name, sha256=hashlib.sha256(source).hexdigest(), match=source == native))
assert all(result['match'] for result in results), results
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(dict(piSha=pi_sha, schemas=results, match=True,
    comparison='Exact bytes of the four source artifacts, retaining Pi schema IDs, comments and definitions.',
    limitations='This establishes schema provenance, not full runtime validation equivalence. Publication is covered by native assembly/output checks. Settings unknown-key handling, all model validation and unsupported runtime fields remain ledger work.'), indent=2) + '\n')
print('Matched four pinned Pi schema artifacts byte for byte.')
