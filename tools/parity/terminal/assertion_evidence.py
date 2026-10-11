"""Load the compact terminal evidence used by behavioral assertion scripts."""
import gzip
import json
from pathlib import Path


def load_assertion_evidence(report_directory, case):
    artifact = case.get('assertionEvidence')
    if not artifact:
        raise RuntimeError('This report predates compact assertion evidence; rerun the terminal differential first.')

    path = Path(report_directory) / artifact
    if not path.is_file():
        raise RuntimeError(f'Compact assertion evidence is missing: {path}; rerun the terminal differential first.')
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        return json.load(stream)
