using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Provider request retry options, separate from PiSharp's agent-level retry loop.</summary>
public sealed record ProviderRetrySettings(int? MaxRetries = null, int? TimeoutMs = null, double? MaxRetryDelayMs = null)
{
    public const int DefaultMaxRetries = 0;
    public const double DefaultMaxRetryDelayMs = 60_000;

    public static ProviderRetrySettings Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("settings.json retry.provider must be an object.");
        int? maxRetries = null, timeoutMs = null;
        double? maxRetryDelayMs = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"settings.json retry.provider contains duplicate property '{property.Name}'.");
            if (property.Name == "maxRetries" && property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var retries) && retries is >= 0 and <= 20)
            {
                maxRetries = retries;
                continue;
            }
            if (property.Name == "timeoutMs" && property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var timeout) && timeout >= 0)
            {
                timeoutMs = timeout;
                continue;
            }
            if (property.Name == "maxRetryDelayMs" && property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetDouble(out var delay) && double.IsFinite(delay) && delay >= 0)
            {
                maxRetryDelayMs = delay;
                continue;
            }
            throw new InvalidDataException($"settings.json retry.provider.{property.Name} is unsupported or invalid.");
        }
        return new(maxRetries, timeoutMs, maxRetryDelayMs);
    }

    public static ProviderRetrySettings? Merge(ProviderRetrySettings? defaults, ProviderRetrySettings? overrides) =>
        overrides is null ? defaults : new(
            overrides.MaxRetries ?? defaults?.MaxRetries,
            overrides.TimeoutMs ?? defaults?.TimeoutMs,
            overrides.MaxRetryDelayMs ?? defaults?.MaxRetryDelayMs);
}

/// <summary>Validated retry settings for user and trusted-project scopes.</summary>
public sealed record RetrySettings(bool? Enabled = null, int? MaxRetries = null, int? BaseDelayMs = null,
    int? MaxAgentDelayMs = null, ProviderRetrySettings? Provider = null)
{
    public static RetrySettings Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("settings.json retry must be an object.");
        bool? enabled = null;
        int? maxRetries = null, baseDelayMs = null, maxAgentDelayMs = null;
        double? legacyMaxDelayMs = null;
        ProviderRetrySettings? provider = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException($"settings.json retry contains duplicate property '{property.Name}'.");
            switch (property.Name)
            {
                case "enabled" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    enabled = property.Value.GetBoolean();
                    break;
                case "maxRetries" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var retries) && retries is >= 0 and <= 20:
                    maxRetries = retries;
                    break;
                case "baseDelayMs" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var baseDelay) && baseDelay is >= 0 and <= 60_000:
                    baseDelayMs = baseDelay;
                    break;
                case "maxAgentDelayMs" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out var maxDelay) && maxDelay is >= 0 and <= 300_000:
                    maxAgentDelayMs = maxDelay;
                    break;
                case "maxDelayMs" when property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetDouble(out var legacyDelay) && double.IsFinite(legacyDelay) && legacyDelay >= 0:
                    legacyMaxDelayMs = legacyDelay;
                    break;
                case "provider":
                    provider = ProviderRetrySettings.Parse(property.Value);
                    break;
                default:
                    throw new InvalidDataException($"settings.json retry.{property.Name} is unsupported or invalid.");
            }
        }
        if (legacyMaxDelayMs is { } migratedDelay && provider?.MaxRetryDelayMs is null)
            provider = (provider ?? new ProviderRetrySettings()) with { MaxRetryDelayMs = migratedDelay };
        return new(enabled, maxRetries, baseDelayMs, maxAgentDelayMs, provider);
    }

    public static RetrySettings? Merge(RetrySettings? defaults, RetrySettings? overrides) => overrides is null ? defaults : new(
        overrides.Enabled ?? defaults?.Enabled,
        overrides.MaxRetries ?? defaults?.MaxRetries,
        overrides.BaseDelayMs ?? defaults?.BaseDelayMs,
        overrides.MaxAgentDelayMs ?? defaults?.MaxAgentDelayMs,
        ProviderRetrySettings.Merge(defaults?.Provider, overrides.Provider));

    public AgentRunRetryPolicy ResolvePolicy()
    {
        var defaults = AgentRunRetryPolicy.Default;
        var baseDelay = TimeSpan.FromMilliseconds(BaseDelayMs ?? (int)defaults.BaseDelay.TotalMilliseconds);
        var maximum = TimeSpan.FromMilliseconds(MaxAgentDelayMs ?? (int)defaults.MaxDelay.TotalMilliseconds);
        if (baseDelay > maximum) baseDelay = maximum;
        return new(Enabled ?? defaults.Enabled, MaxRetries ?? defaults.MaxRetries, baseDelay, maximum);
    }
}
