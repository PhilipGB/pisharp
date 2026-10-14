using System.Text;

namespace PiSharp.Runtime.Resources;

/// <summary>Resolves a literal or file-backed prompt using Pi's complete-text and read-error policy.</summary>
public static class PromptFileInput
{
    public sealed record Result(string? Text, string? Path);

    public static async Task<Result> ResolveAsync(string? input, string workingDirectory, string description,
        CancellationToken cancellationToken = default, Action<ResourceDiagnostic>? reportDiagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(input)) return new(null, null);
        string path;
        try { path = System.IO.Path.GetFullPath(input, workingDirectory); }
        catch (ArgumentException) { return new(input, null); }
        if (!File.Exists(path) && !Directory.Exists(path)) return new(input, null);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var text = Encoding.UTF8.GetString(bytes);
            return new(text.StartsWith('\uFEFF') ? text[1..] : text, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            reportDiagnostic?.Invoke(new("warning", $"Warning: Could not read {description} file {input}: {error.Message}", path));
            cancellationToken.ThrowIfCancellationRequested();
            return new(input, path);
        }
    }

    public static async Task<(string Text, IReadOnlyList<string> Paths)> ResolveManyAsync(IReadOnlyList<string> inputs,
        string workingDirectory, string description, CancellationToken cancellationToken = default,
        Action<ResourceDiagnostic>? reportDiagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sections = new List<string>();
        var paths = new List<string>();
        foreach (var input in inputs)
        {
            var resolved = await ResolveAsync(input, workingDirectory, description, cancellationToken, reportDiagnostic);
            if (resolved.Text is { } text) sections.Add(text);
            if (resolved.Path is { } path) paths.Add(path);
        }
        return (string.Join("\n\n", sections), paths);
    }
}
