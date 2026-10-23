using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using PiSharp.Core;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Tools;

internal static class FdSearch
{
    public static async Task<SearchToolOutput> ExecuteAsync(string cwd, string pattern, string? path, double? limit,
        CancellationToken cancellationToken, Action? onResult = null)
    {
        AbortIfCancelled(cancellationToken);
        var root = CodingToolPaths.Resolve(path is null or "" ? "." : path, cwd, allowNullBytes: true);
        var maximum = limit ?? 1000;
        var executable = OperatingSystem.IsWindows()
            ? ProcessLifetime.FindExecutable("fd.exe")
            : ProcessLifetime.FindExecutable("fd") ?? ProcessLifetime.FindExecutable("fdfind");
        if (executable is null) throw new ToolFailureException("fd is not available and could not be downloaded");
        var insideRepository = false;
        for (var current = root; ;)
        {
            if (!current.Contains('\0') && Exists(Path.Combine(current, ".git"))) { insideRepository = true; break; }
            var parent = current.Contains('\0') && !OperatingSystem.IsWindows()
                ? current[..Math.Max(1, current.LastIndexOf('/'))] : Path.GetDirectoryName(current) ?? current;
            if (parent == current) break;
            current = parent;
        }
        var arguments = new List<string> { "--glob", "--color=never", "--hidden" };
        if (!insideRepository) arguments.Add("--no-require-git");
        arguments.AddRange(["--max-results", JavaScriptNumberText.Format(maximum)]);
        var maxResultsIndex = arguments.Count - 1;
        var effectivePattern = pattern;
        if (pattern.Contains('/'))
        {
            arguments.Add("--full-path");
            if (!pattern.StartsWith('/') && !pattern.StartsWith("**/", StringComparison.Ordinal) && pattern != "**") effectivePattern = "**/" + pattern;
            if (OperatingSystem.IsWindows()) effectivePattern = effectivePattern.Replace("/", @"[/\\]", StringComparison.Ordinal);
        }
        arguments.AddRange(["--", effectivePattern, root]);
        var nullArgument = arguments.FindIndex(value => value.Contains('\0'));
        if (nullArgument >= 0)
            throw new ToolFailureException($"The argument 'args[{nullArgument}]' must be a string without null bytes. Received " + CodingToolPaths.Inspect(arguments[nullArgument]));
        var directory = Directory.Exists(root) ? root : Path.GetDirectoryName(root)!;
        var excluded = Directory.Exists(directory) ? await SearchInventory.CreateRepositoryExclusionGuardAsync(directory, cancellationToken) : null;
        var guardLimit = excluded is not null && maximum > 0 && maximum < 18446744073709551616d && maximum == Math.Truncate(maximum);
        if (guardLimit) arguments[maxResultsIndex] = "0";
        var output = new SearchOutputBuilder();
        long rawBytes = 0;
        var rawLines = 0L;
        var resultCount = 0L;
        var stopped = false;
        var lines = new ProcessTextLines(raw =>
        {
            if (stopped) return;
            rawBytes += Encoding.UTF8.GetByteCount(raw) + (rawLines++ > 0 ? 1 : 0);
            var line = ProcessTextLines.Trim(raw);
            if (line.Length == 0) return;
            if (excluded?.Invoke(Path.IsPathRooted(line) ? line : Path.Combine(root, line)) == true) return;
            var trailingSeparator = line.EndsWith(Path.DirectorySeparatorChar) || OperatingSystem.IsWindows() && line.EndsWith('/');
            var relative = Path.IsPathRooted(line) ? Path.GetRelativePath(root, line) : line;
            relative = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (trailingSeparator && !relative.EndsWith('/')) relative += "/";
            output.Append(relative);
            resultCount++;
            onResult?.Invoke();
            stopped = guardLimit && resultCount >= maximum;
        });
        PiSharpExtensionExecResult result;
        try
        {
            ProcessSpawnValidation.RequireExecutable(executable, Environment.CurrentDirectory);
            result = await DirectProcessExecution.ExecuteAsync(executable, arguments, Environment.CurrentDirectory,
                timeout: null, cancellationToken, bytes =>
                {
                    lines.Append(bytes);
                    return ValueTask.FromResult(!stopped);
                });
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Operation aborted", cancellationToken); }
        catch (ToolFailureException error) when (error.Message.StartsWith("spawn ", StringComparison.Ordinal))
        { throw new ToolFailureException("Failed to run fd: " + error.Message, inner: error); }
        catch (Win32Exception error)
        {
            if (OperatingSystem.IsLinux() && error.NativeErrorCode == 7) throw new ToolFailureException("spawn E2BIG", inner: error);
            throw new ToolFailureException($"Failed to run fd: spawn {executable} {LinuxFileAccess.ErrorCode(error.NativeErrorCode)}", inner: error);
        }
        AbortIfCancelled(cancellationToken);
        if (rawBytes == 0)
        {
            if (result.ExitCode != 0) throw new ToolFailureException(ProcessTextLines.Trim(result.Stderr) is { Length: > 0 } error ? error : $"fd exited with code {result.ExitCode}");
            return new("No files found matching pattern");
        }
        if (excluded is not null && resultCount == 0) return new("No files found matching pattern");
        var (content, truncation) = output.Finish();
        var reached = resultCount >= maximum;
        var notices = new List<string>();
        if (reached) notices.Add($"{JavaScriptNumberText.Format(maximum)} results limit reached. Use limit={JavaScriptNumberText.Format(maximum * 2)} for more, or refine pattern");
        if (truncation is not null) notices.Add("50.0KB limit reached");
        object? details = notices.Count == 0 ? null : maximum <= int.MaxValue && maximum >= int.MinValue && maximum == Math.Truncate(maximum)
            ? new FindToolDetails(truncation, reached ? (int)maximum : null)
            : new FractionalFindDetails(truncation, reached ? maximum : null);
        return new(content + (notices.Count > 0 ? "\n\n[" + string.Join(". ", notices) + "]" : ""), details);
    }

    private static bool Exists(string path) => OperatingSystem.IsLinux()
        ? LinuxFileAccess.AccessError(path, 0) == 0 : File.Exists(path) || Directory.Exists(path);

    private static void AbortIfCancelled(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new OperationCanceledException("Operation aborted", token);
    }

    private sealed record FractionalFindDetails(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolTruncationDetails? Truncation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? ResultLimitReached);
}
