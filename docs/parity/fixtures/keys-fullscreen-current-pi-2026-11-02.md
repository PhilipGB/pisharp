# Fullscreen transcript navigation parity — 2026-11-02

Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

This receipt covers the `keys-fullscreen` contract only: PageUp/PageDown,
Ctrl+Home/End, Ctrl+Shift+Up/Down semantic anchors, Ctrl+Shift+F search,
Enter/Shift+Enter result navigation, Escape close, streaming input precedence,
resize and terminal restoration.

## Corrections

- Added `tui.altScreen.previousPrompt` and `tui.altScreen.nextPrompt` with Pi's
  Linux defaults (Ctrl+Shift+Up/Down plus Ctrl+Up/Down) and Windows/WSL
  Ctrl+Up/Down defaults. The keybinding schema already advertised these actions;
  the runtime keymap and both input loops did not.
- Marked semantic anchors at the first rendered segment of user messages, and at
  the first non-tool assistant segment (thinking or text). This matches Pi's
  OSC 133 zone placement, where both user and assistant message starts are
  navigation anchors.
- Search now opens with an empty query, the search widget owns paging keys, the
  search shortcut toggles search closed, result counts refresh while output
  streams, and Escape restores the saved draft without aborting the run.
- Fullscreen scroll compensation now detects when Markdown re-rendering has made
  a source offset point at unrelated content during resize and falls back to the
  previous absolute row. Stable text keeps its source anchor.

## Evidence

Deterministic checks:

- `EditorKeymapTests.TranscriptPromptNavigationUsesPiDefaultsAndRemainsConfigurable`
  covers defaults, Windows/WSL aliases, user overrides and disabled actions.
- `EditorKeymapTests.StandardAndKittyPromptNavigationSequencesDecode` covers
  standard and Kitty modified arrow decoding.
- `TerminalScreenTests.PromptNavigationJumpsToPreviousAndNextUserMessages` and
  `SemanticPromptNavigationIncludesUserAndAssistantMessageStarts` cover anchor
  motion, direction and resize.
- `TerminalScreenTests.ActiveSearchTakesPrecedenceOverTranscriptPaging`,
  `ActiveSearchRefreshesResultsAsTheTranscriptStreams`,
  `ActiveSearchShortcutTogglesClosedAndCyclesBothDirections` and
  `ActiveSearchHighlightsMatchesAndRestoresItsDraftOnEscape` cover search
  precedence, live counts, toggling, cycling, draft restoration and highlight
  cleanup.
- `TerminalPtyTests.FullscreenTranscriptNavigationAndSearchThroughLinuxPty`
  drives all navigation and search keys in a real Linux PTY, checks the selected
  transcript position, and compares `stty -g` before and after an ordinary quit.
- `TerminalPtyTests.FullscreenNavigationAndSearchRemainActiveWhileStreamingThroughLinuxPty`
  holds a loopback provider stream open, exercises paging, semantic anchors,
  live search count changes, Enter/Shift+Enter, Escape close, draft preservation,
  cancellation and terminal restoration.

Pinned Pi differential:

- Same-Pi calibration: 48 canonical render frames, **0 differences**.
- Paired run: 25 named checkpoints, no scenario errors, exact HTTP match, and the
  semantic assertion passed for both products.
- The paired full-screen comparison still reports 9,094 cell/state differences,
  4,472 render differences and 4,955 control-boundary differences from unrelated
  theme, padding, scrollbar and frame-count differences. These are retained, not
  reclassified as equivalent. The assertion compares navigation-observable
  effects: first user/assistant anchor, jump-hint presence, search result count
  and search-panel presence.
- Named checkpoints cover scroll top/live tail, next/previous semantic anchors,
  resize from an assistant anchor, previous anchor after resize, resize back,
  search open/close/toggle, pasted query, Enter/Shift+Enter cycling, cursor
  movement inside search, Escape close and exit.

Commands:

```sh
python3 tools/parity/terminal/run.py --pi /home/philip/Projects/pi-pinned \
  --fixture tools/parity/terminal/fixtures/terminal-long-transcript.json \
  --case 80x24-dark-fullscreen --calibrate --semantic-only \
  --output artifacts/parity/keys-fullscreen-long-transcript-calibration6
python3 tools/parity/terminal/run.py --pi /home/philip/Projects/pi-pinned \
  --fixture tools/parity/terminal/fixtures/terminal-long-transcript.json \
  --case 80x24-dark-fullscreen --semantic-only \
  --output artifacts/parity/keys-fullscreen-long-transcript-paired7
python3 tools/parity/terminal/assert_fullscreen_navigation.py \
  artifacts/parity/keys-fullscreen-long-transcript-paired7/report.json
```

Local validation: `dotnet format --verify-no-changes` passed, the
warnings-as-errors build passed with zero warnings/errors, and the full suite
passed 4,076/4,076 with zero skips.

## Limits

The paired raw byte streams and full cell matrices are not identical, so this
receipt does not claim whole-TUI visual parity. It claims the `keys-fullscreen`
navigation/search actions have the same named observable effects as pinned Pi,
including during streaming and after resize. Raw captures remain under the
ignored `artifacts/parity/` directory; only this compact summary is committed.
