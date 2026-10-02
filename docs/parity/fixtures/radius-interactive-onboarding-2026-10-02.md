# Radius interactive onboarding — 2026-10-02

Oracle: `earendil-works/pi@b271b0a524b29e13c0c9e748aea0d34e1597f2db`. The queued change is `ed8b3bcc194c8263ec8bec3f337053ae73866da1`. This records a bounded implementation slice; full authentication and terminal parity remain in progress.

## Implemented behavior

`/login` opens the authentication menu with account, API-key and direct Radius choices. Radius account entries are distinct from subscription entries. The Radius row reflects stored authentication and animates while selected. Existing explicit provider/type arguments remain supported. Canceling OAuth restores the menu that initiated the flow; explicit Radius login restores the editor. An outstanding device-token request responds to the configured `app.interrupt` binding.

After Radius OAuth succeeds, the global MCP offer uses the constant `https://radius.pi.dev/mcp`, independently of the OAuth gateway override. It reuses the first matching global HTTP server, preserves its options, replaces `auth` with the Radius provider, removes `oauth`, and chooses `radius`/`radius-mcp` for a missing server. Project MCP configuration does not suppress this global offer. Already configured global provider authentication skips it. Accepting saves and invokes the existing runtime reload; declining or canceling does neither. Only configuration-write failures use the configuration error message; reload failures retain the existing command error path.

`Program` only composes `TerminalProviderLogin`. OAuth exchanges and credential storage still use the existing provider runtime; MCP writes use the existing editor. The shared MAF execution path is unchanged.

## Evidence and limits

The initial PTY menu test failed against source `4595bc9f4f2db98e45adf98b8ece2e00bbcfc0d1`: `/login` entered the local API-key prompt and offered no authentication menu. Two later fail-first shimmer tests reproduced fractional 256-color mismatches at 123 ms and 777 ms. Keeping floating-point channels through palette quantization resolves those differences. A subsequent real-terminal animation test failed because overlay sanitization stripped application-generated escapes, then failed again because clipping counted ANSI bytes as visible cells. The overlay now receives already sanitized list output with application styling intact; clipping preserves complete escapes and counts display cells. Existing untrusted-label sanitization checks still pass.

The final focused provider-login/Radius/menu/selection/theme/text-layout run passes 59/59 with zero skips. Seven real CLI PTY scenarios at 120 columns by 40 rows use a loopback Radius device-code server: MCP Yes/No/cancel; OAuth-method cancellation from menu/direct login; and pending-token cancellation with Escape/custom Ctrl+Q. They assert stored credentials, configuration preservation, return navigation and successful process exit without exposing fixture tokens. The three MCP choices change settings on disk during the offer, then inspect the terminal settings picker: only Yes reloads the runtime snapshot. Successful sign-in also changes the login menu to the configured state. Two additional real terminal menu tests verify the initial menu and capture changing truecolor shimmer frames; deselecting Radius removes the shimmer.

The [MCP paired fixture](radius-mcp-onboarding-2026-10-02.json) compares ten offer/configuration outcomes using Pi's actual interactive MCP offer and PiSharp's configuration feature. It compares whole global JSON without normalization. Runtime reload and OAuth exchange are covered by the separate PTY tests, not inferred from a synthetic callback count.

The [shimmer paired fixture](radius-login-shimmer-2026-10-02.json) compares 26 exact selected-label ANSI substrings from Pi's actual menu at controlled times in truecolor and 256-color modes. It retains text, color sequences and foreground resets. A regression rerun also matches all 1,008 system-theme ANSI prefixes across 18 palette/mode combinations at this oracle. Current Pi Radius OAuth/provider suites pass 8/8.

These are semantic PTY checks and exact animated-label checks. They do not establish equal complete terminal cell matrices, dialog placement, padding, borders, status styling, cursor coordinates or terminal-control behavior. Authentication dialog presentation, URL copying, post-login catalog synchronization/model selection, success wording and Pi credential-file interoperability remain open in the broader authentication ledger. Radius is not marked fully matched.

## Local and publication checks

The first full run passed 1131 tests and failed one legacy unsupported-OAuth wording assertion. The existing login/secret-isolation scenario was extracted from ProviderModelRuntimeTests into ProviderLoginTuiTests and now checks the unsupported-adapter diagnostic. Focused coverage passes after this correction.

Full solution format verification passes. The final fixture-isolation edit also passes a focused format check. The warnings-as-errors build passes with zero warnings/errors. The final full serial suite passes 1132/1132 with zero skips in 53 seconds. Publication and exact-head CI are pending. The last published checkpoint is `4595bc9f4f2db98e45adf98b8ece2e00bbcfc0d1`, with exact-head Linux CI [36997235947](https://github.com/PhilipGB/pisharp/actions/runs/36997235947) passing all 1114 tests and zero skips.
