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
        # Fuzzy, non-prefix queries: "mdl" ranks model before scoped-models and
        # "hks" selects hotkeys even though neither is a literal prefix.
        action('fuzzy-mdl', '/mdl', editor='/mdl', quiet=700),
        barrier('fuzzy-mdl-barrier'),
        action('fuzzy-hks', CLEAR + '/hks', editor='/hks', quiet=700),
        barrier('fuzzy-hks-barrier'),
        action('no-match', CLEAR + '/zzzz', editor='/zzzz', absent='\u2192', quiet=700),
        barrier('no-match-barrier'),
        # Deterministic ordering with several candidates.
        action('multi-order', CLEAR + '/s', editor='/s', quiet=700),
        barrier('multi-order-barrier'),
        action('navigate-down-one', '\u001b[B', editor='/s', quiesce=True, quiet=500),
        barrier('navigate-down-one-barrier'),
        action('navigate-down-two', '\u001b[B', editor='/s', quiesce=True, quiet=500),
        barrier('navigate-down-two-barrier'),
        action('navigate-down-tab', '\t', expect='fixture-model', quiet=600),
        barrier('navigate-down-tab-barrier'),
        action('navigate-wrap', CLEAR + '/s', editor='/s', quiet=700),
        action('navigate-wrap-up', '\u001b[A', editor='/s', quiesce=True, quiet=500),
        barrier('navigate-wrap-barrier'),
        action('navigate-wrap-tab', '\t', expect='fixture-model', quiet=600),
        barrier('navigate-wrap-tab-barrier'),
        # Argument completion for the commands where pinned Pi exposes it.
        action('argument-model-query', CLEAR + '/model fix', editor='/model fix', quiet=700),
        barrier('argument-model-query-barrier'),
        action('argument-model-apply', '\t', expect='fixture-model', quiet=700),
        barrier('argument-model-apply-barrier'),
        action('argument-thinking-query', CLEAR + '/thinking ', editor='/thinking', quiet=700),
        barrier('argument-thinking-query-barrier'),
        action('argument-thinking-apply', '\t', expect='fixture-model', quiet=700),
        barrier('argument-thinking-apply-barrier'),
        action('argument-thinking-fuzzy', CLEAR + '/thinking o', editor='/thinking o', quiet=700),
        barrier('argument-thinking-fuzzy-barrier'),
        action('argument-no-match', CLEAR + '/model zzz', editor='/model zzz', absent='\u2192', quiet=700),
        barrier('argument-no-match-barrier'),
        # Moving the cursor back into the command name re-queries the picker.
        action('cursor-requery', CLEAR + '/model fix', editor='/model fix', quiet=700),
        barrier('cursor-requery-typed'),
        action('cursor-requery-left', '\u001b[D\u001b[D\u001b[D\u001b[D\u001b[D',
               editor='/model fix', quiesce=True, quiet=800),
        barrier('cursor-requery-barrier'),
        action('cursor-requery-escape', '\u001b', editor='/model fix', absent='\u2192'),
        # Deleting after dismissal reopens the picker, like Pi's Backspace path.
        action('escape-then-backspace', CLEAR + '/model', editor='/model', quiet=700),
        action('escape-close', '\u001b', editor='/model', absent='\u2192', quiet=600),
        barrier('escape-close-barrier'),
        action('backspace-reopen', '\u007f', editor='/mode', quiet=700),
        barrier('backspace-reopen-barrier'),
        action('clear-reopen', CLEAR, quiesce=True, checkpoint=False),
        # Completing a command must leave the editable draft intact for submission.
        action('draft-after-command', CLEAR + '/thinking of', editor='/thinking of', quiet=700),
        action('draft-after-command-tab', '\t', expect='fixture-model', quiet=700),
        action('draft-after-command-clear', '\u0001\u000b', editor='', quiet=400),
        action('draft-after-command-type', 'completion kept the draft', editor='completion kept the draft'),
        action('draft-after-command-submit', '\r', expect='FIXTURE_REPLY_1'),
        action('clear-final', CLEAR, editor=''),
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
    'Beta.txt': 'upper\n',
    'beta2.txt': 'lower\n',
    '.hidden.txt': 'hidden\n',
    '.hidden-dir/secret.txt': 'secret\n',
    '.git/config': '[core]\n',
    '\u4e16\u754c.txt': 'world\n',
    'caf\u00e9.txt': 'cafe\n',
    '\U0001f642.md': 'smile\n',
}


def login_completion():
    actions = [
        startup(),
        action('login-anthropic', '/login anthropic', editor='/login anthropic', quiet=900),
        barrier('login-anthropic-barrier'),
        action('login-anthropic-apply', '\t', editor='/login anthropic', quiet=700),
        barrier('login-anthropic-apply-barrier'),
        action('login-radius', CLEAR + '/login radius', editor='/login radius', quiet=900),
        barrier('login-radius-barrier'),
        action('login-no-match', CLEAR + '/login zzzz', editor='/login zzzz', absent='\u2192', quiet=700),
        barrier('login-no-match-barrier'),
        action('clear-final', CLEAR, editor=''),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('login-completion', 'login-completion', actions,
                extra={'compareCandidateDescriptions': True})


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
        # Fuzzy multi-candidate ordering and nested/recursive discovery.
        action('at-fuzzy', '\u0015@al', editor='@al', quiet=900),
        barrier('at-fuzzy-barrier'),
        action('at-nested', '\u0015@deep', editor='@deep', quiet=900),
        barrier('at-nested-barrier'),
        action('at-relative', '\u0015@./src', editor='@./src', quiet=900),
        barrier('at-relative-barrier'),
        action('at-case', '\u0015@BETA', editor='@BETA', quiet=900),
        barrier('at-case-barrier'),
        # Directory continuation keeps the completion open without a trailing space.
        action('at-directory', '\u0015@src', editor='@src', quiet=900),
        barrier('at-directory-barrier'),
        action('at-directory-continue', '\t', editor='@src/', quiet=900),
        barrier('at-directory-continue-barrier'),
        action('at-directory-child', 'mai', editor='@src/mai', quiet=900),
        barrier('at-directory-child-barrier'),
        action('at-directory-child-tab', '\t', editor='@src/main.cs', quiet=900),
        barrier('at-directory-child-tab-barrier'),
        # No matches leave the draft untouched and do not open an overlay.
        action('at-no-match', '\u0015@zzzz', editor='@zzzz', absent='\u2192', quiet=900),
        barrier('at-no-match-barrier'),
        action('at-no-match-tab', '\t', editor='@zzzz', absent='\u2192', quiet=900),
        barrier('at-no-match-tab-barrier'),
        # Selection and application, including Enter (which does not submit).
        action('at-select', '\u0015@al', editor='@al', quiet=900),
        action('at-select-down', '\u001b[B', editor='@al', quiesce=True, quiet=500),
        action('at-select-down-tab', '\t', expect='fixture-model', quiet=700),
        barrier('at-select-down-tab-barrier'),
        action('at-enter', '\u0015@alpha\r', expect='fixture-model', quiet=900),
        barrier('at-enter-barrier'),
        # Embedded completion preserves surrounding prose and reaches the provider.
        action('at-embedded', '\u0015look at @al', editor='look at @al', quiet=900),
        barrier('at-embedded-barrier'),
        action('at-embedded-tab', '\t', expect='fixture-model', quiet=900),
        barrier('at-embedded-tab-barrier'),
        action('at-embedded-submit', 'please\r', expect='FIXTURE_REPLY_1'),
        # Paste does not activate completion; the next real key does.
        action('at-paste', '\u0015\u001b[200~@alph\u001b[201~', editor='@alph', absent='\u2192', quiet=900),
        barrier('at-paste-barrier'),
        action('at-paste-type', 'a', editor='@alpha', quiet=900),
        barrier('at-paste-type-barrier'),
        action('at-paste-escape', '\u001b', editor='@alpha', absent='\u2192'),
        action('clear-paste', CLEAR, quiesce=True),
        # Dismissal followed by a deletion reopens the picker.
        action('at-escape-backspace', '\u0015@alp', editor='@alp', quiet=900),
        action('at-escape', '\u001b', editor='@alp', absent='\u2192', quiet=600),
        barrier('at-escape-barrier'),
        action('at-escape-backspace-key', '\u007f', editor='@al', quiet=900),
        barrier('at-escape-backspace-barrier'),
        action('clear-escape-backspace', CLEAR, quiesce=True, checkpoint=False),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('path-completion', 'path-completion', actions, files=PATH_FILES,
                bin_files={'fd': FD_SHIM}, extra={'compareCandidateDescriptions': True})


def path_symlink():
    files = {
        'target/notes.txt': 'target\n',
        'link.txt': {'$symlink': 'target/notes.txt'},
        'linked-dir': {'$symlink': 'target'},
        'ordinary.txt': 'ordinary\n',
    }
    actions = [
        startup(),
        action('symlink-file', '@link', editor='@link', quiet=900),
        barrier('symlink-file-barrier'),
        action('symlink-file-tab', '\t', expect='fixture-model', quiet=900),
        barrier('symlink-file-tab-barrier'),
        action('symlink-dir', '\u0015@linked', editor='@linked', quiet=900),
        barrier('symlink-dir-barrier'),
        action('symlink-dir-continue', '\t', editor='@linked-dir/', quiet=900),
        barrier('symlink-dir-continue-barrier'),
        action('symlink-dir-child', 'n', editor='@linked-dir/n', quiet=900),
        barrier('symlink-dir-child-barrier'),
        action('symlink-dir-child-tab', '\t', editor='@linked-dir/notes.txt', quiet=900),
        barrier('symlink-dir-child-tab-barrier'),
        action('clear-symlink', CLEAR, quiesce=True),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('path-symlink', 'path-completion', actions, files=files,
                bin_files={'fd': FD_SHIM}, extra={'compareCandidateDescriptions': True})


def file_tab_completion():
    files = {
        'readme.md': 'readme\n',
        'package.json': '{}\n',
        'Beta.txt': 'upper\n',
        'beta2.txt': 'lower\n',
        'src/index.ts': 'export {}\n',
    }
    actions = [
        startup(),
        action('tab-all', '\t', editor='', quiet=900),
        barrier('tab-all-barrier'),
        action('tab-narrow', 'B', editor='B', quiet=900),
        barrier('tab-narrow-barrier'),
        action('tab-apply', '\t', expect='fixture-model', quiet=900),
        barrier('tab-apply-barrier'),
        action('tab-clear', '\u0015', editor='', quiesce=True),
        action('tab-dir', 'src', editor='src', quiet=200),
        action('tab-dir-complete', '\t', editor='src/', quiet=900),
        barrier('tab-dir-complete-barrier'),
        action('tab-dir-child', 'i', editor='src/i', quiet=900),
        barrier('tab-dir-child-barrier'),
        action('tab-dir-child-apply', '\t', expect='fixture-model', quiet=900),
        barrier('tab-dir-child-apply-barrier'),
        action('tab-clear-final', '\u0015', editor='', quiesce=True),
        {'id': 'exit', 'send': '\u0004', 'exit': True, 'expect': 'fixture-model'},
    ]
    return base('file-tab-completion', 'path-completion', actions, files=files,
                extra={'compareCandidateDescriptions': True})


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
    login_completion,
    path_completion,
    path_symlink,
    file_tab_completion,
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
