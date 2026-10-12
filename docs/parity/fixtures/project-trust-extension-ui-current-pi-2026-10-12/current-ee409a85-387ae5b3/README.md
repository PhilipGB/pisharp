# Current-Pi trusted project-extension UI replay

Pins: Pi `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`; PiSharp `387ae5b300f187eec28c5b50b51ab77489732f31`.

The `120x40-dark-fullscreen` paired PTY run and independent same-Pi calibration both match all 14 normalized terminal render frames, terminal state, and HTTP traces. The semantic assertion also confirms the ordered pre-trust extension callbacks, the trusted project skill, editor and submitted-prompt echoes, exact provider user message, and normalized provider request. Both generated system prompts have SHA-256 `7f4100f7c700f2dcfde0929ae2fa634d7242ada53df572e8e6f782fc0da9dd6a` after documentation-root normalization.

The refreshed capture includes the startup-spacing change and confirms it does not alter the trust-dialog, completion, or exit frames. Raw PTY bytes still differ because the terminal control protocols differ; the only raw provider-request difference is the generated documentation package path. The working-indicator frame count varies with PTY scheduling.

The paired capture, semantic assertion, extension callback trace, and fresh same-Pi calibration are stored beside this file. The Python runner and its child processes are capped at two CPUs and common worker threads by [the terminal harness](../../../../../tools/parity/terminal/README.md).
