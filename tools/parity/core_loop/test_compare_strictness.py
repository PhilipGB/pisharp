"""Negative controls for the comparison itself.

Ordering is part of the parity surface, so the comparator must never sort or normalise it away.
These cases use synthetic views, so they need neither a Pi checkout nor a product run.
"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from compare import compare_case  # noqa: E402


def view(**overrides):
    base = dict(events=[['agent_start']], providerRequests=[], effects=[], mcpCalls=[],
                state=None, entries=[], error=None)
    base.update(overrides)
    return base


class ComparisonStrictnessTests(unittest.TestCase):
    def test_effect_order_is_compared(self):
        diffs = compare_case(view(effects=['a', 'b']), view(effects=['b', 'a']))

        self.assertTrue(diffs, 'effect order must be compared, not sorted')

    def test_concurrent_mcp_dispatch_order_is_not_a_difference(self):
        # Documented exception: calls from one assistant turn are dispatched concurrently, and pinned
        # Pi itself produces both arrival orders. Only the set of calls is compared.
        diffs = compare_case(view(mcpCalls=[['append', {'id': 'a'}], ['gate', {'token': 't'}]]),
                             view(mcpCalls=[['gate', {'token': 't'}], ['append', {'id': 'a'}]]))
        self.assertEqual([], diffs)

    def test_cross_batch_mcp_reordering_is_a_difference(self):
        # Two assistant turns declared tool calls, so arrival order across turns is product behavior.
        two_batches = [['assistant', 'toolCall', False, '', [{'name': 'append', 'args': {'id': 'a'}}]],
                       ['assistant', 'toolCall', False, '', [{'name': 'append', 'args': {'id': 'b'}}]]]
        diffs = compare_case(
            view(events=two_batches, mcpCalls=[['append', {'id': 'a'}], ['append', {'id': 'b'}]]),
            view(events=two_batches, mcpCalls=[['append', {'id': 'b'}], ['append', {'id': 'a'}]]))
        self.assertTrue(diffs, 'calls from different tool batches must stay ordered')

    def test_single_batch_arrival_order_is_not_a_difference(self):
        one_batch = [['assistant', 'toolCall', False, '',
                      [{'name': 'append', 'args': {'id': 'a'}}, {'name': 'gate', 'args': {'token': 't'}}]]]
        diffs = compare_case(
            view(events=one_batch, mcpCalls=[['append', {'id': 'a'}], ['gate', {'token': 't'}]]),
            view(events=one_batch, mcpCalls=[['gate', {'token': 't'}], ['append', {'id': 'a'}]]))
        self.assertEqual([], diffs)

    def test_a_different_set_of_mcp_calls_is_still_a_difference(self):
        diffs = compare_case(view(mcpCalls=[['append', {'id': 'a'}], ['append', {'id': 'b'}]]),
                             view(mcpCalls=[['append', {'id': 'a'}]]))
        self.assertTrue(diffs, 'the multiset of MCP calls must still be compared')

    def test_event_order_is_compared(self):
        diffs = compare_case(view(events=[['tool_end', 'a'], ['queue', ['S1'], []]]),
                             view(events=[['queue', ['S1'], []], ['tool_end', 'a']]))
        self.assertTrue(diffs, 'lifecycle event order must be compared')

    def test_provider_request_tool_order_is_compared(self):
        request = dict(index=0, outcome='completed', messages=[], tools=['a', 'b'])
        swapped = dict(index=0, outcome='completed', messages=[], tools=['b', 'a'])
        diffs = compare_case(view(providerRequests=[request]), view(providerRequests=[swapped]))

        self.assertTrue(diffs, 'declared tool order must be compared, not sorted')

    def test_identical_views_have_no_diffs(self):
        self.assertEqual([], compare_case(view(effects=['a']), view(effects=['a'])))


if __name__ == '__main__':
    unittest.main()
