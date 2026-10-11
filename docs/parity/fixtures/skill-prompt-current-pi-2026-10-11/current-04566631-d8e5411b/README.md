# Current-head skill prompt differential — 2026-10-11

Oracle: Pi `04566631c2b51fb688486bc1f040f60d1518c126` (`origin/main`). Product: PiSharp `d8e5411be475a0f4bc3bf3458bc46bdbc8fd40f9` (`origin/main`). The Pi changes after `97195ec3` do not touch the skill loader, system prompt, package-manager resource filtering, or their tests. This rechecks the latest PiSharp skill-prompt implementation against the fetched Pi head.

The five paired fixtures and their same-Pi calibrations are retained beside this report. The checked-in `assert_skill_prompt_parity.py` was rerun against each capture. Every calibration matched exactly, every paired provider-message trace matched after only generated documentation install paths were canonicalized, every normalized request comparison had zero differences, and no scenario raised an error.

| Mode | Verified behavior | Requests | Full paired terminal |
| --- | --- | ---: | --- |
| `read` | Exact skill advertisement and read hint; project, user, temporary and shared scopes; XML escaping for `unsafe&skill`; exact location/reference/body/chomping/argument envelope. | 5 | Difference remains |
| `bash` | Exact advertisement and Bash hint; all five skill scopes and expansion envelopes match. | 5 | Difference remains |
| `indirect` | Both direct readers hidden; generic hint matches and the manually invoked shared skill expands identically. | 1 | Difference remains |
| `none` | No `<skills>` advertisement without a valid reader; manual `/skill:name` expansions still match. | 5 | Exact match |
| `reload` | Autocomplete disabled but manual invocation works; `/reload` replaces advertised metadata and both initial/reloaded expansions match. | 2 | Exact match |

The read, Bash and indirect captures report `fullTerminalMatch=false` despite zero normalized render differences; these state/control differences remain open. Raw PTY bytes differ in every paired fixture. The no-reader and reload captures report exact terminal matches. No terminal or byte differences were normalized away by this skill assertion.

Exact files: each mode has `*-paired/report.json`, `*-paired/skill-prompt-behavior.json`, `*-calibration/report.json`, and compressed PTY/frame/render captures. Reproduce the semantic assertions with `tools/parity/terminal/assert_skill_prompt_parity.py`, passing the mode's paired directory and calibration `report.json`.
