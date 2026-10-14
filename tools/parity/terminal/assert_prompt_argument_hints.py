"""Verify hint, empty-hint and multiline completion plus provider expansion checkpoints."""
import argparse
import json
from pathlib import Path

from assertion_evidence import load_assertion_evidence
from assert_resource_skill_precedence import chat_request, frame_text, user_message
from thread_limits import apply_test_thread_limit

apply_test_thread_limit()


def verify(paired_directory, calibration_directory):
    paired_directory = Path(paired_directory)
    calibration = json.loads((Path(calibration_directory) / 'report.json').read_text())
    paired = json.loads((paired_directory / 'report.json').read_text())
    assert calibration['match'], 'Pi calibration differs'
    assert paired['piSha'] == calibration['piSha'], 'Source pins differ'
    assert len(paired['cases']) == 1
    case = paired['cases'][0]
    products = load_assertion_evidence(paired_directory, case)
    for name in ['pi', 'pisharp']:
        product = products[name]
        assert not product.get('scenarioError'), product.get('scenarioError')
        assert '→ guide       <subject> — [t] Guide' in frame_text(product, 'hint-suggestion')
        assert '→ plain       [t] Plain' in frame_text(product, 'empty-hint-suggestion')
        assert '→ multi       <subject> [detail] — [t] Guide' in frame_text(product, 'multiline-hint-suggestion')
        assert user_message(chat_request(product)) == 'Review subject'
    return dict(behaviorMatch=True, calibrationMatch=True, terminalMatch=case['terminalMatch'],
                renderDifferences=case['renderDifferences'], httpMatch=case['httpMatch'],
                scenarioErrors=case['scenarioErrors'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--paired', type=Path, required=True)
    parser.add_argument('--calibration', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.paired, args.calibration), indent=2))
