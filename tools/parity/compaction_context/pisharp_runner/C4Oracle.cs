using System.Reflection;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Runtime.Sessions;

internal static class C4Oracle
{
    private static readonly Type s_estimator = typeof(ConversationSession).Assembly.GetType(
        "PiSharp.Runtime.Sessions.PiContextUsageEstimator", throwOnError: true)!;
    private static readonly MethodInfo s_estimate = s_estimator.GetMethod("Estimate", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo s_estimateAfterUsage = s_estimator.GetMethod("EstimateAfterUsage", BindingFlags.Public | BindingFlags.Static)!;

    public static object Run(string cwd)
    {
        return new
        {
            cases = new Dictionary<string, object>
            {
                ["C4.model-context-window-controls-trigger"] = ModelContextWindowTrigger(),
                ["C4.reserve-and-keep-recent-defaults"] = Defaults(),
                ["C4.provider-model-override-precedence"] = ProviderModelOverrides(),
                ["C4.unknown-limit-is-never-invented"] = UnknownLimit(),
                ["C4.provider-usage-anchors-current-context"] = ProviderUsageAnchor(cwd),
                ["C4.aborted-error-and-zero-usage-are-not-anchors"] = InvalidUsageAnchors(cwd),
                ["C4.messages-after-usage-are-estimated"] = MessagesAfterUsage(cwd),
                ["C4.context-edit-and-compaction-invalidate-stale-usage"] = InvalidatedUsage(cwd),
                ["C4.cache-read-and-write-token-fallback"] = CacheFallback(cwd),
                ["C4.system-prompt-and-tool-declaration-estimate"] = SystemAndToolsEstimate(),
                ["C4.image-and-large-tool-result-estimate"] = ImageAndToolResultEstimate(),
                ["C4.estimate-method-is-explicitly-heuristic-not-tokenizer"] = HeuristicEstimate(),
            }
        };
    }

    private static object ModelContextWindowTrigger()
    {
        const int window = 100_000;
        const int reserve = 16_384;
        var policy = new AutoCompactionPolicy(window, reserve);
        return new
        {
            triggerTokens = policy.TriggerTokens,
            atThreshold = 83_616 > policy.TriggerTokens,
            oneAboveThreshold = 83_617 > policy.TriggerTokens,
            belowThreshold = 80_000 > policy.TriggerTokens,
        };
    }

    private static object Defaults()
    {
        var resolved = new CompactionSettings().Resolve(100_000, _ => null);
        return new
        {
            enabled = resolved?.ContextWindowTokens is > 0,
            contextWindowTokens = resolved?.ContextWindowTokens,
            reserveTokens = resolved?.ReserveTokens,
            keepRecentTokens = resolved?.KeepRecentTokens,
            defaultReserveTokens = AutoCompactionPolicy.DefaultReserveTokens,
            defaultKeepRecentTokens = CompactionSettings.DefaultKeepRecentTokens,
        };
    }

    private static object ProviderModelOverrides()
    {
        var settings = new CompactionSettings(ReserveTokens: 8192, KeepRecentTokens: 10_000,
            ModelOverrides: new Dictionary<string, CompactionSettings>(StringComparer.Ordinal)
            {
                ["provider/family/model"] = new(ReserveTokens: 400_000),
            });
        var selected = settings.Resolve(1_000_000, _ => null, "provider/family/model");
        var fallback = settings.Resolve(1_000_000, _ => null, "provider/other");
        return new
        {
            selectedReserveTokens = selected?.ReserveTokens,
            selectedKeepRecentTokens = selected?.KeepRecentTokens,
            fallbackReserveTokens = fallback?.ReserveTokens,
            fallbackKeepRecentTokens = fallback?.KeepRecentTokens,
        };
    }

    private static object UnknownLimit()
    {
        var settings = new CompactionSettings();
        var policy = settings.Resolve(null, _ => null);
        return new
        {
            contextWindowKnown = policy is not null,
            pisharpTriggerInvented = policy is not null,
            reserveTokens = policy?.ReserveTokens ?? AutoCompactionPolicy.DefaultReserveTokens,
        };
    }

    private static object ProviderUsageAnchor(string cwd)
    {
        var session = new ConversationSession(cwd, "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "previous request"));
        session.AppendUsage(new UsageRecord("model", "model", 10, 3, 0, 0, 13, null));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "pending"));
        var usageTokens = session.LatestContextUsageTokens();
        var projection = Projection(session);
        var trailing = ReadProperty<ChatMessage[]>(projection, "TrailingMessages");
        var trailingTokens = EstimateAfterUsage(0, trailing);
        return new
        {
            hasValidUsageAnchor = usageTokens is not null,
            usageTokens,
            trailingTokens,
            totalTokens = usageTokens.GetValueOrDefault() + trailingTokens,
        };
    }

    private static object InvalidUsageAnchors(string cwd)
    {
        var session = new ConversationSession(cwd, "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.AppendUsage(new UsageRecord("model", "model", 500, 0, 0, 0, 500, null));
        session.Append(new ChatMessage(ChatRole.Assistant, "failed")
        {
            AdditionalProperties = new() { ["pisharp.stopReason"] = "error" }
        });
        session.AppendUsage(new UsageRecord("model", "model", 0, 0, 0, 0, 0, null));
        session.Append(new ChatMessage(ChatRole.Assistant, "zero usage"));
        var usageTokens = session.LatestContextUsageTokens();
        var context = session.ContextMessages();
        var estimate = EstimateAfterUsage(0, context);
        return new
        {
            hasValidUsageAnchor = usageTokens is not null,
            usageTokens,
            estimateFromVisibleMessages = estimate,
            visibleMessages = context.Select(message => new
            {
                role = message.Role.Value,
                text = message.Text,
                tokenEstimate = EstimateAfterUsage(0, [message]),
                contentTypes = message.Contents.Select(content => content.GetType().Name),
            }),
        };
    }

    private static object MessagesAfterUsage(string cwd)
    {
        var session = new ConversationSession(cwd, "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.AppendUsage(new UsageRecord("model", "model", 90, 10, 0, 0, 100, null));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        session.Append(new ChatMessage(ChatRole.User, "pending one"));
        session.Append(new ChatMessage(ChatRole.User, "pending two with more text"));
        var usageTokens = session.LatestContextUsageTokens() ?? 0;
        var projection = Projection(session);
        var trailing = ReadProperty<ChatMessage[]>(projection, "TrailingMessages");
        var trailingTokens = EstimateAfterUsage(0, trailing);
        return new { usageTokens, trailingTokens, totalTokens = usageTokens + trailingTokens };
    }

    private static object InvalidatedUsage(string cwd)
    {
        var edited = new ConversationSession(cwd, "fixture", null);
        edited.Append(new ChatMessage(ChatRole.User, "request"));
        edited.AppendUsage(new UsageRecord("model", "model", 10, 3, 0, 0, 13, null));
        edited.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        var assistantId = edited.Tree.HeadId!;
        edited.Tree.Append("context_edit", System.Text.Json.JsonSerializer.SerializeToElement(
            new { targetId = assistantId, replacement = (string?)null }));
        var editProjection = Projection(edited);

        var compacted = new ConversationSession(cwd, "fixture", null);
        compacted.Append(new ChatMessage(ChatRole.User, "old request"));
        compacted.AppendUsage(new UsageRecord("model", "model", 10, 3, 0, 0, 13, null));
        compacted.Append(new ChatMessage(ChatRole.Assistant, "old answer"));
        compacted.Append(new ChatMessage(ChatRole.User, "current request"));
        compacted.Append(new ChatMessage(ChatRole.Assistant, "current answer"));
        var plan = compacted.PrepareCompaction(1) ?? throw new InvalidOperationException("compaction fixture plan missing");
        compacted.AppendCompaction(plan, "current summary", 1);
        var compactionProjection = Projection(compacted);
        return new
        {
            contextEditInvalidatesUsage = edited.LatestContextUsageTokens() is null &&
                ReadProperty<bool>(editProjection, "IsInvalidated"),
            compactionInvalidatesUsage = compacted.LatestContextUsageTokens() is null &&
                ReadProperty<bool>(compactionProjection, "HasCompaction"),
        };
    }

    private static object CacheFallback(string cwd)
    {
        var session = new ConversationSession(cwd, "fixture", null);
        session.Append(new ChatMessage(ChatRole.User, "request"));
        session.AppendUsage(new UsageRecord("model", "model", 7, 3, 20, 0, 0, null, CachedWriteTokens: 4));
        session.Append(new ChatMessage(ChatRole.Assistant, "answer"));
        var usageTokens = session.LatestContextUsageTokens();
        return new { usageTokens, totalTokens = usageTokens.GetValueOrDefault() };
    }

    private static object SystemAndToolsEstimate()
    {
        using var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""");
        var tool = AIFunctionFactory.CreateDeclaration("custom", "fixture", schema.RootElement.Clone());
        const string prompt = "fixture system prompt characters";
        var messages = new[] { new ChatMessage(ChatRole.User, "user context") };
        var estimate = Estimate(prompt.Length, [tool], messages);
        return new { estimate, systemPromptCharacters = prompt.Length, toolCount = 1, messageCount = messages.Length };
    }

    private static object ImageAndToolResultEstimate()
    {
        var image = new ChatMessage(ChatRole.User,
            [new DataContent(ReadOnlyMemory<byte>.Empty, "image/png"), new TextContent("image marker")]);
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("large", new string('x', 8000))]);
        return new
        {
            estimate = EstimateAfterUsage(0, [image, result]),
            imageEstimate = EstimateAfterUsage(0, [image]),
            resultEstimate = EstimateAfterUsage(0, [result]),
            imageCharacters = 4800,
            toolResultCharacters = 8000,
        };
    }

    private static object HeuristicEstimate()
    {
        var text = string.Concat(Enumerable.Repeat("π🦊é", 25));
        var estimate = EstimateAfterUsage(0, [new ChatMessage(ChatRole.User, text)]);
        return new { estimate, expectedCharacterHeuristic = (text.Length + 3) / 4, tokenizerOutput = false };
    }

    private static int Estimate(int systemPromptCharacters, IEnumerable<AITool> tools, IEnumerable<ChatMessage> messages) =>
        (int)s_estimate.Invoke(null, [systemPromptCharacters, tools, messages])!;

    private static int EstimateAfterUsage(long usageTokens, IEnumerable<ChatMessage> messages) =>
        (int)s_estimateAfterUsage.Invoke(null, [usageTokens, messages])!;

    private static object Projection(ConversationSession session) => typeof(ConversationSession)
        .GetMethod("ProjectContextUsageForEstimate", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(session, null)!;

    private static T ReadProperty<T>(object instance, string name) =>
        (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
}
