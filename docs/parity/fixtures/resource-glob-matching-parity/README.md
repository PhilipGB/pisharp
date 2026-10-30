# Resource path and glob matching parity (work in progress)

Pinned Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
PiSharp base: `480f058a95713fbc51b6904d4450da68bcda886c`.
Pi's lockfile pins `minimatch` `10.2.6`; the matcher oracle uses default options.

Current evidence is a partial contract run, not milestone completion:

- Glob matcher and override slice: **64/64**.
- Full-loader skills/prompts selection: **72/72**, including package, trust, and theme package cases.
- Explicit skill/prompt path cases: **19/19**.
- Full-loader local theme settings/trust cases: **36/36** across the baseline and four settings cases.
- Real Pi/PiSharp CLI process settings cases: **5/5**; user/project skills and prompts, trust/precedence,
  configured and excluded extensions, metadata, and provider inputs match. Theme settings are exercised
  through the full-loader process probe because the normal RPC CLI does not expose the selected theme.
- Extension ordering, metadata, and startup/reload context probes: all matched.
- Matcher verifier rejection controls: **7/7**.
- Focused .NET resource/theme tests: **63/63**; extension/theme/reload selection tests: **24/24**; extension catalog/reload tests: **15/15**.
- Full solution tests: **4,009/4,009**, with no failures or skips. `dotnet format PiSharp.slnx --verify-no-changes` and `dotnet build PiSharp.slnx --warnaserror` pass; build has zero warnings and errors.

Raw reports and their SHA-256 manifest are in the ignored local bundle
`artifacts/parity/resource-glob-4fd0c6ab/`. The case index and executable matcher
harness are in `tools/parity/resource_glob/`.

Still required: theme selection through the interactive CLI process, final independent E0 review,
exact-head CI, and a draft PR. Existing reload evidence includes the paired extension resource
discovery startup/reload probes and resource package reload tests; the new local settings process
cases cover selection and trust in one-shot CLI runs.
