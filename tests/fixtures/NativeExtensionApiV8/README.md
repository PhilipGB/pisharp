# PR #8 API compatibility fixture

The `Contract` directory snapshots the `PiSharp.Extensions.Abstractions` sources
from PR #8 merge commit `d9156e53c4822e7b98226df6170b29c61bc87eb1`, before the
PR #9 state and UI additions.
The extension is packed with the original assembly identity, API manifest version
1, and its normal dependency file. The current host must load and run its command,
context-transform, and tool-call registrations.
