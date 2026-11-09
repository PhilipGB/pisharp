"""Declared normalisations and unresolved differences for the editor milestone."""

NORMALIZATIONS = [
    {
        'id': 'post-exit-scroll-position',
        'scope': 'the named exit checkpoint',
        'reason': 'After returning to the normal screen the absolute cursor row reflects each product\'s '
                  'transcript height. Process exit compares exitCode, active screen, cursor visibility and '
                  'private modes instead. No editor state is normalised.',
    },
    {
        'id': 'completion-label-display-prefix',
        'scope': 'overlay candidate labels',
        'reason': 'Pi renders a path label without the leading @ while its value carries the marker. '
                  'One leading @ is removed from the display label only; candidate identity and order are '
                  'still compared exactly.',
    },
    {
        'id': 'completion-position-row',
        'scope': 'overlay rows',
        'reason': 'The "(n/m)" position row reports the visible slice of the candidate list. It is recorded '
                  'separately and excluded from candidate identities.',
    },
    {
        'id': 'external-editor-temporary-path',
        'scope': 'external-editor captures',
        'reason': 'Temporary directory names and file modes are product internals. Both products create a '
                  '0700 directory; Pi writes the prompt with mode 0644 and PiSharp with 0600. The captured '
                  'prompt text, editor result, provider text and terminal restoration are compared exactly.',
    },
]

KNOWN_DIFFERENCES = []
