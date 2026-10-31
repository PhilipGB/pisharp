"""Resolve the pinned Pi checkout for the core-loop parity tools.

One resolution point for the whole harness: ``--pi`` first, then ``PISHARP_PI_CHECKOUT``, and
otherwise a clear configuration error. There is deliberately no embedded developer path, so a
missing checkout is reported instead of silently pointing at one machine's filesystem.
"""

import os
from pathlib import Path

VARIABLE = 'PISHARP_PI_CHECKOUT'

# Resolved when this module is imported; empty when the environment does not configure a checkout.
DEFAULT = os.environ.get(VARIABLE) or None

MESSAGE = f'a pinned Pi checkout is required: pass --pi or set {VARIABLE}'


def require(parser, value):
    """Return the configured checkout as a path, or exit with a clear configuration error."""
    if not value:
        parser.error(MESSAGE)
    return Path(value)
