# Current-Pi trusted project-extension UI replay

Pins: Pi `4270522473e677ddba7b093caa0064a3df51d459`; PiSharp `b1cc9013fc6ac66a5947fd67347726449cc57778`.

The `120x40-dark-fullscreen` paired PTY run and independent same-Pi calibration both match all 14 normalized terminal render frames, terminal state, and HTTP traces. The project-trust extension trace is `configured`, `trust-undecided:true`, `trust-decision:true`, then `trust-ui:tui:true:Continue:true:approved`. Pi and PiSharp each make one provider request with the trusted project skill. Their normalized generated system prompts match at SHA-256 `7f4100f7c700f2dcfde0929ae2fa634d7242ada53df572e8e6f782fc0da9dd6a`; provider request bodies match after documentation install paths are normalized.

The trust selector, confirmation prompt, completed prompt, and quit checkpoints match. Both products echo and submit the same user text, and neither shows completed-run usage while work is in progress. The working indicator appears in 16 Pi frames and 3 PiSharp frames; this animation count varies with PTY scheduling. Raw PTY bytes differ because the terminal control protocols differ, and the only raw provider-request difference is the generated documentation path.

The paired capture, semantic assertions, and same-Pi calibration are stored beside this file. The Python runner applies the two-CPU and child-worker limits documented in [the terminal harness](../../../../../tools/parity/terminal/README.md).
