# Trusted project settings and resource reload

The interactive fixture changes a trusted project's `defaultTools` setting while the session is open, adds a project `SKILL.md`, sends `/reload`, and then invokes the new skill. It runs real Pi and PiSharp CLI processes in PTYs against a local streaming provider. The fixed Pi source is `f1b2e77f5b13b2a199b1052cb79c235451afe7d7`; the PiSharp capture uses `8d6205a4d5b8d4bdf111a76a5785f86ffa860666`.

Both provider traces contain two requests. The first request exposes only `read` and expands the initial project skill. The post-reload request exposes `read` and newly added `bash`, and expands the newly discovered skill with its argument. Neither process reports a scenario error. The checked behavioral summary is [summary-8d6205a4.json](project-settings-reload-current-pi-2026-10-09/summary-8d6205a4.json).

Full PTY output remains different: the paired capture records 111,927 terminal field differences, 71,800 render differences, 423 control-boundary differences, and a non-matching HTTP body trace. The raw streams, request bodies and screen state remain in the [paired report](project-settings-reload-current-pi-2026-10-09/comparison-8d6205a4/report.json) and [compressed capture](project-settings-reload-current-pi-2026-10-09/comparison-8d6205a4/120x40-dark-fullscreen.json.gz). Same-Pi calibration has no scenario errors and matching HTTP traces, with two render differences from a spinner phase; it is retained in the [calibration report](project-settings-reload-current-pi-2026-10-09/calibration-8d6205a4/report.json).

Reproduce the current captures from the checkout root:

```sh
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-parity-current-pi \
  --calibrate --fixture tools/parity/terminal/fixtures/trusted-project-settings-reload.json \
  --output docs/parity/fixtures/project-settings-reload-current-pi-2026-10-09/calibration-8d6205a4 \
  --case 120x40-dark-fullscreen
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-parity-current-pi \
  --fixture tools/parity/terminal/fixtures/trusted-project-settings-reload.json \
  --output docs/parity/fixtures/project-settings-reload-current-pi-2026-10-09/comparison-8d6205a4 \
  --case 120x40-dark-fullscreen
python3 tools/parity/terminal/assert_trusted_project_reload.py \
  --report docs/parity/fixtures/project-settings-reload-current-pi-2026-10-09/comparison-8d6205a4/report.json \
  --capture docs/parity/fixtures/project-settings-reload-current-pi-2026-10-09/comparison-8d6205a4/120x40-dark-fullscreen.json.gz \
  --calibration-report docs/parity/fixtures/project-settings-reload-current-pi-2026-10-09/calibration-8d6205a4/report.json \
  --output docs/parity/fixtures/project-settings-reload-current-pi-2026-10-09/summary-8d6205a4.json
```

This closes the scoped `/reload` check for adding default tools and discovering/invoking a project skill. Broader trust-settings behavior, extension resource discovery, exact full-screen parity and the remaining terminal capability families stay open.
