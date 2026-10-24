using System.ComponentModel;
using System.Text.Json.Serialization;
using PiSharp.Core;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Tools;

internal static class RipgrepSearch
{
    public static async Task<SearchToolOutput> ExecuteAsync(string cwd, string pattern, string? path, string? glob,
        bool ignoreCase, bool literal, double context, double? limit, CancellationToken cancellationToken,
        bool respectGitIgnoreOutsideRepository = false, Func<string, Stream>? openContextFile = null)
    {
        AbortIfCancelled(cancellationToken);
        var executable = ProcessLifetime.FindExecutable(OperatingSystem.IsWindows() ? "rg.exe" : "rg");
        if (executable is null) throw new ToolFailureException("ripgrep (rg) is not available and could not be downloaded");
        var root = CodingToolPaths.Resolve(path is null or "" ? "." : path, cwd, allowNullBytes: true);
        bool isDirectory;
        // Pi catches stat failures after lexical resolution; never pass a NUL path to a filesystem API.
        if (root.Contains('\0')) throw new ToolFailureException($"Path not found: {root}");
        if (OperatingSystem.IsLinux() && LinuxFileAccess.AccessError(root, 0) != 0)
            throw new ToolFailureException($"Path not found: {root}");
        try { isDirectory = (File.GetAttributes(root) & FileAttributes.Directory) != 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ToolFailureException($"Path not found: {root}"); }
        var arguments = new List<string> { "--json", "--line-number", "--color=never", "--hidden" };
        if (respectGitIgnoreOutsideRepository &&
            !SearchInventory.IsWithinGitRepository(isDirectory ? root : Path.GetDirectoryName(root)!))
            arguments.Add("--no-require-git");
        if (ignoreCase) arguments.Add("--ignore-case");
        if (literal) arguments.Add("--fixed-strings");
        if (glob is { Length: > 0 }) { arguments.Add("--glob"); arguments.Add(glob); }
        arguments.AddRange(["--", pattern, root]);
        var nullArgument = arguments.FindIndex(value => value.Contains('\0'));
        if (nullArgument >= 0)
            throw new ToolFailureException($"The argument 'args[{nullArgument}]' must be a string without null bytes. Received " + CodingToolPaths.Inspect(arguments[nullArgument]));
        var maximum = Math.Max(1, limit ?? 100);
        var surrounding = context > 0 ? context : 0;
        var pending = new List<RipgrepMatch>();
        var output = new SearchOutputBuilder();
        var matchCount = 0L;
        var limitReached = false;
        var linesTruncated = false;
        await using var contexts = new GrepContextReader(openContextFile);
        var excluded = await SearchInventory.CreateRepositoryExclusionGuardAsync(isDirectory ? root : Path.GetDirectoryName(root)!, cancellationToken);
        var reader = new RipgrepJsonReader(match =>
        {
            if (match.Path is not null && excluded?.Invoke(match.Path) == true) return true;
            matchCount++;
            if (match is { Path: not null, LineNumber: not null }) pending.Add(match);
            return !(limitReached = matchCount >= maximum);
        });
        PiSharpExtensionExecResult result;
        try
        {
            ProcessSpawnValidation.RequireExecutable(executable, Environment.CurrentDirectory);
            // rg inherits the process cwd, which also controls glob anchoring when the tool cwd differs.
            result = await DirectProcessExecution.ExecuteAsync(executable, arguments, Environment.CurrentDirectory,
                timeout: null, cancellationToken, async bytes =>
            {
                var more = reader.Append(bytes);
                foreach (var match in pending)
                {
                    AbortIfCancelled(cancellationToken);
                    var relative = isDirectory ? Path.GetRelativePath(root, match.Path!) : Path.GetFileName(match.Path!);
                    if (relative.Length == 0 || relative.StartsWith("..", StringComparison.Ordinal)) relative = Path.GetFileName(match.Path!);
                    relative = relative.Replace('\\', '/');
                    if (surrounding == 0 && match.Text is not null)
                    {
                        linesTruncated |= match.LineTruncated;
                        output.Append(relative + ":" + JavaScriptNumberText.Format(match.LineNumber!.Value) + ": " + match.Text);
                    }
                    else
                    {
                        var position = output.Capture();
                        var previousTruncation = linesTruncated;
                        var read = await contexts.AppendAsync(match.Path!, match.LineNumber!.Value, surrounding,
                            (number, text, truncated) =>
                            {
                                linesTruncated |= truncated;
                                var value = JavaScriptNumberText.Format(number);
                                output.Append(number == match.LineNumber ? $"{relative}:{value}: {text}" : $"{relative}-{value}- {text}");
                            }, cancellationToken);
                        if (!read)
                        {
                            output.Restore(position);
                            linesTruncated = previousTruncation;
                            output.Append(relative + ":" + JavaScriptNumberText.Format(match.LineNumber.Value) + ": (unable to read file)");
                        }
                    }
                }
                pending.Clear();
                return more;
            });
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Operation aborted", cancellationToken); }
        catch (ToolFailureException error) when (error.Message.StartsWith("spawn ", StringComparison.Ordinal))
        { throw new ToolFailureException("Failed to run ripgrep: " + error.Message, inner: error); }
        catch (Win32Exception error)
        {
            if (OperatingSystem.IsLinux() && error.NativeErrorCode == 7) throw new ToolFailureException("spawn E2BIG", inner: error);
            throw new ToolFailureException($"Failed to run ripgrep: spawn {executable} {LinuxFileAccess.ErrorCode(error.NativeErrorCode)}", inner: error);
        }
        AbortIfCancelled(cancellationToken);
        if (!limitReached && result.ExitCode is not (0 or 1))
            throw new ToolFailureException(result.Stderr.Trim() is { Length: > 0 } error ? error : $"ripgrep exited with code {result.ExitCode}");
        if (matchCount == 0) return new("No matches found");
        var (content, truncation) = output.Finish();
        var notices = new List<string>();
        if (limitReached) notices.Add($"{JavaScriptNumberText.Format(maximum)} matches limit reached. Use limit={JavaScriptNumberText.Format(maximum * 2)} for more, or refine pattern");
        if (truncation is not null) notices.Add("50.0KB limit reached");
        if (linesTruncated) notices.Add("Some lines truncated to 500 chars. Use read tool to see full lines");
        object? details = notices.Count == 0 ? null : maximum <= int.MaxValue && maximum == Math.Truncate(maximum)
            ? new GrepToolDetails(truncation, limitReached ? (int)maximum : null, linesTruncated ? true : null)
            : new FractionalGrepDetails(truncation, limitReached ? maximum : null, linesTruncated ? true : null);
        return new(content + (notices.Count > 0 ? "\n\n[" + string.Join(". ", notices) + "]" : ""), details);
    }

    private static void AbortIfCancelled(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new OperationCanceledException("Operation aborted", token);
    }

    private sealed record FractionalGrepDetails(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolTruncationDetails? Truncation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MatchLimitReached,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? LinesTruncated);
}
