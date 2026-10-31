"""Normalization and comparison for core-loop differential observations.

Compares externally observable behavior only:
  * provider-visible request message/tool sequences,
  * the projected RPC event stream (lifecycle, messages, tools, queue updates),
  * non-idempotent MCP side effects and MCP call sequences,
  * the final get_state projection,
  * the persisted session history projection (see harness.project_session_entries).

Product-specific static content (session ids/paths, timestamps, uuids, the docs
section of the system prompt, chunk-level streaming events) is masked.
"""
import json
from difflib import unified_diff

_STOP_CANONICAL = {'stop': 'stop', 'toolUse': 'toolCall', 'tool_calls': 'toolCall',
                   'aborted': 'aborted', 'error': 'error', 'length': 'length'}


def _canonical_stop(stop):
    return _STOP_CANONICAL.get(stop, stop)


def _content_text(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return '\n'.join(block.get('text', '') for block in content
                         if isinstance(block, dict) and block.get('type') == 'text')
    return ''


def project_events(records):
    events = []
    for record in records:
        kind = record.get('type')
        if kind == 'response':
            command = record.get('command')
            if command in ('get_state', 'get_entries', 'get_messages'):
                continue
            data = record.get('data')
            disposition = data.get('disposition') if isinstance(data, dict) else None
            error = record.get('error')
            events.append(('resp', command, bool(record.get('success')),
                           disposition,
                           error.get('message') if isinstance(error, dict) else None))
        elif kind in ('agent_start', 'agent_end', 'turn_start'):
            events.append((kind,))
        elif kind == 'agent_settled':
            events.append(('settled', bool(record.get('aborted'))))
        elif kind == 'turn_end':
            events.append(('turn_end', (record.get('message') or {}).get('stopReason')))
        elif kind in ('message_start', 'message_end'):
            message = record.get('message') or {}
            role = message.get('role')
            if role == 'system':
                if kind == 'message_start':
                    events.append(('system',))
                continue
            if role == 'user':
                if kind == 'message_start':
                    events.append(('user', _content_text(message.get('content'))))
                continue
            if role == 'assistant' and kind == 'message_end':
                content = message.get('content')
                text_parts, tool_calls = [], []
                if isinstance(content, str):
                    text_parts.append(content)
                elif isinstance(content, list):
                    for block in content:
                        if not isinstance(block, dict):
                            continue
                        if block.get('type') == 'text':
                            text_parts.append(block.get('text', ''))
                        elif block.get('type') == 'toolCall':
                            tool_calls.append(block.get('name'))
                tool_calls += [call.get('name') for call in message.get('toolCalls', [])
                               if isinstance(call, dict)]
                events.append(('assistant', _canonical_stop(message.get('stopReason')),
                               bool(message.get('isError')),
                               '\n'.join(text_parts), tool_calls))
                continue
            if role == 'toolResult' and kind == 'message_end':
                events.append(('toolResult', bool(message.get('isError')),
                               _content_text(message.get('content'))))
                continue
        elif kind == 'tool_execution_start':
            args = record.get('args')
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except ValueError:
                    pass
            args = json.dumps(args, sort_keys=True) if isinstance(args, dict) else str(args)
            events.append(('tool_start', record.get('toolName'), args))
        elif kind == 'tool_execution_end':
            events.append(('tool_end', record.get('toolName'), bool(record.get('isError'))))
        elif kind == 'queue_update':
            events.append(('queue', record.get('steering'), record.get('followUp')))
        elif kind in ('auto_retry_started', 'auto_retry_succeeded',
                      'compaction_start', 'compaction_end', 'aborted', 'diagnostic'):
            payload = json.dumps({key: value for key, value in record.items() if key != 'type'},
                                 sort_keys=True, default=str)[:300]
            events.append((kind, payload))
        # message_update and other wire-level streaming events are out of scope
    return events


def project_provider_requests(requests):
    view = []
    for request in requests:
        messages = []
        for message in request.get('messages', []):
            role = message.get('role')
            if role == 'system':
                messages.append(['system'])
                continue
            normalized = [role]
            text = message.get('text')
            if text:
                normalized.append(text)
            if role == 'assistant':
                stop = message.get('stopReason')
                normalized.append(_canonical_stop(stop) if stop else None)
                calls = [dict(name=call.get('name'), args=call.get('args'))
                         for call in message.get('toolCalls', [])]
                if calls:
                    normalized.append(calls)
            if message.get('toolCall'):
                normalized.append('tool-result')
            messages.append(normalized)
        view.append(dict(index=request.get('index'), outcome=request.get('outcome'),
                         messages=messages, tools=request.get('tools', [])))
    return view


_STATE_KEYS = ('steeringMode', 'followUpMode', 'isStreaming', 'isCompacting', 'messageCount',
               'pendingMessageCount', 'autoCompactionEnabled', 'thinkingLevel')


def project_state(state):
    if not isinstance(state, dict):
        return state
    view = {key: state.get(key) for key in _STATE_KEYS}
    model = state.get('model') or {}
    view['model'] = {key: model.get(key) for key in ('id', 'provider', 'api', 'contextWindow',
                                                     'maxTokens', 'reasoning', 'input')}
    return view


def normalize_entries(entries):
    """The system/context entry position relative to the first user prompt is a session-format
    detail: Pi records the system section before the prompt, while PiSharp persists the tool
    transcript when it is first populated (after the request that seeds it). The system section
    is context, not a conversation message, so compare it as present (counted) and compare the
    remaining entries in order, without asserting the system entry's exact position."""
    system_count = sum(1 for entry in entries if entry == ['system'])
    rest = [entry for entry in entries if entry != ['system']]
    return [f'system x{system_count}'] + rest


def project_observation(obs):
    return dict(
        events=project_events(obs['records']),
        providerRequests=project_provider_requests(obs['providerRequests']),
        # Physical side-effect order and physical MCP call order are compared as observed. Where a
        # product's own ordering is nondeterministic, the permitted alternatives come from Pi-vs-Pi
        # calibration (tools/parity/core_loop/order_calibration.json); the evidence is never sorted
        # to make a comparison pass.
        effects=obs.get('effects', []),
        mcpCalls=[[call.get('name'), call.get('args')] for call in obs.get('mcpCalls', [])],
        state=project_state(obs.get('state')),
        entries=normalize_entries(obs.get('sessionEntries', [])),
        error=obs.get('error'),
    )


def compare_views(a, b, label):
    if a == b:
        return []
    if isinstance(a, list) and isinstance(b, list):
        diffs = []
        for i in range(max(len(a), len(b))):
            va = a[i] if i < len(a) else None
            vb = b[i] if i < len(b) else None
            if va != vb:
                diffs.append(f'{label}[{i}] {json.dumps(va, default=str)} != {json.dumps(vb, default=str)}')
        return diffs
    return [f'{label}: {json.dumps(a, default=str)} != {json.dumps(b, default=str)}']


FIELDS = ('events', 'providerRequests', 'effects', 'mcpCalls', 'state', 'entries', 'error')


def compare_case(view_pi, view_pisharp):
    diffs = []
    for field in FIELDS:
        diffs.extend(compare_views(view_pi.get(field), view_pisharp.get(field), field))
    return diffs


def render_diff(view_a, view_b):
    lines_a = [f'== {field} ==\n{json.dumps(view_a.get(field), default=str, indent=1)}'
               for field in FIELDS]
    lines_b = [f'== {field} ==\n{json.dumps(view_b.get(field), default=str, indent=1)}'
               for field in FIELDS]
    return '\n'.join(unified_diff(lines_a, lines_b, 'pi', 'pisharp', lineterm=''))
