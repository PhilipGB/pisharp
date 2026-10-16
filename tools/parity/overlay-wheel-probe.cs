using System.Text;
using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

var results = new List<object>();
foreach (var id in new[] { "unhandled-editorpanel", "unhandled-extension-input", "unhandled-extension-batch", "unhandled-inline-select", "unhandled-progress", "unhandled-search", "empty-selection", "handled-selection" })
{
    using var output = new StringWriter();
    using var error = new StringWriter();
    using var screen = new TerminalScreen(output, error, () => 40, () => 12);
    screen.Output.Write(string.Join('\n', Enumerable.Range(1, 40).Select(index => "line " + index)) + "\n");
    var initialOffset = screen.TranscriptScrollOffset;
    var observedOffset = initialOffset;
    var operation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    const string wheel = "\u001b[<64;3;1M";
    var keyboard = id == "unhandled-progress" ? " " : id == "unhandled-search" ? "org/model\r" :
        id.EndsWith("select", StringComparison.Ordinal) || id is "handled-selection" or "empty-selection" ? "\r" : "typed\r";
    var prefix = id == "unhandled-extension-batch" ? "pre" : "";
    using var bytes = new ObservedInput(Encoding.UTF8.GetBytes(prefix + wheel + keyboard), Encoding.UTF8.GetByteCount(prefix + wheel), () =>
    {
        observedOffset = screen.TranscriptScrollOffset;
        operation.TrySetResult("done");
    });
    var input = new TerminalInput(bytes);
    var keymap = new EditorKeymap();
    var dialogs = new TerminalDialogs(keymap, () => input, () => screen, _ => Task.FromResult<string?>(null));
    if (id == "unhandled-progress")
    {
        var display = new TerminalProgressDisplay(keymap, () => input, () => screen, dialogs);
        var outcome = await display.RunProgressAsync("Loading", "fixture", "Stop", "Stop the operation?",
            (_, _) => operation.Task, () => Task.CompletedTask);
        if (outcome.Cancelled || outcome.Value != "done") throw new InvalidOperationException("Wheel changed progress outcome.");
    }
    else if (id == "unhandled-search")
    {
        using var http = new HttpClient(new NoNetwork());
        var selected = new TerminalHuggingFaceSearch(screen, input, keymap, new HuggingFaceClient(http, null),
            new Dictionary<string, IReadOnlyList<HuggingFaceModel>>()).Show();
        if (selected != "org/model") throw new InvalidOperationException("Wheel changed search input.");
    }
    else if (id is "unhandled-inline-select" or "handled-selection" or "empty-selection")
    {
        TerminalSelectionOption<string>[] options = [new("first", "first", "First"), new("second", "second", "Second")];
        var selected = id is "handled-selection" or "empty-selection"
            ? new TerminalOverlayHost(input).Select(screen, "Mouse-aware selection", id == "empty-selection" ? [] : options)
            : dialogs.ShowInlineSelectionList("Keyboard-only selection", options, [], "enter select");
        if (selected?.Option.Value != (id == "empty-selection" ? null : "first"))
            throw new InvalidOperationException("Wheel changed keyboard-only selection.");
    }
    else
    {
        var values = await dialogs.PromptSequenceAsync("Focused input", [("Value", null)],
            extensionDialog: id.StartsWith("unhandled-extension", StringComparison.Ordinal));
        if (values is null || !values.SequenceEqual([prefix + "typed"])) throw new InvalidOperationException("Wheel changed typed text.");
    }
    results.Add(new { id, scrolledUp = observedOffset > initialOffset });
}
Console.WriteLine("PISHARP_OVERLAY_WHEEL=" + JsonSerializer.Serialize(results));

sealed class ObservedInput(byte[] bytes, int observeAt, Action observe) : MemoryStream(bytes)
{
    public override int ReadByte()
    {
        if (Position == observeAt) observe();
        return base.ReadByte();
    }
}

sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected network request in modal wheel probe.");
}
