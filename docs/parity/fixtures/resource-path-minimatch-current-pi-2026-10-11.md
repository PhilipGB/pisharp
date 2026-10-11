# Resource path minimatch differential — 2026-10-11

Current Pi `origin/main` is `04566631c2b51fb688486bc1f040f60d1518c126`. `packages/coding-agent/src/core/package-manager.ts` calls the installed `minimatch` package for resource paths using the relative path, basename, and absolute path; skill files also test their parent directory. On POSIX, `toPosixPath` replaces only the platform separator, so a backslash remains a minimatch escape. The fetched oracle's installed version is minimatch 10.2.6.

PiSharp source `d8e5411be475a0f4bc3bf3458bc46bdbc8fd40f9` implements path-segment glob matching for `*`, `?`, `**`, hidden components, brace alternatives and ranges, and extglob operators. This is a verified subset of Pi's matcher, not full minimatch parity. The direct oracle was refreshed at Pi `04566631c2b51fb688486bc1f040f60d1518c126`. A resource exclusion prefixes each pattern below with `!`; a matching path is then excluded.

| Relative path | Pi minimatch pattern | Match |
|---|---|---|
| `src/file.ts` | `src/@(file|test).ts` | yes |
| `src/test.ts` | `src/+(file|test).ts` | yes |
| `src/a.ts` | `src/?(a).ts` | yes |
| `src/ab.ts` | `src/?(a).ts` | no |
| `src/aa.ts` | `src/*(a).ts` | yes |
| `src/.config.json` | `src/@(.config|readme).json` | yes |
| `src/foo.ts` | `src/@(f@(oo|oo)|bar).ts` | yes |
| `src/b.ts` | `src/@(a|[b|c]).ts` | yes |
| `src/foo.ts` | `src/!(bar).ts` | yes |
| `src/bar.ts` | `src/!(bar).ts` | no |
| `src/foobar.ts` | `src/foo!(bar).ts` | no |
| `src/foobaz.ts` | `src/foo!(bar).ts` | yes |

The cases also cover the earlier hidden-path, explicit-dot, brace-list and numeric-range vectors. PiSharp's extglob theory adds 12 fail-first cases to `LocalResourcePathRulesTests`; before that implementation, 7/10 new extglob cases failed. The current POSIX escape regression adds nine fail-first inputs (eight matched the wrong result; its non-match control passed) and a bracket-class close case. The direct current-Pi oracle returned these results:

| POSIX path | Pi minimatch pattern | Match |
| --- | --- | --- |
| `docs/a*.md` | `docs/a\*.md` | yes |
| `docs/other.md` | `docs/a\*.md` | no |
| `docs/file?.md` | `docs/file\?.md` | yes |
| `docs/[draft].md` | `docs/\[draft\].md` | yes |
| `docs/.private.md` | `docs/\.private.md` | yes |
| `docs/file{1,2}.md` | `docs/file\{1,2\}.md` | yes |
| `docs/@(one|two).md` | `docs/\@(one|two).md` | yes |
| `docs/a\name.md` | `docs/a\\name.md` | yes |
| `docs/trailing\` | `docs/trailing\` | yes |
| `docs/-.md` | `docs/[a\-c].md` | yes |
| `docs/].md` | `docs/[\]].md` | yes |

PiSharp now preserves POSIX backslashes in normalized paths and patterns, consumes escapes before glob operators and delimiters are parsed, and scans character classes without treating an escaped `]` as the close. Windows continues to normalize its native path separator. The focused `LocalResourcePathRulesTests` pass 34/34 after the change.

Format verification passes; `dotnet build PiSharp.slnx --no-restore --warnaserror` succeeds with zero warnings/errors; Node passes 24/24, Python passes 37/37, and the full .NET suite passes 1,300/1,300 with zero skips. These are local results with the POSIX escape change in the working tree. Exact-head Linux CI [38160159991](https://github.com/PhilipGB/pisharp/actions/runs/38160159991) is the preceding green run on `d8e5411be475a0f4bc3bf3458bc46bdbc8fd40f9`; CI for the changed commit remains to be verified after push.

Complete minimatch grammar and broad resource terminal/request parity remain open. The live PiSharp skill-prompt differential at Pi `04566631` / PiSharp `d8e5411` also passes advertisement, expansion and normalized request assertions in read, Bash, indirect, no-reader and reload modes; read/Bash/indirect full terminal state and all paired raw PTY streams still differ. See [the current-head skill prompt evidence](skill-prompt-current-pi-2026-10-11/current-04566631-d8e5411b/README.md).
