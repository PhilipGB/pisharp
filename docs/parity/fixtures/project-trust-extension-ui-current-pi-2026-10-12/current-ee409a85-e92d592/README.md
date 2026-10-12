# Current-Pi trusted project-extension UI replay

Pins: Pi `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`; PiSharp `e92d592b818cb5655046b98abafbd288a5e375d5`.

The `120x40-dark-fullscreen` paired PTY run and independent same-Pi calibration both match all 14 normalized terminal render frames, terminal state, and HTTP traces. The project-trust extension trace is `configured`, `trust-undecided:true`, `trust-decision:true`, then `trust-ui:tui:true:Continue:true:approved`. Both products make one provider request containing the trusted project skill. Their normalized generated system prompts match at SHA-256 `7f4100f7c700f2dcfde0929ae2fa634d7242ada53df572e8e6f782fc0da9dd6a`; normalized provider request bodies match.

The trust selector, confirmation prompt, completed prompt, and quit checkpoints match. Both products echo and submit the same user text, and neither shows completed-run usage while work is in progress. The working indicator appears in 7 Pi frames and 2 PiSharp frames; this animation count varies with PTY scheduling. Raw PTY bytes differ because the terminal control protocols differ. The only raw provider-request difference is the generated documentation package path, which is normalized in the semantic comparison.

The paired capture, semantic assertions, and same-Pi calibration are stored beside this file. The Python runner and its child processes are capped at two CPUs and common worker threads by [the terminal harness](../../../../../tools/parity/terminal/README.md).
