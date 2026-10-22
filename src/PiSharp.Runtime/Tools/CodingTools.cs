using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Local coding tools. Paths resolve against the directory in which the agent was started.</summary>
public sealed class CodingTools
{
    internal const string BashOutputContextKey = "PiSharp.Runtime.BashOutputUpdate";
    private readonly string _cwd;
    private readonly FileReader _reader;
    private readonly FileMutations _mutations;
    private readonly ShellExecution _shell;

    public CodingTools(string workingDirectory, string? shellPath = null,
        ModelImageResizeOptions? imageResizeOptions = null, string? shellCommandPrefix = null)
    {
        _cwd = Path.GetFullPath(workingDirectory);
        _reader = new FileReader(_cwd, imageResizeOptions);
        _mutations = new FileMutations(_cwd);
        _shell = new ShellExecution(_cwd, shellPath, shellCommandPrefix);
    }

    internal ModelImageResizeOptions? ImageResizeOptions => _reader.ImageResizeOptions;
    internal string WorkingDirectory => _cwd;

    public void SetImageResizeOptions(ModelImageResizeOptions? options) => _reader.SetImageResizeOptions(options);

    public IList<AITool> Create(IReadOnlyList<string>? requested = null, IReadOnlyList<string>? excluded = null, bool noTools = false)
    {
        var available = CreateAll().ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var names = requested ?? (noTools ? [] : ["read", "bash", "edit", "write"]);
        var disabled = excluded is null ? null : new HashSet<string>(excluded, StringComparer.Ordinal);
        var selected = new List<AITool>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (!available.TryGetValue(name, out var tool)) throw new ArgumentException($"Unknown tool name: {name}");
            if (disabled?.Contains(name) != true) selected.Add(tool);
        }
        return selected;
    }

    /// <summary>Creates every built-in tool for registry, discovery, and dynamic loadout use.</summary>
    public IReadOnlyList<AITool> CreateAll()
    {
        var search = new SearchTools(_cwd);
        var listing = new DirectoryListingTool(_cwd);
        return Array.AsReadOnly<AITool>(
        [
            CodingToolDeclarations.Read(AIFunctionFactory.Create(ReadForTool, name: "read")),
            CodingToolDeclarations.Bash(AIFunctionFactory.Create(BashForTool, name: "bash")),
            CodingToolDeclarations.Edit(AIFunctionFactory.Create(EditForTool, name: "edit")),
            CodingToolDeclarations.Write(AIFunctionFactory.Create(Write, name: "write")),
            CodingToolDeclarations.Grep(AIFunctionFactory.Create(search.GrepForTool, name: "grep")),
            CodingToolDeclarations.Find(AIFunctionFactory.Create(search.FindForTool, name: "find")),
            CodingToolDeclarations.Ls(AIFunctionFactory.Create(listing.ListForTool, name: "ls"))
        ]);
    }

    [Description("Read a text file. Output is limited to 2000 lines or 50KB. Use offset and limit to continue.")]
    public async Task<string> Read(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("First line to read, starting at 1.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await _reader.ReadAsync(path, offset, limit, cancellationToken)).Text;

    [Description("Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to 2000 lines or 50KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.")]
    private async Task<object> ReadForTool(
        [Description("Path to the file to read (relative or absolute)")] string path,
        [Description("Line number to start reading from (1-indexed)")] int offset = 1,
        [Description("Maximum number of lines to read")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var output = await _reader.ReadAsync(path, offset, limit, cancellationToken);
        return output.ImageDataBase64 is null ? output.Text : output;
    }

    [Description("Write content to a file, creating parent directories if necessary. Overwrites existing files.")]
    public Task<string> Write(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("Complete file contents.")] string content,
        CancellationToken cancellationToken = default) => _mutations.WriteAsync(path, content, cancellationToken);

    [Description("Replace one uniquely matching block in an existing file. Does not create files.")]
    public Task<string> Edit(string path, string oldText, string newText, CancellationToken cancellationToken = default) =>
        EditBatch(path, [new TextEdit(oldText, newText)], cancellationToken);

    public async Task<string> EditBatch(string path, List<TextEdit> edits, CancellationToken cancellationToken = default) =>
        (await _mutations.EditAsync(path, edits, cancellationToken)).Text;

    [Description("Edit a file with multiple exact, unique, nonoverlapping replacements matched against its original contents.")]
    private Task<EditToolOutput> EditForTool(
        [Description("Path relative to the working directory or absolute path.")] string path,
        [Description("One or more oldText/newText blocks; each oldText must match uniquely in the original file.")] List<TextEdit> edits,
        CancellationToken cancellationToken = default) => _mutations.EditAsync(path, edits, cancellationToken);

    [Description("Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.")]
    private async Task<Extensions.PiSharpToolResult> BashForTool(
        [Description("Shell command to execute")] string command,
        [Description("Timeout in seconds (optional, no default timeout)")] double? timeout = null,
        AIFunctionArguments? arguments = null,
        CancellationToken cancellationToken = default)
    {
        Action<string>? onUpdate = null;
        if (arguments?.Context?.TryGetValue(BashOutputContextKey, out var value) == true)
            onUpdate = value as Action<string>;
        var result = await _shell.ExecuteAsync(command, timeout, onUpdate, cancellationToken, returnCancellationResult: false,
            CurrentBashSessionEnvironment(), captureStructuredOutput: true);
        return BashToolOutput.Project(result);
    }

    /// <summary>Execute bash in the configured working directory, returning the bounded combined output.</summary>
    public Task<string> Bash(string command, double? timeout = null, CancellationToken cancellationToken = default) =>
        _shell.ExecuteForToolAsync(command, timeout, onUpdate: null, cancellationToken, normalizeOutput: true);

    /// <summary>Execute bash and return the process result without converting a nonzero exit to a tool failure.</summary>
    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) =>
        _shell.ExecuteAsync(command, timeout: null, onUpdate, cancellationToken, returnCancellationResult: true,
            normalizeOutput: true, throttleUpdates: false);

    internal Task<BashExecutionResult> ExecuteUserBashOperationAsync(string command,
        PiSharp.Extensions.Abstractions.PiSharpUserBashOperation operation, Action<string>? onUpdate,
        CancellationToken cancellationToken) =>
        _shell.ExecuteWithOperationsAsync(command,
            (effectiveCommand, workingDirectory, outputSink, token) =>
                operation(effectiveCommand, workingDirectory, outputSink, token), onUpdate, cancellationToken);

    public void AbortBash() => _shell.AbortBash();

    private static IReadOnlyDictionary<string, string?>? CurrentBashSessionEnvironment()
    {
        AgentRunContext? context;
        try { context = AIAgent.CurrentRunContext; }
        catch (InvalidOperationException) { return null; }
        if (context is null) return null;
        var properties = context.RunOptions?.AdditionalProperties;
        if (properties is null) return null;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in ShellExecution.SessionEnvironmentNames)
            if (properties.TryGetValue(name, out var value) && value is string text)
                environment[name] = text;
        return environment;
    }
}
