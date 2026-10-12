# Current Pi resource precedence and reload flows

Oracle: Pi `4270522473e677ddba7b093caa0064a3df51d459`. PiSharp product code is `65d045c5ff99e8c289b2cddb75cf6309a5879fdd`; the capture reports evidence commit `3043c4b185e594b989aab73ff7eff915f7beed8d`. The legacy-skill fixture change in this commit adds only named assertion checkpoints and does not alter CLI behavior.

All three flows pass same-Pi calibration and Pi/PiSharp comparison with no scenario errors. Normalized terminal states, render frames and provider request traces match. Raw PTY streams are not byte-identical.

| Flow | Calibration frames | Paired render differences | Verified behavior |
| --- | ---: | ---: | --- |
| `resource-skill-precedence` | 10 | 0 | Project scope wins; the POSIX-alpha exclusion is honored; invalid names remain available; collisions and warnings match; the expanded skill request and HTTP route match. |
| `extension-resource-discovery-reload` | 16 | 0 | Startup and reloaded extension skills/prompts expand identically; tool output collapses and expands; reload status and theme diagnostics match across four requests. |
| `legacy-project-skills-reload` | 14 | 0 | Completion stays disabled before reload, then shows the reloaded skill; both initial and reloaded skill bodies and arguments reach the provider across two requests. |

The legacy-skill assertion initially failed because the fixture omitted its named frames, so the assertion could not find `completion-disabled-before-reload`, `reload`, or `completion-enabled-after-reload`. Those frame IDs are now declared in `tools/parity/terminal/fixtures/legacy-project-skills-reload.json`; the recalibrated pair and behavior assertion pass. Full reports and compressed terminal captures are kept in each flow's `paired/` and `calibration/` directories. The gzip captures were losslessly recompressed at level 9 with decompressed SHA-256 verification, reducing the directory from 21.2 MiB to 9.1 MiB.
