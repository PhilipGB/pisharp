# Terminal rendering and interaction

CLI owns terminal input, output, themes, layout and display state. Runtime emits
agent lifecycle events and owns conversation execution; terminal components project
those events without implementing another agent loop.

## Rendering flow

[TerminalScreen](../../src/PiSharp.Cli/Tui/TerminalScreen.cs) owns terminal modes,
console-writer installation, the screen lock, viewport/editor/panel state and frame
composition. Its public entry points preserve validation and shutdown behavior.
[InteractiveTranscript](../../src/PiSharp.Cli/Tui/InteractiveTranscript.cs) translates
lifecycle events into streamed assistant updates, committed content and tool views.

[TerminalConversationPresentation](../../src/PiSharp.Cli/Tui/TerminalConversationPresentation.cs)
owns live assistant/reasoning sources and rendered text, thinking visibility,
Markdown indentation, user/skill spacing, tool projections and image registration.
It projects into [TerminalTranscriptBuffer](../../src/PiSharp.Cli/Tui/TerminalTranscriptBuffer.cs),
which owns bounded retained segments, collapsed previews and scrollback capture.
Theme/width accessors read current screen values, including callbacks retained for
later theme reflow; they preserve the original width and palette lookup timing.
Screen mutations call presentation under the existing screen lock.

[TerminalRenderScheduler](../../src/PiSharp.Cli/Tui/TerminalRenderScheduler.cs) owns
timer lifetime, pending render state, timestamp and minimum-interval coalescing.
Its timer callback takes the same screen lock; synchronous calls already hold it.
Immediate modal rendering cancels pending work. Transcript replacement and shutdown
flush pending output before discarding state or restoring scrollback. Suspension
cancels queued rendering; resuming repaints current state immediately. Deferral flags
remain in the screen, which supplies an explicit render eligibility callback.

[TerminalScreenNotifications](../../src/PiSharp.Cli/Tui/TerminalScreenNotifications.cs)
owns captured status/kind/origin, the originating sign-in URL and clipboard feedback.
Clearing one auth origin cannot discard a newer origin's URL. Screen validates URLs
and gates clipboard feedback on active panel visibility. Hyperlink/key-hint rendering
keeps its original pre-lock timing and HTTP/HTTPS validation.

[TerminalScreenCompositor](../../src/PiSharp.Cli/Tui/TerminalScreenCompositor.cs) composes
rows and emits synchronized screen updates. [TerminalImageRenderer](../../src/PiSharp.Cli/Tui/TerminalImageRenderer.cs)
owns terminal image protocol placement and cleanup. Search/mouse/viewport components
retain navigation and selection. Screen keeps shutdown ordering visible: pending
render, timer disposal, writer restoration, image/mouse/keyboard mode cleanup,
normal-buffer document/scrollback restoration.

## Input and actions

[TerminalEditor](../../src/PiSharp.Cli/Tui/TerminalEditor.cs) uses EditorBuffer,
EditorKeymap, EditorCompletion and TerminalInput for draft editing and dispatches
application actions through callbacks composed by Program. Dialogs and progress
panels consume the same input/mode components. Editor retains ordinary draft editing,
completion/history, active-run queue/abort and application shortcut dispatch. It owns
the lazily opened console input and its terminal-response subscriptions.

[TerminalDialogs](../../src/PiSharp.Cli/Tui/TerminalDialogs.cs) owns selector/model-refresh
and prompt input loops, including redirected prompt fallback, panel preservation and
mode cleanup. [TerminalDialogPanels](../../src/PiSharp.Cli/Tui/TerminalDialogPanels.cs)
owns their concrete row, cursor and key-hint rendering. Model refresh retains its
15-second cancellation bound and shows cached choices when refresh fails.
[TerminalProgressDisplay](../../src/PiSharp.Cli/Tui/TerminalProgressDisplay.cs) owns the
16-millisecond initial-frame delay, synchronized progress updates, operation settlement,
cancel confirmation and backend/after-cancel ordering. Fast success or failure never
opens a transient progress panel; failure preserves the original exception.

[TerminalTranscriptSearchInput](../../src/PiSharp.Cli/Tui/TerminalTranscriptSearchInput.cs)
owns the saved draft/footer/cursor, remembered search query, match navigation and close
restoration. [TerminalAuthorizationInput](../../src/PiSharp.Cli/Tui/TerminalAuthorizationInput.cs)
owns sign-in mouse/key/EOF handling and MCP authorization monitoring. Input and screen
accessors use the editor's current bindings; owners do not cache an old attached screen.
Editor entry points keep application callers together, while these owners execute
their workflows and keep their state. Command workflows and active bindings
are described in [CLI composition](cli-composition.md). Provider/MCP login owns
its authentication workflow; terminal notification state only presents its URL.
These concrete components remain inside CLI; no new TUI framework, project,
interface or generic rendering bus is introduced.

## Pi constraints and evidence

Live reasoning/text, Markdown palettes/indentation, skill spacing, modal flushes,
reload deferral, alternate-screen restoration and authorization clipboard state
are parity-sensitive. Public screen/editor APIs and mode sequences are preserved.
[TerminalScreenTests](../../tests/PiSharp.Tests/TerminalScreenTests.cs) and
[InteractiveTranscriptTests](../../tests/PiSharp.Tests/InteractiveTranscriptTests.cs)
cover coalescing, suspend/resume, live reflow/commit, auth-origin protection,
scrollback, images and failure/lifetime transitions. Compositor, input, theme,
Markdown, image, selection and Linux PTY tests cover their boundaries.
[TerminalEditorProgressTests](../../tests/PiSharp.Tests/TerminalEditorProgressTests.cs)
hosts fast progress success/failure in a real Linux PTY, exercising terminal mode
ownership alongside draft/result/exception preservation and cancellation cleanup.

The [terminal differential harness](../../tools/parity/terminal/run.py) uses the
[frozen Pi reference](../parity/pi-reference.md), complete fixture matrices and
same-Pi calibration. Comparisons retain HTTP identity/count/order and normalized
cell/style/cursor/mode evidence. Historical known presentation gaps and timing
variance remain in [parity evidence](../parity/feature-matrix.md); test success proves
only the covered behavior. Fixtures and frozen semantic sets must not be weakened
when changing rendering or interaction.
