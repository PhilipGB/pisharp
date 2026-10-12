# Quiet-startup resource and help parity

Current Pi `4270522473e677ddba7b093caa0064a3df51d459` and PiSharp `e17e759bc0ad61c8f1b93589f28788f7b250b384` were compared with `quiet-startup-resources.json` in a 120x40 dark fullscreen PTY.

The same-Pi calibration matches all seven captured render frames, terminal state, normalized and raw HTTP traces, and raw bytes, with no control-boundary differences. In the paired run, normalized and raw HTTP traces match and neither process reports a scenario error. The full terminal still differs: 67,022 state/render fields and 41,196 render fields across seven frames; raw PTY bytes differ, with 454 control-boundary differences. The paired screen residual remains open.

The extracted Context, Skills, and Prompts rows match exactly in compact `startup` and expanded `expand-startup` states. Expanded user resources retain Pi scope grouping, indentation, and the `~/skills/startup-skill/SKILL.md` path. The untrusted project skill stays hidden; the project `AGENTS.md` context file remains visible. [`resource-projection.json`](resource-projection.json) records both projections.

Expanded startup help now lists the supported interrupt, clear/exit, delete-to-end, model-cycle, thinking-level, tool-expansion, external-editor, Bash/no-context Bash, follow-up, queued-message, paste, and file-drop actions. A Linux PTY regression verifies that two `Ctrl+C` presses within Pi's 500 ms window exit normally; the test failed before the implementation change. Current Pi's extra startup controls and the remaining header/help wording and layout differences are still unresolved.

The paired and same-Pi captures retain terminal state, render frames, HTTP requests, raw PTY data, and control diagnostics. Local format verification, a warnings-as-errors build, Node 24/24, Python 37/37, and .NET 1,337/1,337 with zero skips pass. Exact-head Linux CI run [38198167393](https://github.com/PhilipGB/pisharp/actions/runs/38198167393) passes on the PiSharp SHA above.
