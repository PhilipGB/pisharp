using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Core.Settings;

/// <summary>
/// The resolved Pi settings document (global merged with the trusted project scope).
/// Property names mirror the pinned Pi <c>Settings</c> interface in
/// <c>packages/coding-agent/src/core/settings-manager.ts</c>; absent values are null and
/// resolve to Pi defaults through <see cref="SettingsManager"/> getters.
/// </summary>
public sealed class PiSettings
{
    /// <summary>Version of the changelog already shown to the user.</summary>
    public string? LastChangelogVersion { get; set; }

    /// <summary>Default provider identifier used when no model flag is given.</summary>
    public string? DefaultProvider { get; set; }

    /// <summary>Default model id used when no model flag is given.</summary>
    public string? DefaultModel { get; set; }

    /// <summary>Default thinking level (off/minimal/low/medium/high/xhigh/max).</summary>
    public string? DefaultThinkingLevel { get; set; }

    /// <summary>Per-model default thinking level overrides keyed by "provider/modelId".</summary>
    public Dictionary<string, string>? ModelThinkingLevels { get; set; }

    /// <summary>Preferred transport (auto/sse/websocket).</summary>
    public string? Transport { get; set; }

    /// <summary>How queued steering messages are drained (all/one-at-a-time).</summary>
    public string? SteeringMode { get; set; }

    /// <summary>How queued follow-up messages are drained (all/one-at-a-time).</summary>
    public string? FollowUpMode { get; set; }

    /// <summary>Theme name or "auto/<terminal-theme>" selection.</summary>
    public string? Theme { get; set; }

    /// <summary>Compaction budgets and per-model overrides.</summary>
    public PiCompactionSettings? Compaction { get; set; }

    /// <summary>Branch summary budgets and prompt behaviour.</summary>
    public PiBranchSummarySettings? BranchSummary { get; set; }

    /// <summary>Retry budgets for agent turns and provider requests.</summary>
    public PiRetrySettings? Retry { get; set; }

    /// <summary>Hide thinking/reasoning blocks in the transcript.</summary>
    public bool? HideThinkingBlock { get; set; }

    /// <summary>Show cache cost and provider recovery notices.</summary>
    public bool? ShowCacheMissNotices { get; set; }

    /// <summary>Command for the external editor; takes precedence over VISUAL/EDITOR.</summary>
    public string? ExternalEditor { get; set; }

    /// <summary>Custom shell path; supports a leading ~.</summary>
    public string? ShellPath { get; set; }

    /// <summary>Suppress startup banner output.</summary>
    public bool? QuietStartup { get; set; }

    /// <summary>Default project trust decision (ask/always/never); global scope only.</summary>
    public string? DefaultProjectTrust { get; set; }

    /// <summary>Prefix prepended to every shell command.</summary>
    public string? ShellCommandPrefix { get; set; }

    /// <summary>argv-style command used for npm lookup/install operations.</summary>
    public string[]? NpmCommand { get; set; }

    /// <summary>Show a condensed changelog after an update.</summary>
    public bool? CollapseChangelog { get; set; }

    /// <summary>Anonymous version/update ping after changelog-detected updates.</summary>
    public bool? EnableInstallTelemetry { get; set; }

    /// <summary>Opt-in analytics data sharing.</summary>
    public bool? EnableAnalytics { get; set; }

    /// <summary>Analytics tracking identifier generated on first opt-in.</summary>
    public string? TrackingId { get; set; }

    /// <summary>npm/git package sources (string or filtered object form).</summary>
    public PiPackageSource[]? Packages { get; set; }

    /// <summary>Local extension file paths or directories.</summary>
    public string[]? Extensions { get; set; }

    /// <summary>Local skill file paths or directories.</summary>
    public string[]? Skills { get; set; }

    /// <summary>Local prompt template paths or directories.</summary>
    public string[]? Prompts { get; set; }

    /// <summary>Local theme file paths or directories.</summary>
    public string[]? Themes { get; set; }

    /// <summary>Register skills as /skill:name commands.</summary>
    public bool? EnableSkillCommands { get; set; }

    /// <summary>Terminal rendering options.</summary>
    public PiTerminalSettings? Terminal { get; set; }

    /// <summary>Image handling options.</summary>
    public PiImageSettings? Images { get; set; }

    /// <summary>Model patterns enabled for cycling (same format as the --models flag).</summary>
    public string[]? EnabledModels { get; set; }

    /// <summary>Initial built-in tool selection.</summary>
    public string[]? DefaultTools { get; set; }

    /// <summary>Action for double-escape with an empty editor (fork/tree/none).</summary>
    public string? DoubleEscapeAction { get; set; }

    /// <summary>Default filter when opening the tree selector.</summary>
    public string? TreeFilterMode { get; set; }

    /// <summary>Custom token budgets for thinking levels.</summary>
    public PiThinkingBudgetsSettings? ThinkingBudgets { get; set; }

    /// <summary>Horizontal padding for the input editor.</summary>
    public int? EditorPaddingX { get; set; }

    /// <summary>Horizontal padding for chat message output (0 or 1).</summary>
    public int? OutputPad { get; set; }

    /// <summary>Max visible items in the autocomplete dropdown.</summary>
    public int? AutocompleteMaxVisible { get; set; }

    /// <summary>Show the terminal hardware cursor while positioning it for IME.</summary>
    public bool? ShowHardwareCursor { get; set; }

    /// <summary>Markdown rendering options.</summary>
    public PiMarkdownSettings? Markdown { get; set; }

    /// <summary>Warning behaviour options.</summary>
    public PiWarningSettings? Warnings { get; set; }

    /// <summary>Custom session storage directory.</summary>
    public string? SessionDir { get; set; }

    /// <summary>Proxy URL applied to managed HTTP clients.</summary>
    public string? HttpProxy { get; set; }

    /// <summary>
    /// HTTP header/body idle timeout in milliseconds (number) or "disabled" (string); 0
    /// disables it. Typed as JsonElement to mirror Pi's string-or-number acceptance.
    /// </summary>
    public JsonElement? HttpIdleTimeoutMs { get; set; }

    /// <summary>WebSocket connect/open handshake timeout in milliseconds; 0 disables it.</summary>
    public JsonElement? WebsocketConnectTimeoutMs { get; set; }

    /// <summary>TUI mode (regular/fullscreen).</summary>
    public string? TuiMode { get; set; }

    /// <summary>What fullscreen exit prints (transcript/resume-hint).</summary>
    public string? FullscreenExitOutput { get; set; }

    /// <summary>Scrollbar policy in fullscreen mode.</summary>
    public string? FullscreenScrollbar { get; set; }

    /// <summary>Copy selection on confirm in fullscreen mode.</summary>
    public bool? FullscreenCopyOnSelect { get; set; }
}

/// <summary>Compaction budgets; per-model overrides use exact "provider/modelId" keys.</summary>
public sealed class PiCompactionSettings
{
    /// <summary>Enable automatic compaction (default true).</summary>
    public bool? Enabled { get; set; }

    /// <summary>Tokens reserved for the summarisation prompt and response (default 16384).</summary>
    public long? ReserveTokens { get; set; }

    /// <summary>Approximate recent tokens retained after compaction (default 20000).</summary>
    public long? KeepRecentTokens { get; set; }

    /// <summary>Per-model overrides keyed by exact "provider/modelId".</summary>
    public Dictionary<string, PiCompactionModelOverride>? ModelOverrides { get; set; }
}

/// <summary>Per-model compaction token budget override.</summary>
public sealed class PiCompactionModelOverride
{
    /// <summary>Per-model reserve token budget.</summary>
    public long? ReserveTokens { get; set; }

    /// <summary>Per-model keep-recent token budget.</summary>
    public long? KeepRecentTokens { get; set; }
}

/// <summary>Branch summary budgets.</summary>
public sealed class PiBranchSummarySettings
{
    /// <summary>Tokens reserved for prompt + LLM response (default 16384).</summary>
    public long? ReserveTokens { get; set; }

    /// <summary>Skip the "Summarize branch?" prompt and default to no summary.</summary>
    public bool? SkipPrompt { get; set; }
}

/// <summary>Retry budgets for agent turns and provider requests.</summary>
public sealed class PiRetrySettings
{
    /// <summary>Enable retries (default true).</summary>
    public bool? Enabled { get; set; }

    /// <summary>Agent-level retry attempts (default 3).</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Exponential backoff base delay in milliseconds (default 2000).</summary>
    public long? BaseDelayMs { get; set; }

    /// <summary>Maximum agent-level delay in milliseconds (default 60000).</summary>
    public long? MaxAgentDelayMs { get; set; }

    /// <summary>Provider/SDK-level retry options.</summary>
    public PiProviderRetrySettings? Provider { get; set; }
}

/// <summary>Provider/SDK-level retry options.</summary>
public sealed class PiProviderRetrySettings
{
    /// <summary>SDK/provider request timeout in milliseconds.</summary>
    public long? TimeoutMs { get; set; }

    /// <summary>SDK/provider retry attempts.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Maximum server-requested delay before failing (default 60000).</summary>
    public long? MaxRetryDelayMs { get; set; }
}

/// <summary>Terminal rendering options.</summary>
public sealed class PiTerminalSettings
{
    /// <summary>Show inline images when the terminal supports them (default true).</summary>
    public bool? ShowImages { get; set; }

    /// <summary>Preferred inline image width in terminal cells (default 60).</summary>
    public int? ImageWidthCells { get; set; }

    /// <summary>Clear empty rows when content shrinks (default false).</summary>
    public bool? ClearOnShrink { get; set; }

    /// <summary>OSC 9;4 terminal progress indicators (default false).</summary>
    public bool? ShowTerminalProgress { get; set; }

    /// <summary>Hyperlink support (true/false/auto).</summary>
    public JsonElement? Hyperlinks { get; set; }

    /// <summary>Inline image protocol (kitty/iterm2/auto/false).</summary>
    public JsonElement? Images { get; set; }

    /// <summary>Truecolor support (true/false/auto).</summary>
    public JsonElement? TrueColor { get; set; }
}

/// <summary>Image handling options.</summary>
public sealed class PiImageSettings
{
    /// <summary>Resize images before sending (default true).</summary>
    public bool? AutoResize { get; set; }

    /// <summary>Prevent all images from being sent to providers.</summary>
    public bool? BlockImages { get; set; }
}

/// <summary>Custom token budgets for thinking levels.</summary>
public sealed class PiThinkingBudgetsSettings
{
    /// <summary>Token budget for the minimal level.</summary>
    public int? Minimal { get; set; }

    /// <summary>Token budget for the low level.</summary>
    public int? Low { get; set; }

    /// <summary>Token budget for the medium level.</summary>
    public int? Medium { get; set; }

    /// <summary>Token budget for the high level.</summary>
    public int? High { get; set; }
}

/// <summary>Markdown rendering options.</summary>
public sealed class PiMarkdownSettings
{
    /// <summary>Code block indentation (default two spaces).</summary>
    public string? CodeBlockIndent { get; set; }

    /// <summary>Mermaid rendering mode (off/final/streaming).</summary>
    public string? Mermaid { get; set; }
}

/// <summary>Warning behaviour options.</summary>
public sealed class PiWarningSettings
{
    /// <summary>Warn when Anthropic subscription auth may use paid extra usage (default true).</summary>
    public bool? AnthropicExtraUsage { get; set; }
}

/// <summary>
/// A package source: either a bare source string (load all resources) or an object form
/// filtering which resource types load, matching Pi's <c>PackageSource</c> union.
/// </summary>
public sealed class PiPackageSource
{
    /// <summary>Source identifier (npm:, git:, or a local path).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Start empty and only apply explicit resource patterns.</summary>
    public bool? Autoload { get; set; }

    /// <summary>Extension resource filters (globs).</summary>
    public string[]? Extensions { get; set; }

    /// <summary>Skill resource filters (globs).</summary>
    public string[]? Skills { get; set; }

    /// <summary>Prompt resource filters (globs).</summary>
    public string[]? Prompts { get; set; }

    /// <summary>Theme resource filters (globs).</summary>
    public string[]? Themes { get; set; }
}

/// <summary>Converts Pi's string-or-object package source union to <see cref="PiPackageSource"/>.</summary>
public sealed class PackageSourceJsonConverter : JsonConverter<PiPackageSource>
{
    /// <inheritdoc/>
    public override PiPackageSource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new PiPackageSource { Source = reader.GetString() ?? string.Empty };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a string or object package source.");
        }

        var source = new PiPackageSource();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return source;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected a package source property name.");
            }

            var name = reader.GetString() ?? string.Empty;
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of package source object.");
            }

            switch (name)
            {
                case "source":
                    source.Source = reader.GetString() ?? string.Empty;
                    break;
                case "autoload":
                    source.Autoload = reader.GetBoolean();
                    break;
                case "extensions":
                    source.Extensions = ReadStringArray(ref reader);
                    break;
                case "skills":
                    source.Skills = ReadStringArray(ref reader);
                    break;
                case "prompts":
                    source.Prompts = ReadStringArray(ref reader);
                    break;
                case "themes":
                    source.Themes = ReadStringArray(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Unexpected end of package source object.");
    }

    private static string[] ReadStringArray(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a string array.");
        }
        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values.ToArray();
            }
            values.Add(reader.GetString() ?? string.Empty);
        }
        throw new JsonException("Unexpected end of string array.");
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        PiPackageSource value,
        JsonSerializerOptions options)
    {
        var hasFilters = value.Autoload is not null ||
                         value.Extensions is not null ||
                         value.Skills is not null ||
                         value.Prompts is not null ||
                         value.Themes is not null;
        if (!hasFilters)
        {
            writer.WriteStringValue(value.Source);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("source", value.Source);
        if (value.Autoload is not null)
        {
            writer.WriteBoolean("autoload", value.Autoload.Value);
        }
        WriteStringArray(writer, "extensions", value.Extensions);
        WriteStringArray(writer, "skills", value.Skills);
        WriteStringArray(writer, "prompts", value.Prompts);
        WriteStringArray(writer, "themes", value.Themes);
        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, string[]? values)
    {
        if (values is null)
        {
            return;
        }
        writer.WriteStartArray(name);
        foreach (var item in values)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}
