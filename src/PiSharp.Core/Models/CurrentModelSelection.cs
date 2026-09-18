namespace PiSharp.Core.Models;

/// <summary>
/// The model a session is currently running (pinned AgentSession state: model +
/// thinkingLevel). Carries the session-level context-window and max-output overrides and
/// exposes the effective limits used by compaction thresholds and provider requests.
///
/// Effective limits follow the current model dynamically: a context-window override (PiSharp
/// extension for local endpoints, e.g. --context-tokens) wins over model metadata; without an
/// override the model's own contextWindow/maxTokens are authoritative, so switching models
/// changes the next compaction threshold and the next provider request without a restart.
/// </summary>
public sealed class CurrentModelSelection
{
    public CurrentModelSelection(
        ModelInfo? model,
        string thinkingLevel,
        int? contextWindowOverride,
        int? maxOutputOverride)
    {
        Model = model;
        ThinkingLevel = thinkingLevel;
        ContextWindowOverride = contextWindowOverride;
        MaxOutputOverride = maxOutputOverride;
    }

    /// <summary>The selected model, or null before any model resolved.</summary>
    public ModelInfo? Model { get; }

    /// <summary>The Pi thinking level in effect (canonical: off, minimal, low, medium, high, xhigh, max).</summary>
    public string ThinkingLevel { get; }

    /// <summary>
    /// Session-level context-window override (PiSharp extension; pinned Pi always uses the
    /// model's metadata). Null means "follow the current model".
    /// </summary>
    public int? ContextWindowOverride { get; }

    /// <summary>Session-level max-output override. Null means "follow the current model".</summary>
    public int? MaxOutputOverride { get; }

    /// <summary>The provider id of the current model, if any.</summary>
    public string? Provider => Model?.Provider;

    /// <summary>The model id of the current model, if any.</summary>
    public string? ModelId => Model?.Id;

    /// <summary>Canonical provider/model reference, e.g. "openai/gpt-4o".</summary>
    public string? Reference => Model?.Reference;

    /// <summary>Effective context window: explicit override, else the current model's metadata.</summary>
    public int EffectiveContextWindow =>
        ContextWindowOverride ?? Model?.ContextWindow ?? 128_000;

    /// <summary>Effective max output tokens: explicit override, else the current model's metadata.</summary>
    public int EffectiveMaxOutput =>
        MaxOutputOverride ?? Model?.MaxTokens ?? 0;

    /// <summary>Whether the current model supports thinking/reasoning levels.</summary>
    public bool SupportsThinking => Model?.Reasoning == true;

    /// <summary>Swaps the model, keeping the session overrides (used by /model and cycling).</summary>
    public CurrentModelSelection WithModel(ModelInfo model, string thinkingLevel) =>
        new(model, thinkingLevel, ContextWindowOverride, MaxOutputOverride);

    /// <summary>Swaps the thinking level, keeping the model and session overrides.</summary>
    public CurrentModelSelection WithThinking(string thinkingLevel) =>
        new(Model, thinkingLevel, ContextWindowOverride, MaxOutputOverride);
}
