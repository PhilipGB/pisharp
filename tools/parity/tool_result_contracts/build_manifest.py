#!/usr/bin/env python3
"""Build the committed tool-result-contracts acceptance manifest from matrix evidence."""
import argparse
import json
from pathlib import Path

CRITERIA = [
    ('C1', 'Expose schema-backed machine-readable results separately from model-facing text/images and UI details'),
    ('C2', 'Preserve explicit tool error state, usage, progress updates and termination through provider/session/export paths'),
    ('C3', 'Keep thrown exceptions and returned error results behaviorally distinct where Pi does'),
]


def state(call_id, is_error=False, terminate=False):
    return {'callId': call_id, 'isError': is_error, 'terminate': terminate}


def simple(case, criteria, test, requests, executions, states, differential):
    return dict(id=case, criteria=criteria, test=test,
                expected={'requests': requests,
                          'executions': {name: count for name, count in executions.items() if count},
                          'resultStates': states},
                differential=differential)


def termination(case, flags, requests):
    states = [state(f'call-{index}', case == 'T09' and index == 1, flag)
              for index, flag in enumerate(flags)]
    return simple(case, ['C2'], 'ToolResultContractMatrixTests.TerminationPermutations',
                  requests, {f'tool{index}': 1 for index in range(len(flags))}, states,
                  {'status': 'match', 'case': case})


CASES = [
    termination('T01', [False], 2),
    termination('T02', [True], 1),
    termination('T03', [False, False], 2),
    termination('T04', [True, False], 2),
    termination('T05', [False, True], 2),
    termination('T06', [True, True], 1),
    termination('T07', [True, True, False], 2),
    termination('T08', [True, True, True], 1),
    termination('T09', [True, True], 1),
    simple('T10', ['C2'],
           'ToolResultContractMatrixTests.ParallelToolsCompleteInReverseOrderWithoutChangingTerminationOrResultOrder',
           1, {'tool0': 1, 'tool1': 1}, [state('call-0', False, True), state('call-1', False, True)],
           {'status': 'match', 'case': 'T10'}),
    simple('T11', ['C2'], 'ToolResultContractMatrixTests.SequentialToolsBothExecuteBeforeTheMixedBatchContinues',
           2, {'tool0': 1, 'tool1': 1}, [state('call-0', False, True), state('call-1')],
           {'status': 'match', 'case': 'T11'}),
    simple('T12', ['C2'], 'ToolResultContractMatrixTests.ParallelMixedBatchRunsEveryToolBeforeContinuing',
           2, {'tool0': 1, 'tool1': 1}, [state('call-0', False, True), state('call-1')],
           {'status': 'match', 'case': 'T12'}),
    simple('E01', ['C2'], 'ToolResultContractMatrixTests.SuccessfulResultContinuesWithFalseErrorState',
           2, {'tool0': 1}, [state('call-0')],
           {'status': 'match', 'case': 'T01', 'note': 'Successful continuation is the T01 path.'}),
    simple('E02', ['C2', 'C3'], 'ToolResultContractMatrixTests.ReturnedErrorIsNotAThrownException',
           2, {'tool0': 1}, [state('call-0', True)],
           {'status': 'match', 'case': 'E02'}),
    simple('E03', ['C2', 'C3'], 'ToolResultContractMatrixTests.ThrownExceptionRemainsDistinctFromReturnedError',
           2, {'tool0': 1}, [state('call-0', True)],
           {'status': 'match', 'case': 'E03',
            'note': 'Canonical text/error match; M.E.AI supplies generic provider-only text for thrown exceptions.'}),
    simple('E04', ['C2'],
           'ToolResultContractMatrixTests.UnknownInvalidAndBlockedInvocationsProduceErrorsWithoutRunningHandlers',
           2, {}, [state('call-unknown', True), state('call-invalid', True), state('call-blocked', True)],
           {'status': 'match', 'case': 'E04'}),
    simple('E05', ['C1', 'C2'], 'ToolResultContractMatrixTests.OutputSchemaViolationIsAVisibleErrorResult',
           2, {'tool0': 1}, [state('call-0', True)],
           {'status': 'intentional-difference', 'case': 'E05',
            'reason': 'Pinned Pi treats outputSchema as declarative; PiSharp validates it and returns a visible error.'}),
    simple('E06', ['C2', 'C3'], 'ToolResultContractMatrixTests.FailedAndSuccessfulSiblingBothContinueBecauseFailureDoesNotTerminate',
           2, {'tool0': 1, 'tool1': 1}, [state('call-0', True), state('call-1')],
           {'status': 'match', 'case': 'E06'}),
    simple('E07', ['C2'], 'ToolResultContractMatrixTests.ErrorResultTerminationStillDependsOnTheWholeBatch',
           2, {'tool0': 1, 'tool1': 1}, [state('call-0', True, True), state('call-1')],
           {'status': 'match', 'case': 'E07'}),
    simple('R01', ['C1'], 'ToolResultContractMatrixTests.TextStructuredAndUiDetailsStayDistinct',
           2, {'tool0': 1}, [state('call-0')], {'status': 'match', 'case': 'R01'}),
    simple('R02', ['C1'], 'ToolResultContractMatrixTests.ValidStructuredOutputPassesTheDeclaredSchemaWithoutLosingData',
           2, {'tool0': 1}, [state('call-0')], {'status': 'match', 'case': 'R02'}),
    simple('R03', ['C1'], 'ToolResultContractMatrixTests.TextAndImageContentReachTheModelAndTheCanonicalResult',
           2, {'tool0': 1}, [state('call-0')], {'status': 'match', 'case': 'R03'}),
    simple('R04', ['C2'], 'ToolResultContractMatrixTests.ProgressUpdatesAreOrderedAndFinalUsageIsRetained',
           2, {'tool0': 1}, [state('call-0')], {'status': 'match', 'case': 'R04'}),
    simple('R05', ['C1', 'C2'], 'ToolResultContractMatrixTests.NestedStructuredResultAndErrorMetadataReachTheParent',
           2, {'parent': 1, 'child': 1}, [state('call-0')],
           {'status': 'not-applicable',
            'reason': 'Pinned Pi owns nested execution in its coding-agent session; the agent-loop oracle does not expose parent association.'}),
    simple('H01', ['C2'], 'ToolResultContractMatrixTests.ResultHookCanRequestTermination',
           1, {'tool0': 1}, [state('call-0', False, True)], {'status': 'match', 'case': 'H01'}),
    simple('H02', ['C2'], 'ToolResultContractMatrixTests.ResultHookCanCancelTerminationAndContinue',
           2, {'tool0': 1}, [state('call-0')], {'status': 'match', 'case': 'H02'}),
    simple('H03', ['C2'], 'ToolResultContractMatrixTests.ResultHookTextAndErrorAreObservedByEventsProviderAndHistory',
           2, {'tool0': 1}, [state('call-0', True)], {'status': 'match', 'case': 'H03'}),
    simple('H04', ['C1', 'C2'], 'ToolResultContractMatrixTests.ContentOnlyHookReplacementDropsStructuredContentButKeepsMetadata',
           2, {'tool0': 1}, [state('call-0')], {'status': 'match', 'case': 'H04'}),
    simple('H05', ['C2', 'C3'], 'ToolResultContractMatrixTests.ThrowingResultHookBecomesAnErrorResultNotASuccess',
           2, {'tool0': 1}, [state('call-0', True)], {'status': 'match', 'case': 'H05'}),
    simple('P01', ['C2'], 'ToolResultContractMatrixTests.ProviderContinuationReceivesEveryResultInModelCallOrder',
           2, {'tool0': 1, 'tool1': 1, 'tool2': 1},
           [state('call-0'), state('call-1'), state('call-2')], {'status': 'match', 'case': 'P01'}),
    simple('P02', ['C2'], 'ToolResultContractMatrixTests.CanonicalSaveAndReloadPreservesTheResultWithoutReexecution',
           2, {'tool0': 1}, [state('call-0')],
           {'status': 'not-applicable',
            'reason': 'PiSharp canonical session JSON is an implementation persistence boundary; pinned Pi has no equivalent API. Session JSONL persistence parity is covered by the merged session milestone.'}),
    simple('P03', ['C2'], 'ToolResultContractMatrixTests.PiJsonlRoundTripPreservesSupportedResultFields',
           1, {'tool0': 1}, [state('call-0', False, True)],
           {'status': 'not-applicable',
            'reason': 'Pi JSONL parser and interchange parity is owned by the merged session milestone differential; this case proves PiSharp result-field round-trip.'}),
    simple('P04', ['C2', 'C3'], 'ToolResultContractMatrixTests.ReturnedAndThrownToolErrorsRemainDistinctAfterReload',
           2, {'tool0': 1, 'tool1': 1}, [state('call-0', True), state('call-1', True)],
           {'status': 'not-applicable',
            'reason': 'Pinned Pi session records only isError and has no thrown-exception marker to differentiate after reload.'}),
    simple('P05', ['C2'], 'ToolResultContractMatrixTests.TerminatingBatchIsNotReplayedByANewUserTurn',
           2, {'tool0': 1}, [state('call-0', False, True)],
           {'status': 'covered-by', 'cases': ['P01'],
            'reason': 'Provider continuation after a terminating batch is the P01 continuation boundary; the matrix test asserts no replay.'}),
]


def observed(observation, expected):
    return {
        'requests': observation['requests'],
        'executions': {name: count for name, count in observation['executions'].items() if count},
        'resultStates': [{
            'callId': result['callId'],
            'isError': result['isError'],
            'terminate': result['terminate'],
        } for result in observation['canonical']],
        'piJsonlResultStates': [{
            'callId': item['callId'],
            'isError': item['isError'],
            'terminate': item['terminate'],
        } for item in observation.get('piJsonl', [])],
    }


def verify_observed(case, observation, expected):
    if observation['requests'] != expected['requests']:
        raise SystemExit(f'{case}: requests {observation["requests"]} != {expected["requests"]}')
    executions = {name: count for name, count in observation['executions'].items() if count}
    if executions != expected['executions']:
        raise SystemExit(f'{case}: executions {executions} != {expected["executions"]}')
    states = [{'callId': item['callId'], 'isError': item['isError'], 'terminate': item['terminate']}
              for item in observation['canonical']]
    if states != expected['resultStates']:
        raise SystemExit(f'{case}: canonical states {states} != {expected["resultStates"]}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--implementation-sha', required=True)
    args = parser.parse_args()

    cases = []
    passed = 0
    for entry in CASES:
        evidence_path = args.evidence / f'{entry["id"]}.json'
        if not evidence_path.exists():
            raise SystemExit(f'{entry["id"]}: missing evidence {evidence_path}')
        payload = json.loads(evidence_path.read_text())
        if payload['status'] != 'passed':
            raise SystemExit(f'{entry["id"]}: evidence status {payload["status"]}')
        if payload['implementationSha'] != args.implementation_sha:
            raise SystemExit(f'{entry["id"]}: evidence implementation SHA mismatch')
        observation = payload['observations']
        if entry['id'] == 'E07':
            for name in ('single', 'mixed'):
                sub = observation[name]
                sub_expected = {
                    'requests': 1 if name == 'single' else 2,
                    'executions': {'tool0': 1} if name == 'single' else {'tool0': 1, 'tool1': 1},
                    'resultStates': [state('call-0', True, True)] if name == 'single'
                    else [state('call-0', True, True), state('call-1')],
                }
                verify_observed(f'E07-{name}', sub, sub_expected)
            actual = {'single': observed(observation['single'], None),
                      'mixed': observed(observation['mixed'], None)}
        else:
            verify_observed(entry['id'], observation, entry['expected'])
            actual = observed(observation, entry['expected'])
        cases.append({
            'id': entry['id'],
            'criteria': entry['criteria'],
            'test': entry['test'],
            'evidence': f'evidence/{entry["id"]}.json',
            'expected': entry['expected'],
            'actual': actual,
            'status': 'passed',
            'differential': entry['differential'],
        })
        passed += 1

    manifest = {
        'schema': 1,
        'capability': 'tool-result-contracts',
        'piSha': 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
        'implementationSha': args.implementation_sha,
        'evidenceHeadPolicy': (
            'implementationSha is the source head that produced the executable evidence. Any later commit '
            'on the publication branch may touch only the committed evidence, this manifest/tooling, the '
            'execution-ledger entry and continuation notes; the verifier rejects any other changed path.'
        ),
        'evidenceDirectory': 'docs/parity/fixtures/tool-result-contracts/evidence',
        'differentialEvidence': 'docs/parity/fixtures/tool-result-contracts/differential.json',
        'ledgerCriteria': [{'id': key, 'text': text} for key, text in CRITERIA],
        'cases': cases,
        'summary': {'total': len(cases), 'passed': passed, 'failed': 0, 'blocked': 0,
                    'differentialMatch': sum(1 for item in cases if item['differential']['status'] == 'match'),
                    'differentialIntentional': sum(1 for item in cases
                                                  if item['differential']['status'] == 'intentional-difference'),
                    'differentialNotApplicable': sum(1 for item in cases
                                                    if item['differential']['status'] in ('not-applicable', 'covered-by'))},
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2) + '\n')
    print(f'wrote {args.output} with {passed} passed cases')


if __name__ == '__main__':
    main()
