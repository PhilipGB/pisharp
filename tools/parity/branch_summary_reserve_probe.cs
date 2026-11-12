using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

internal static class BranchSummaryReserveProbe
{
    internal static readonly string[] Markers = ["OLDEST-MARKER", "MIDDLE-MARKER", "RECENT-MARKER", "SUMMARY-MARKER", "COMPACTION-MARKER"];

    internal static async Task<object> ObserveAsync(JsonElement item)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-reserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var result = new Dictionary<string, object?> { ["id"] = item.GetProperty("id").GetString() };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), item.GetProperty("userRaw").GetString());
            var user = await UserSettings.LoadAsync(root, _ => null);
            Directory.CreateDirectory(Path.Combine(root, ".pi"));
            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"), item.GetProperty("projectRaw").GetString());
            var project = item.GetProperty("projectTrusted").GetBoolean()
                ? await UserSettings.LoadProjectAsync(root) : new UserSettings();
            var settings = user.Overlay(project);
            var branch = settings.EffectiveBranchSummary;
            var reserve = Convert.ToDouble(branch.EffectiveReserveTokens, CultureInfo.InvariantCulture);
            // Reflection keeps the identical probe executable at the fail-first base and implementation.
            var stored = branch.GetType().GetProperty("RawReserveTokens")?.GetValue(branch);
            var raw = stored?.GetType().GetProperty("Raw")?.GetValue(stored) as string;
            raw ??= branch.ReserveTokens?.ToString(CultureInfo.InvariantCulture);
            var context = item.GetProperty("contextWindow").GetInt32();
            if (context == 0) context = 128000;
            var budget = context - reserve;
            result["loaded"] = true;
            result["parentPresent"] = settings.BranchSummary is not null;
            result["reservePresent"] = raw is not null;
            result["raw"] = raw ?? "<absent>";
            result["effective"] = Number(reserve);
            result["contextWindow"] = context;
            result["budget"] = Number(budget);
            result["limiting"] = budget > 0;
            result["skips"] = branch.SkipsSummaryPrompt;

            var conversation = new ConversationSession(root, "reserve-fixture", null);
            void User(string text) => conversation.Append(new ChatMessage(ChatRole.User, text));
            void Assistant(string text) => conversation.Append(new ChatMessage(ChatRole.Assistant, text));
            User("SHARED");
            var common = conversation.Tree.HeadId;
            User("OLDEST-MARKER".PadRight(40, 'x'));
            var kind = item.GetProperty("history").GetString();
            if (kind == "files")
                conversation.Append(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("old-read", "read", new Dictionary<string, object?> { ["path"] = "old.txt" })]));
            if (kind == "summary")
                conversation.AppendBranchSummary("nested-source", "SUMMARY-MARKER".PadRight(160, 'x'),
                    JsonSerializer.SerializeToElement(new { readFiles = new[] { "nested.txt" }, modifiedFiles = Array.Empty<string>() }));
            else if (kind == "compaction")
                conversation.Tree.Append("compaction", JsonSerializer.SerializeToElement(new { summary = "COMPACTION-MARKER".PadRight(160, 'x'), tokensBefore = 100, firstKeptEntryId = conversation.Tree.HeadId }));
            else Assistant("MIDDLE-MARKER".PadRight(40, 'x'));
            User("RECENT-MARKER".PadRight(40, 'x'));
            var old = conversation.Tree.HeadId;
            var entries = conversation.Tree.ActivePath().Skip(1).ToArray();
            conversation.Tree.Select(common);
            Assistant("TARGET");
            var target = conversation.Tree.HeadId;
            conversation.Tree.Select(old);
            var prepare = typeof(ConversationRun).Assembly.GetType("PiSharp.Runtime.Sessions.BranchSummaryPreparation")!
                .GetMethod("Prepare")!;
            var budgetArgument = prepare.GetParameters()[1].ParameterType == typeof(int) ? (object)(int)Math.Max(0, budget) : budget;
            var prepared = prepare.Invoke(null, [entries, budgetArgument])!;
            var preparedMessages = (IReadOnlyList<ChatMessage>)prepared.GetType().GetProperty("Messages")!.GetValue(prepared)!;
            result["preparedMarkers"] = Project(string.Join("\n", preparedMessages.Select(x => x.Text)));
            result["preparedCount"] = preparedMessages.Count;
            using var client = new CaptureClient();
            var run = await ConversationRun.OpenAsync(new PiAgent(client, new CodingTools(root), noTools: true,
                maxOutputTokens: item.GetProperty("maxTokens").GetInt32()), conversation,
                autoCompaction: new AutoCompactionPolicy(context));
            var options = new ConversationTreeSelectionOptions(Summarize: !branch.SkipsSummaryPrompt,
                CustomInstructions: item.TryGetProperty("customInstructions", out var custom) ? custom.GetString() : null);
            if (options.GetType().GetProperty("ConfiguredReserveTokens") is { } configured)
                configured.SetValue(options, reserve);
            else options = options with { ReserveTokens = (int)reserve };
            await run.NavigateTreeAsync(target, options);
            var summary = conversation.Tree.Entries.LastOrDefault(x => x.Type == "branch_summary" &&
                x.Payload.GetProperty("fromId").GetString() == old);
            var request = client.Requests.FirstOrDefault() ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(request, @"<conversation>\n([\s\S]*?)\n</conversation>");
            var transcript = match.Success ? match.Groups[1].Value : "";
            result["markers"] = Project(transcript);
            result["providerCalls"] = client.Requests.Count;
            result["outputCaps"] = client.Caps;
            result["conversation"] = transcript;
            result["summaryCreated"] = summary is not null;
            result["targetSelected"] = summary is not null ? summary.ParentId == target : conversation.Tree.HeadId == target;
            result["emptySummary"] = summary?.Payload.GetProperty("summary").GetString() == "No content to summarize";
            result["readFiles"] = summary?.Payload.GetProperty("details").GetProperty("readFiles").EnumerateArray().Select(x => x.GetString()).ToArray() ?? [];
            result["modifiedFiles"] = summary?.Payload.GetProperty("details").GetProperty("modifiedFiles").EnumerateArray().Select(x => x.GetString()).ToArray() ?? [];
            result["customPrompt"] = request.Contains("Additional focus: CUSTOM BRANCH FOCUS", StringComparison.Ordinal);
            result["error"] = null;
        }
        catch (Exception exception)
        {
            result.TryAdd("loaded", false);
            result["error"] = exception.GetType().Name + ": " + exception.Message;
        }
        finally { Directory.Delete(root, recursive: true); }
        return result;
    }

    private static object Number(double value) => double.IsFinite(value) ? value : value.ToString(CultureInfo.InvariantCulture);
    private static string[] Project(string text) => Markers.Where(x => text.Contains(x, StringComparison.Ordinal)).ToArray();

    private sealed class CaptureClient : IChatClient
    {
        public List<string> Requests { get; } = [];
        public List<int?> Caps { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(string.Join("\n", messages.Select(x => x.Text)));
            Caps.Add(options?.MaxOutputTokens);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixture summary")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
