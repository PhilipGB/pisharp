"""Verify malformed skill recovery, error presentation and the original provider input."""
import argparse
import json
from pathlib import Path

from assertion_evidence import load_assertion_evidence
from assert_resource_skill_precedence import chat_request, frame_text, user_message
from thread_limits import apply_test_thread_limit

apply_test_thread_limit()


def verify(paired_directory, calibration_directory):
    paired_directory = Path(paired_directory)
    paired = json.loads((paired_directory / 'report.json').read_text())
    calibration = json.loads((Path(calibration_directory) / 'report.json').read_text())
    assert calibration['match'], 'Pi calibration differs'
    assert paired['piSha'] == calibration['piSha'], 'Source pins differ'
    assert paired['match'], 'Complete terminal/render/provider comparison differs'
    assert len(paired['cases']) == 1
    case = paired['cases'][0]
    products = load_assertion_evidence(paired_directory, case)
    for name in ['pi', 'pisharp']:
        product = products[name]
        assert not product.get('scenarioError'), product.get('scenarioError')
        error = frame_text(product, 'skill-error-active')
        assert 'Extension "' in error
        assert '/guide/SKILL.md" error:' in error
        assert 'sufficiently indented' in error
        assert 'description: [broken' in error
        assert error.index('description: [broken') < error.index('/skill:guide args')
        assert 'ERROR_RECOVERED_REPLY' in frame_text(product, 'reply-active')
        idle = frame_text(product, 'reply-idle')
        assert 'ERROR_RECOVERED_REPLY' in idle
        assert '↑10 ↓3' in idle
        assert 'Working' not in idle
        assert user_message(chat_request(product)) == '/skill:guide args'
    return dict(behaviorMatch=True, calibrationMatch=True, terminalMatch=case['terminalMatch'],
                renderDifferences=case['renderDifferences'], httpMatch=case['httpMatch'],
                scenarioErrors=case['scenarioErrors'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--paired', type=Path, required=True)
    parser.add_argument('--calibration', type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(verify(args.paired, args.calibration), indent=2))
