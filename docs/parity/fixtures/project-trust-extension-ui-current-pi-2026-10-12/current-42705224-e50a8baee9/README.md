# Trusted project extension UI

This differential uses current Pi `4270522473e677ddba7b093caa0064a3df51d459` and PiSharp `e50a8baee9bc97d2e887077ce65cf85d605980df`. It exercises project-trust selection, the extension's select/confirm/input/notify callbacks, editor echo, prompt submission, the provider request, completion, and exit.

The same-Pi calibration matches all 14 canonical render checkpoints, terminal states, and HTTP requests. The paired Pi/PiSharp run also matches all 14 checkpoints: terminal state, rendered cells, and normalized HTTP traces have zero differences, with no scenario errors.

The extension trace is ordered as `configured`, `trust-undecided:true`, `trust-decision:true`, then `trust-ui:tui:true:Continue:true:approved`. Both products show the typed prompt in the editor and transcript, and submit exactly one identical user message: `Inspect the trusted project instructions.` Both requests include the trusted `trusted-check` skill. Their normalized system prompts have SHA-256 `7f4100f7c700f2dcfde0929ae2fa634d7242ada53df572e8e6f782fc0da9dd6a`; the normalized provider request bodies match exactly. The only raw request-body difference is the product-specific installed documentation path in the system message.

The three startup-dialog frames, completion frame, and quit frame match exactly. Neither product shows completed-run usage while work is still in progress. The number of intermediate working-animation renders is scheduling-dependent (14 for Pi and 3 for PiSharp in this run).

Raw PTY bytes and control-sequence boundaries are product-specific: the paired capture records 721 boundary differences. Same-Pi raw PTY bytes also differ even though canonical terminal state, renders, and HTTP match. The full streams are retained in the captures for further diagnosis.

Machine-readable reports, semantic assertions, compressed frame streams, and calibration captures are in `calibration/` and `paired/`. The semantic assertions explicitly check callback order, editor echo, submitted user message, trusted skill inclusion, normalized provider request, terminal checkpoints, and work-in-progress usage.
