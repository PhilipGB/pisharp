# Current Pi skill prompt flows

Oracle: Pi `4270522473e677ddba7b093caa0064a3df51d459`. PiSharp's product code is `65d045c5ff99e8c289b2cddb75cf6309a5879fdd`; the captures report evidence commit `0461f41de66b8c022f2d1eb7dbcaf548e2dc7ac9`, which contains that same product tree. Exact-head Linux CI [38204612669](https://github.com/PhilipGB/pisharp/actions/runs/38204612669) passed on the evidence commit.

All three flows pass same-Pi calibration and current Pi/PiSharp comparison. Provider messages and normalized HTTP traces match, named terminal state and render comparisons report zero differences, and there are no scenario errors. Raw PTY byte streams differ, and the recorded control-boundary diagnostics reflect differences in how terminal output was split across reads.

| Mode | Fixture | Requests | Render frames | Control-boundary diagnostics |
| --- | --- | ---: | ---: | ---: |
| `read` | `resource-skill-prompt-read-bash.json` | 5 | 14 | 2,063 |
| `bash` | `resource-skill-prompt-bash.json` | 5 | 14 | 2,091 |
| `indirect` | `resource-skill-prompt-hidden-read-bash.json` | 1 | 10 | 534 |

The `read` and `bash` flows match the exact advertisement, reader hint, expansion envelope, and all five shared/project/user/temporary/unsafe skill scopes. The `indirect` flow hides both direct readers and matches the generic hint and shared-skill expansion. Each mode's `paired/` and `calibration/` directories retain the reports, named states, raw streams, and compressed render captures; `paired/skill-prompt-behavior.json` records the semantic assertions.

The gzip captures were losslessly recompressed at level 9 after collection. Decompressed SHA-256 checks passed for all 36 files, reducing this evidence directory from 90 MiB to 39 MiB without changing the captured data.
