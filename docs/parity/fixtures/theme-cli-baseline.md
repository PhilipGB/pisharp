# Current-Pi theme CLI baseline

Current Pi `main` was checked on 2026-09-28 at `e1787702d32e0a7fd7387840c918faa4d1286187`. `packages/coding-agent/src/cli/args.ts`, `main.ts`, `core/resource-loader.ts`, `docs/cli.md`, and tests in `test/args.test.ts` and `test/resource-loader-theme.test.ts` define the behavior.

- `--theme <path>` is repeatable and loads a theme file or directory relative to the invocation working directory.
- `--use-theme <name[/name]>` sets an initial interactive theme for this run.
- `--no-themes` disables discovered and configured themes while retaining built-ins and explicitly supplied `--theme` paths.
- The startup theme override is scoped to interactive mode; selecting a theme during the run replaces the current override.

PiSharp parses the three flags in `CliArguments`. `TerminalThemeCatalog` recursively loads explicit files/directories from the invocation directory and suppresses configured/user/project discovery when `NoThemes` is set. `Program.cs` applies `UseTheme` only when an interactive editor exists, preserves it through theme refresh, and lets an explicitly saved theme setting replace it. The settings picker reports the active startup override while it is in effect.

`CliThemeArgumentsTests` covers repeatable paths and required values. `TerminalThemeTests.ExplicitThemePathsLoadRelativeToInvocationDirectoryEvenWhenDiscoveryIsDisabled` covers nested directories, built-in/system retention, and discovery suppression. `CliThemeProcessTests.ExplicitThemePathAndUseThemeApplyThroughLinuxPtyWithNoThemes` starts the actual CLI in a Linux PTY and observes a unique custom footer color from the explicitly loaded theme. The help process test checks the displayed flags. Exact-head Linux CI run [36420934092](https://github.com/PhilipGB/pisharp/actions/runs/36420934092) for `c68ad5b8f51d9184ef4c69bedf858d2617291d43` passed restore, format, warnings-as-errors build, and 729/729 tests (0 skipped).

This closes the CLI theme flags gap while broader theme rendering remains in progress. Custom HTML-export theme CSS and full upstream process/theme differentials remain open.
