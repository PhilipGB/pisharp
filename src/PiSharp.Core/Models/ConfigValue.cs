using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PiSharp.Core.Models;

/// <summary>
/// Resolves configuration values that may be shell commands, environment variables, or
/// literals (pinned Pi: resolve-config-value.ts). Used for models.json apiKey/header
/// values. Command results are cached for the process lifetime.
/// </summary>
public static class ConfigValue
{
    private const int CommandTimeoutMs = 10_000;
    private static readonly Regex EnvVarName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex EnvVarNamePrefix = new("^[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, string?> CommandResultCache = new(StringComparer.Ordinal);

    private sealed record CommandReference(string Command);
    private sealed record TemplateReference(IReadOnlyList<(string Kind, string Value)> Parts);
    private sealed record Reference(CommandReference? Command, TemplateReference? Template);

    /// <summary>True when the value is a "!" shell command reference.</summary>
    public static bool IsCommand(string config) => ParseReference(config).Command is not null;

    /// <summary>Returns the environment variable names referenced by a template value.</summary>
    public static string[] GetEnvVarNames(string config)
    {
        var reference = ParseReference(config);
        if (reference.Template is not { } template)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var part in template.Parts)
        {
            if (part.Kind == "env" && !names.Contains(part.Value))
            {
                names.Add(part.Value);
            }
        }

        return names.ToArray();
    }

    /// <summary>Returns the env var names referenced but missing from the environment.</summary>
    public static string[] GetMissingEnvVarNames(string config, IReadOnlyDictionary<string, string>? env)
        => GetEnvVarNames(config)
            .Where(name => ResolveEnvVar(name, env) is null)
            .ToArray();

    /// <summary>True when every referenced env var resolves.</summary>
    public static bool IsConfigured(string config, IReadOnlyDictionary<string, string>? env = null)
        => GetMissingEnvVarNames(config, env).Length == 0;

    /// <summary>
    /// Resolves a config value (cached for commands). Undefined result means the value
    /// is not configured.
    /// </summary>
    public static string? Resolve(string config, IReadOnlyDictionary<string, string>? env)
    {
        var reference = ParseReference(config);
        if (reference.Command is { } command)
        {
            return ExecuteCommandCached(command.Command);
        }

        return reference.Template is { } template ? ResolveTemplate(template, env) : null;
    }

    /// <summary>Resolves without the command cache (used at request time for headers).</summary>
    public static string? ResolveUncached(string config, IReadOnlyDictionary<string, string>? env)
    {
        var reference = ParseReference(config);
        if (reference.Command is { } command)
        {
            return ExecuteCommandUncached(command.Command);
        }

        return reference.Template is { } template ? ResolveTemplate(template, env) : null;
    }

    /// <summary>
    /// Resolves or throws with a deterministic message (pinned Pi:
    /// resolveConfigValueOrThrow).
    /// </summary>
    public static string ResolveOrThrow(string config, string description, IReadOnlyDictionary<string, string>? env)
    {
        var resolved = ResolveUncached(config, env);
        if (resolved is not null)
        {
            return resolved;
        }

        var reference = ParseReference(config);
        if (reference.Command is { } command)
        {
            throw new InvalidOperationException($"Failed to resolve {description} from shell command: {command.Command[1..]}");
        }

        var missing = GetMissingEnvVarNames(config, env);
        if (missing.Length == 1)
        {
            throw new InvalidOperationException($"Failed to resolve {description} from environment variable: {missing[0]}");
        }

        if (missing.Length > 1)
        {
            throw new InvalidOperationException($"Failed to resolve {description} from environment variables: {string.Join(", ", missing)}");
        }

        throw new InvalidOperationException($"Failed to resolve {description}");
    }

    /// <summary>Resolves every header value; unresolved entries are dropped.</summary>
    public static IReadOnlyDictionary<string, string>? ResolveHeaders(
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyDictionary<string, string>? env)
    {
        if (headers is null)
        {
            return null;
        }

        var resolved = new Dictionary<string, string>();
        foreach (var (key, value) in headers)
        {
            var resolvedValue = Resolve(value, env);
            if (resolvedValue is not null)
            {
                resolved[key] = resolvedValue;
            }
        }

        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Resolves every header value, throwing on the first failure.</summary>
    public static IReadOnlyDictionary<string, string>? ResolveHeadersOrThrow(
        IReadOnlyDictionary<string, string>? headers,
        string description,
        IReadOnlyDictionary<string, string>? env)
    {
        if (headers is null)
        {
            return null;
        }

        var resolved = new Dictionary<string, string>();
        foreach (var (key, value) in headers)
        {
            resolved[key] = ResolveOrThrow(value, $"{description} header \"{key}\"", env);
        }

        return resolved.Count > 0 ? resolved : null;
    }

    /// <summary>Clears the command result cache (for tests).</summary>
    public static void ClearCache()
    {
        lock (CacheGate)
        {
            CommandResultCache.Clear();
        }
    }

    private static Reference ParseReference(string config)
    {
        if (config.StartsWith("!", StringComparison.Ordinal))
        {
            return new Reference(new CommandReference(config), null);
        }

        return new Reference(null, new TemplateReference(ParseTemplate(config)));
    }

    private static List<(string Kind, string Value)> ParseTemplate(string config)
    {
        var parts = new List<(string Kind, string Value)>();
        var index = 0;
        while (index < config.Length)
        {
            var dollarIndex = config.IndexOf('$', index);
            if (dollarIndex < 0)
            {
                AppendLiteral(parts, config[index..]);
                break;
            }

            AppendLiteral(parts, config[index..dollarIndex]);
            if (dollarIndex + 1 >= config.Length)
            {
                AppendLiteral(parts, "$");
                index = config.Length;
                continue;
            }

            var nextChar = config[dollarIndex + 1];
            if (nextChar == '$' || nextChar == '!')
            {
                AppendLiteral(parts, nextChar.ToString());
                index = dollarIndex + 2;
                continue;
            }

            if (nextChar == '{')
            {
                var endIndex = config.IndexOf('}', dollarIndex + 2);
                if (endIndex < 0)
                {
                    AppendLiteral(parts, "$");
                    index = dollarIndex + 1;
                    continue;
                }

                var name = config[(dollarIndex + 2)..endIndex];
                if (EnvVarName.IsMatch(name))
                {
                    parts.Add(("env", name));
                }
                else
                {
                    AppendLiteral(parts, config[dollarIndex..(endIndex + 1)]);
                }

                index = endIndex + 1;
                continue;
            }

            var match = EnvVarNamePrefix.Match(config.AsSpan(dollarIndex + 1).ToString());
            if (match.Success)
            {
                parts.Add(("env", match.Value));
                index = dollarIndex + 1 + match.Value.Length;
                continue;
            }

            AppendLiteral(parts, "$");
            index = dollarIndex + 1;
        }

        return parts;
    }

    private static void AppendLiteral(List<(string Kind, string Value)> parts, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (parts.Count > 0)
        {
            var last = parts[^1];
            if (last.Kind == "literal")
            {
                parts[^1] = ("literal", last.Value + value);
                return;
            }
        }

        parts.Add(("literal", value));
    }

    private static string? ResolveTemplate(TemplateReference template, IReadOnlyDictionary<string, string>? env)
    {
        var resolved = string.Empty;
        foreach (var part in template.Parts)
        {
            if (part.Kind == "literal")
            {
                resolved += part.Value;
                continue;
            }

            var envValue = ResolveEnvVar(part.Value, env);
            if (envValue is null)
            {
                return null;
            }

            resolved += envValue;
        }

        return resolved;
    }

    private static string? ResolveEnvVar(string name, IReadOnlyDictionary<string, string>? env)
    {
        if (env is not null && env.TryGetValue(name, out var envValue) && !string.IsNullOrEmpty(envValue))
        {
            return envValue;
        }

        var processValue = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(processValue) ? null : processValue;
    }

    private static string? ExecuteCommandCached(string commandConfig)
    {
        lock (CacheGate)
        {
            if (CommandResultCache.TryGetValue(commandConfig, out var cached))
            {
                return cached;
            }
        }

        var result = ExecuteCommandUncached(commandConfig);
        lock (CacheGate)
        {
            CommandResultCache[commandConfig] = result;
        }

        return result;
    }

    private static string? ExecuteCommandUncached(string commandConfig)
    {
        var command = commandConfig[1..];
        try
        {
            var (fileName, args) = OperatingSystem.IsWindows()
                ? ("cmd", $"/C {command}")
                : ("sh", $"-c {command}");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(CommandTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort.
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var value = output.Trim();
            return value.Length > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
