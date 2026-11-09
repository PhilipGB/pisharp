"""Deterministic real-process scenarios for the TUI editor parity milestone.

Each scenario is executed against the pinned Pi and PiSharp through the shared
terminal harness at 80x24 and 120x40. Actions settle on editor-region text or
declared overlay/footer content; no action relies on a fixed sleep.
"""

SIZES = ({'columns': 80, 'rows': 24}, {'columns': 120, 'rows': 40})

PI_BASE = [
    '--provider', 'fixture', '--model', 'fixture-model', '--no-session', '--offline',
    '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools',
    '--no-context-files', '--system-prompt', 'You are a terminal parity fixture.',
]
PISHARP_BASE = [
    '--provider', 'fixture', '--model', 'fixture-model', '--no-session', '--offline',
    '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools', '--no-approve',
    '--no-context-files', '--system-prompt', 'You are a terminal parity fixture.',
]
WORKSPACE = '/tmp/pisharp-terminal-fixture-workspace'
AGENT = '/tmp/pisharp-terminal-fixture-agent'
FAKE_EDITOR = AGENT + '/bin/fake-editor.sh'
FAILING_EDITOR = AGENT + '/bin/failing-editor.sh'
CAPTURE = WORKSPACE + '/external-capture'

CLEAR = ('\u0015\u000b') * 6 + '\u0015'
FD_SHIM = ('#!/bin/sh\n'
           'if [ "$1" = "--version" ]; then printf \'fd 10.2.0\\n\'; exit 0; fi\n'
           'exec /usr/bin/fdfind "$@"\n')


def base(scenario_id, task, actions, *, files=None, settings=None, bin_files=None,
         environment=None, capture=None, extra=None):
    value = {
        'id': scenario_id,
        'task': task,
        'settings': {'quietStartup': True, **(settings or {})},
        'arguments': {'pi': list(PI_BASE), 'pisharp': list(PISHARP_BASE)},
        'fixtureBehavior': {
            'chatResponseContentByRequest': ['FIXTURE_REPLY_%d' % index for index in range(1, 9)],
        },
        'dimensions': [dict(size) for size in SIZES],
        'themes': ['dark'],
        'modes': ['fullscreen'],
        'assertionFrames': [action['id'] for action in actions if action.get('checkpoint', True)],
        'actions': actions,
    }
    if files:
        value['projectFiles'] = files
    if bin_files:
        value['binFiles'] = bin_files
    if environment:
        value['environment'] = environment
    if capture:
        value['captureWorkspaceFiles'] = capture
    if extra:
        value.update(extra)
    return value


def action(action_id, send, *, editor=None, expect=None, absent=None, checkpoint=True,
           static=False, quiet=250, exit=False, quiesce=False, cursor_change=False):
    value = {'id': action_id, 'send': send}
    if cursor_change:
        value['expectCursorChange'] = True
    if exit:
        value['exit'] = True
    if quiesce:
        value['quiesce'] = True
    if editor is not None:
        value['expectEditor'] = editor
    if expect is not None:
        value['expect'] = expect
    if absent is not None:
        value['notExpect'] = absent
    if not checkpoint:
        value['checkpoint'] = False
    if static:
        value['allowStaticFrame'] = True
    if quiet != 250:
        value['quietMs'] = quiet
    return value


def startup():
    return action('startup', '', expect='fixture-model', checkpoint=False, static=True)


def barrier(action_id, quiet=300):
    return {'id': action_id, 'quiesce': True, 'quietMs': quiet}


CURSOR_TEXT = 'alpha \u4e16\u754c cafe\u0301 \U0001f642 omega'


def cursor_unicode():
    actions = [
        startup(),
        action('typed', CURSOR_TEXT, editor=CURSOR_TEXT),
        action('left-two', '\u001b[D\u001b[D', editor=CURSOR_TEXT),
        action('home', '\u0001', editor=CURSOR_TEXT),
        action('right-cjk', '\u001b[C\u001b[C\u001b[C', editor=CURSOR_TEXT),
        action('end', '\u0005', editor=CURSOR_TEXT),
        action('word-left', '\u001b[1;3D', editor=CURSOR_TEXT),
        action('word-right', '\u001b[1;3C', editor=CURSOR_TEXT),
        action('backspace', '\u007f', editor='alpha \u4e16\u754c cafe\u0301 \U0001f642 omeg'),
        action('home-delete', '\u0001\u001b[3~', editor='lpha \u4e16\u754c cafe\u0301 \U0001f642 omeg'),
        action('submit', '\r', expect='FIXTURE_REPLY_1'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('cursor-unicode', 'cursor-unicode', actions)


def multiline():
    actions = [
        startup(),
        action('line-one', 'line one', editor='line one'),
        action('two-lines', '\nline two', editor='line one\nline two'),
        action('up-first', '\u001b[A', editor='line one\nline two'),
        action('edit-first', 'X', editor='line oneX\nline two'),
        action('down-second', '\u001b[B', editor='line oneX\nline two'),
        action('delete-second', '\u007f', editor='line oneX\nline tw'),
        action('join-newline', '\u0001\u007f', editor='line oneXline tw'),
        action('submit', '\r', expect='FIXTURE_REPLY_1'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('multiline', 'multiline', actions)


def soft_wrap():
    actions = [
        startup(),
        action('wrap-typed', 'word ' * 25 + 'word', editor='word', expect='fixture-model',
               quiet=1000),
        barrier('wrap-typed-barrier'),
        action('wrap-up', '\u001b[A', expect='fixture-model', quiet=800),
                action('wrap-up', '\u001b[A', expect='fixture-model', quiet=800),
        action('wrap-edit', 'X', editor='X'),
        action('wrap-submit', '\r', expect='FIXTURE_REPLY_1'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('soft-wrap', 'multiline', actions)


def history():
    actions = [
        startup(),
        action('submit-one', 'zebra prompt one\r', expect='FIXTURE_REPLY_1'),
        action('two-type', 'yak one', editor='yak one'),
        action('two-newline', '\nyak two', editor='yak one\nyak two'),
        action('submit-two', '\r', expect='FIXTURE_REPLY_2'),
        action('draft', 'draft only', editor='draft only'),
        action('draft-home', '\u0001', editor='draft only', cursor_change=True),
        action('up-newest', '\u001b[A', editor='yak one\nyak two'),
        action('up-older', '\u001b[A', editor='zebra prompt one'),
        action('down-newer', '\u001b[B', editor='yak one\nyak two'),
        action('down-draft', '\u001b[B', editor='draft only'),
        action('dup-one', CLEAR + 'dup prompt\r', expect='FIXTURE_REPLY_3'),
        action('dup-two', 'dup prompt\r', expect='FIXTURE_REPLY_4'),
        action('dup-up-one', '\u001b[A', editor='dup prompt'),
        action('dup-up-two', '\u001b[A', editor='yak one\nyak two'),
        action('recall-submit', '\r', expect='FIXTURE_REPLY_5'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('history', 'history', actions)


def slash_completion():
    actions = [
        startup(),
        action('slash-empty', '/', editor='/', quiet=500),
        action('slash-ambiguous', 's', editor='/s', quiet=500),
        action('cancel-ambiguous', '\u001b', editor='/s', absent='\u2192'),
        action('clear-ambiguous', CLEAR, editor=''),
        action('leading-spaces', '  /hot', editor='  /hot', quiet=500),
        action('unique-tab', '\t', expect='fixture-model', quiet=500),
        action('unique-escape', '\u001b', editor='/hotkeys', absent='\u2192'),
        action('clear-unique', CLEAR, editor=''),
        action('mid-sentence', 'hello /hot', editor='hello /hot', quiet=500),
        action('mid-sentence-tab', '\t', expect='fixture-model', quiet=500),
        action('clear-mid', CLEAR, editor=''),
        action('argument', '/model ', editor='/model', quiet=500),
        action('argument-cancel', '\u001b', editor='/model', absent='\u2192'),
        action('clear-argument', CLEAR, editor=''),
        action('picker', '/s', editor='/s', quiet=500),
        action('picker-down', '\u001b[B', quiesce=True, quiet=400),
        action('picker-up', '\u001b[A', quiesce=True, quiet=400),
        action('picker-cancel', '\u001b', editor='/s', absent='\u2192'),
        action('clear-picker', CLEAR, editor=''),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('slash-completion', 'slash-completion', actions)


PATH_FILES = {
    'alpha.txt': 'a\n',
    'alpine.md': 'b\n',
    'ambiguous-alpha.txt': 'c\n',
    'ambiguous-beta.txt': 'd\n',
    'src/main.cs': 'class Main {}\n',
    'src/module/deep.cs': 'class Deep {}\n',
    'docs/guide.md': 'guide\n',
    'space name.txt': 'space\n',
    '.hidden.txt': 'hidden\n',
    '.hidden-dir/secret.txt': 'secret\n',
    '.git/config': '[core]\n',
    '\u4e16\u754c.txt': 'world\n',
    'caf\u00e9.txt': 'cafe\n',
    '\U0001f642.md': 'smile\n',
}


def path_completion():
    actions = [
        startup(),
        action('at-empty', '@', editor='@', quiet=800),
        action('at-a', 'a', editor='@a', quiet=800),
        action('at-ambiguous', 'mbiguous-', editor='@ambiguous-', quiet=800),
        action('path-cancel-a', '\u001b', editor='@ambiguous-', absent='\u2192'),
        action('clear-a', CLEAR, editor=''),
        action('at-src-dir', '@src/', editor='@src/', quiet=800),
        action('at-src-continue', 'm', editor='@src/m', quiet=800),
        action('path-cancel-src', '\u001b', editor='@src/m', absent='\u2192'),
        action('clear-src', CLEAR, editor=''),
        action('at-hidden', '@.', editor='@.', quiet=800),
        action('path-cancel-hidden', '\u001b', editor='@.', absent='\u2192'),
        action('clear-hidden', CLEAR, editor=''),
        action('at-git', '@.git', editor='@.git', quiet=800),
        action('clear-git', CLEAR, editor=''),
        action('at-space', '@sp', editor='@sp', quiet=800),
        action('apply-space', '\t', expect='fixture-model', quiet=600),
        action('clear-space', CLEAR, editor=''),
        action('at-unicode', '@\u4e16', editor='@\u4e16', quiet=800),
        action('path-cancel-unicode', '\u001b', editor='@\u4e16', absent='\u2192'),
        action('clear-unicode', CLEAR, editor=''),
        action('at-quoted', '@"sp', editor='@"sp', quiet=800),
        action('quoted-cancel', '\u001b', editor='@"sp', absent='\u2192'),
        action('clear-quoted', CLEAR, editor=''),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('path-completion', 'path-completion', actions, files=PATH_FILES,
                bin_files={'fd': FD_SHIM})


PASTE_LARGE = '\n'.join('bounded line %02d' % index for index in range(1, 11))


def bracketed_paste():
    actions = [
        startup(),
        action('paste-single', '\u001b[200~alpha beta\u001b[201~', editor='alpha beta'),
        action('clear-single', CLEAR, editor=''),
        action('paste-multiline', '\u001b[200~one\ntwo\nthree\u001b[201~', editor='one\ntwo\nthree'),
        action('clear-multiline', CLEAR, editor=''),
        action('paste-padded', '\u001b[200~  padded  \u001b[201~', editor='  padded'),
        action('clear-padded', CLEAR, editor=''),
        action('paste-tabs', '\u001b[200~a\tb\tc\u001b[201~', editor='a    b    c'),
        action('clear-tabs', CLEAR, editor=''),
        action('paste-unicode', '\u001b[200~\u4e16\u754c \U0001f642 cafe\u0301\u001b[201~',
               editor='\u4e16\u754c \U0001f642 cafe\u0301'),
        action('clear-unicode', CLEAR, editor=''),
        action('paste-slash', '\u001b[200~/hot\u001b[201~', editor='/hot', quiet=700),
        action('paste-slash-typed', 'k', editor='/hotk', quiet=700),
        action('paste-slash-cancel', '\u001b', editor='/hotk', absent='\u2192'),
        action('clear-slash', CLEAR, quiesce=True, quiet=500),
        action('paste-at', '\u001b[200~@alpha.txt\u001b[201~', editor='@alpha.txt', quiet=700),
        action('clear-at', CLEAR, editor=''),
        action('paste-large', '\u001b[200~' + PASTE_LARGE + '\u001b[201~', editor='bounded line 10'),
        action('large-backspace', '\u007f', editor='bounded line 1'),
        action('large-submit', '\r', expect='FIXTURE_REPLY_1'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('bracketed-paste', 'bracketed-paste', actions)



PASTE_ELEVEN = '\n'.join(f'line {index:02d}' for index in range(1, 12))
PASTE_1001 = 'x' * 1001


def large_paste_marker():
    actions = [
        startup(),
        action('paste-eleven-lines', '\u001b[200~' + PASTE_ELEVEN + '\u001b[201~',
               editor='[paste #1 +11 lines]'),
        action('marker-left', '\u001b[D', editor='[paste #1 +11 lines]', cursor_change=True),
        action('marker-right', '\u001b[C', editor='[paste #1 +11 lines]', cursor_change=True),
        action('clear-eleven', '\u007f', editor=''),
        action('paste-1001-chars', '\u001b[200~' + PASTE_1001 + '\u001b[201~',
               editor='[paste #1 1001 chars]'),
        action('clear-1001', '\u007f', editor=''),
        action('paste-first', '\u001b[200~' + PASTE_ELEVEN + '\u001b[201~',
               editor='[paste #1 +11 lines]'),
        action('paste-middle', ' middle ', editor='[paste #1 +11 lines] middle'),
        action('paste-second', '\u001b[200~' + PASTE_ELEVEN + '\u001b[201~',
               editor='[paste #1 +11 lines] middle [paste #2 +11 lines]'),
        action('marker-home', '\u0001', editor='[paste #1 +11 lines] middle [paste #2 +11 lines]',
               cursor_change=True),
        action('marker-first-right', '\u001b[C',
               editor='[paste #1 +11 lines] middle [paste #2 +11 lines]', cursor_change=True),
        action('delete-first-marker', '\u007f', editor=' middle [paste #1 +11 lines]'),
        action('undo-first-marker', '\u001f',
               editor='[paste #1 +11 lines] middle [paste #2 +11 lines]'),
        action('marker-end', '\u0005',
               editor='[paste #1 +11 lines] middle [paste #2 +11 lines]', cursor_change=True),
        action('marker-second-left', '\u001b[D',
               editor='[paste #1 +11 lines] middle [paste #2 +11 lines]', cursor_change=True),
        action('delete-second-marker', '\u001b[3~', editor='[paste #1 +11 lines] middle'),
        action('undo-second-marker', '\u001f',
               editor='[paste #1 +11 lines] middle [paste #2 +11 lines]'),
        action('submit-two-markers', '\r', expect='FIXTURE_REPLY_1'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('large-paste-marker', 'bracketed-paste', actions)


def large_paste_external_editor():
    actions = [
        startup(),
        action('paste-eleven-lines', '\u001b[200~' + PASTE_ELEVEN + '\u001b[201~',
               editor='[paste #1 +11 lines]'),
        action('open-editor', '\u0007', expect='fixture-model', quiet=800),
        action('edited', 'edited line one', editor='edited line one\nedited line two \U0001f642',
               quiet=800),
        action('submit', '\r', expect='FIXTURE_REPLY_1'),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base(
        'large-paste-external-editor', 'bracketed-paste', actions,
        bin_files={'fake-editor.sh': FAKE_EDITOR_SCRIPT},
        settings={'externalEditor': FAKE_EDITOR + ' ' + CAPTURE},
        capture=['external-capture.input'],
    )

def escape_ctrl_c():
    actions = [
        startup(),
        action('draft', 'draft text', editor='draft text'),
        action('escape-preserves', '\u001b', editor='draft text', static=True),
        barrier('escape-preserves-barrier'),
        action('ctrl-c-clears', '\u0003', editor=''),
        action('retype', 'after clear', editor='after clear'),
        action('ctrl-d-nonempty', '\u0004', editor='after clear', static=True),
        action('clear-before-picker', CLEAR, editor=''),
        action('pick-slash', '/hot', editor='/hot', quiet=500),
        action('escape-picker', '\u001b', editor='/hot', absent='\u2192'),
        action('reopen-picker', 'k', editor='/hotk', quiet=600),
        action('ctrl-c-picker', '\u0003', quiesce=True, quiet=600),
        action('clear-picker', CLEAR, editor=''),
        action('paste-then-escape', '\u001b[200~pasted\ntext\u001b[201~', editor='pasted\ntext'),
        action('escape-pasted', '\u001b', editor='pasted\ntext', absent='\u2192'),
        action('ctrl-c-pasted', '\u0003', editor=''),
        action('retype-final', 'final draft', editor='final draft'),
        action('double-ctrl-c', '\u0003\u0003', exit=True, expect='fixture-model'),
    ]
    return base('escape-ctrl-c', 'escape-ctrl-c', actions)


def exit_empty_ctrl_d():
    actions = [
        startup(),
        {'id': 'ctrl-d-empty', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('exit-empty-ctrl-d', 'escape-ctrl-c', actions)


FAKE_EDITOR_SCRIPT = (
    '#!/bin/sh\n'
    'cap="$1"\n'
    'file="$2"\n'
    'printf \'%s\' "$file" > "$cap.path"\n'
    'cat "$file" > "$cap.input"\n'
    'stat -c \'%a\' "$file" > "$cap.mode"\n'
    'stat -c \'%a\' "$(dirname "$file")" > "$cap.directory-mode"\n'
    'printf \'edited line one\\nedited line two \U0001f642\\n\' > "$file"\n'
)
FAILING_EDITOR_SCRIPT = (
    '#!/bin/sh\n'
    'cap="$1"\n'
    'file="$2"\n'
    'printf \'%s\' "$file" > "$cap.path"\n'
    'cat "$file" > "$cap.input"\n'
    'printf \'FAILED EDITOR RAN\\n\' >&2\n'
    'exit 7\n'
)


def external_editor():
    actions = [
        startup(),
        action('draft', 'original draft', editor='original draft'),
        action('open-editor', '\u0007', expect='fixture-model', quiet=800),
        action('edited', 'edited line one', editor='edited line one\nedited line two \U0001f642', quiet=800),
        action('submit', '\r', expect='FIXTURE_REPLY_1'),
        action('after-prompt', 'next prompt', editor='next prompt'),
        action('clear-next', CLEAR, editor=''),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base(
        'external-editor', 'external-editor', actions,
        bin_files={'fake-editor.sh': FAKE_EDITOR_SCRIPT},
        settings={'externalEditor': FAKE_EDITOR + ' ' + CAPTURE},
        capture=['external-capture.path', 'external-capture.input',
                 'external-capture.mode', 'external-capture.directory-mode'],
    )


def external_editor_failure():
    actions = [
        startup(),
        action('draft', 'preserved draft', editor='preserved draft'),
        action('open-editor', '\u0007', quiesce=True, quiet=1000),
        action('preserved', '', quiesce=True, quiet=500),
        action('next-prompt', '\u0001\u0015next prompt', editor='next prompt'),
        action('clear-next', CLEAR, editor=''),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base(
        'external-editor-failure', 'external-editor', actions,
        bin_files={'failing-editor.sh': FAILING_EDITOR_SCRIPT},
        settings={'externalEditor': FAILING_EDITOR + ' ' + CAPTURE},
        capture=['external-capture.path', 'external-capture.input'],
    )


def external_editor_visual():
    actions = [
        startup(),
        action('draft', 'visual draft', editor='visual draft'),
        action('open-editor', '\u0007', expect='fixture-model', quiet=800),
        action('edited', 'edited line one', editor='edited line one\nedited line two \U0001f642', quiet=800),
        {'id': 'exit', 'send': '\u0003\u0003', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base(
        'external-editor-visual', 'external-editor', actions,
        bin_files={'fake-editor.sh': FAKE_EDITOR_SCRIPT},
        environment={'pi': {'VISUAL': FAKE_EDITOR + ' ' + CAPTURE},
                     'pisharp': {'VISUAL': FAKE_EDITOR + ' ' + CAPTURE}},
        capture=['external-capture.path', 'external-capture.input'],
    )


def external_editor_editor_variable():
    actions = [
        startup(),
        action('draft', 'editor draft', editor='editor draft'),
        action('open-editor', '\u0007', expect='fixture-model', quiet=800),
        action('edited', 'edited line one', editor='edited line one\nedited line two \U0001f642', quiet=800),
        {'id': 'exit', 'send': '\u0003\u0003', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base(
        'external-editor-editor', 'external-editor', actions,
        bin_files={'fake-editor.sh': FAKE_EDITOR_SCRIPT},
        environment={'pi': {'EDITOR': FAKE_EDITOR + ' ' + CAPTURE},
                     'pisharp': {'EDITOR': FAKE_EDITOR + ' ' + CAPTURE}},
        capture=['external-capture.path', 'external-capture.input'],
    )


def journey():
    actions = [
        startup(),
        action('type-unicode', 'alpha \u4e16\u754c', editor='alpha \u4e16\u754c'),
        action('move-word', '\u001b[1;3D', editor='alpha \u4e16\u754c', cursor_change=True),
        action('move-end', '\u0005', editor='alpha \u4e16\u754c', cursor_change=True),
        action('newline', '\nsecond', editor='alpha \u4e16\u754c\nsecond'),
        action('paste', '\u001b[200~pasted\nblock\u001b[201~',
               editor='alpha \u4e16\u754c\nsecondpasted\nblock'),
        action('submit-seed', '\r', expect='FIXTURE_REPLY_1'),
        action('draft', 'draft', editor='draft'),
        action('recall', '\u0001\u001b[A', editor='alpha \u4e16\u754c\nsecondpasted\nblock'),
        action('edit-recall', 'X', editor='Xalpha \u4e16\u754c\nsecondpasted\nblock'),
        action('clear-recall', '\u0003', quiesce=True, quiet=600),
        action('slash', '/hot', editor='/hot', quiet=600),
        action('slash-cancel', '\u001b', editor='/hot', absent='\u2192'),
        action('clear-slash', CLEAR, quiesce=True, quiet=500),
        action('path', '@alpha', editor='@alpha', quiet=800),
        action('path-cancel', '\u001b', editor='@alpha', absent='\u2192'),
        action('clear-path', CLEAR, quiesce=True, quiet=500),
        action('draft-for-editor', 'journey draft', editor='journey draft'),
        action('open-editor', '\u0007', expect='fixture-model', quiet=800),
        action('edited', 'edited line one', editor='edited line one\nedited line two \U0001f642', quiet=800),
        action('submit', '\r', expect='FIXTURE_REPLY_2'),
        action('usable', 'after journey', editor='after journey'),
        action('clear-usable', CLEAR, editor=''),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base(
        'journey', 'journey', actions,
        files={'alpha.txt': 'a\n'},
        settings={'externalEditor': FAKE_EDITOR + ' ' + CAPTURE},
        bin_files={'fd': FD_SHIM, 'fake-editor.sh': FAKE_EDITOR_SCRIPT},
        capture=['external-capture.path', 'external-capture.input'],
    )


SCENARIOS = (
    cursor_unicode,
    multiline,
    soft_wrap,
    history,
    slash_completion,
    path_completion,
    bracketed_paste,
    large_paste_marker,
    large_paste_external_editor,
    escape_ctrl_c,
    exit_empty_ctrl_d,
    external_editor,
    external_editor_failure,
    external_editor_visual,
    external_editor_editor_variable,
    journey,
)


def all_scenarios():
    return [builder() for builder in SCENARIOS]
