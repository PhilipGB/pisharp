using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using PiSharp.Core;
using PiSharp.Core.Settings;

namespace PiSharp.Cli;

internal static class AgentFactory
{
    public static async Task<AgentBootstrap> CreateAsync(
        CliOptions options,
        bool projectTrusted,
        CancellationToken cancellationToken,
        string? homeDirectoryOverride = null,
        SettingsManager? settings = null)
    {
        // Tests without a settings manager get the Pi defaults (empty global scope).
        var resolvedSettings = settings ??
            await SettingsManager.CreateFromStorageAsync(new InMemorySettingsStorage(), cancellationToken: cancellationToken);
        var tools = new CodingTools(options.WorkingDirectory);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        Func<string, int, int, CancellationToken, Task<string>> read = tools.ReadAsync;
        Func<string, string, CancellationToken, Task<string>> write = tools.WriteAsync;
        Func<string, IReadOnlyList<EditOperation>, CancellationToken, Task<EditToolResult>> edit = tools.EditForAgentAsync;
        Func<string, int, CancellationToken, Task<string>> bash = tools.BashAsync;
        Func<string, int, CancellationToken, Task<string>> ls = tools.LsAsync;
        Func<string, string, int, CancellationToken, Task<string>> find = tools.FindAsync;
        Func<string, string, string?, bool, bool, int, int, CancellationToken, Task<string>> grep = tools.GrepAsync;

        var readTool = AIFunctionFactory.Create(read, "read",
            "Read a UTF-8 text file from the workspace. Returns line-numbered text. Use offset and limit for large files.",
            serializerOptions);
        var writeTool = AIFunctionFactory.Create(write, "write",
            "Write an entire UTF-8 text file in the workspace. Creates parent directories and replaces existing content.",
            serializerOptions);
        var editTool = AIFunctionFactory.Create(edit, "edit",
            "Edit one file using exact or conservative fuzzy text replacements. Every oldText must uniquely match the original file and edits must not overlap. Returns a diff.",
            serializerOptions);
        var bashTool = AIFunctionFactory.Create(bash, "bash",
            "Run a shell command from the workspace root. Use for builds, tests, git, search, and repository operations.",
            serializerOptions);
        var lsTool = AIFunctionFactory.Create(ls, "ls",
            "List directory contents alphabetically, including dotfiles. Directories have a trailing slash.",
            serializerOptions);
        var findTool = AIFunctionFactory.Create(find, "find",
            "Find workspace files by glob pattern, such as '*.cs' or '**/*.json'.",
            serializerOptions);
        var grepTool = AIFunctionFactory.Create(grep, "grep",
            "Search workspace file contents using a regular expression or literal pattern.",
            serializerOptions);

        AITool[] aiTools = options.NoTools
            ? []
            : options.ReadOnly
                ? [readTool, lsTool, findTool, grepTool]
                : [readTool, writeTool, editTool, bashTool, lsTool, findTool, grepTool];

        var projectContext = await new AgentsFileLoader().LoadWithSourcesAsync(
            options.WorkingDirectory,
            contextRoot: options.ContextRoot,
            cancellationToken: cancellationToken);
        var homeDirectory = ProjectTrustPath.GetHomeDirectory(homeDirectoryOverride);
        var packageResult = new PiPackageCatalog().Discover(options.WorkingDirectory, homeDirectory);
        var extensionHost = new PiSharpExtensionHost();
        var extensionPaths = BuildExtensionPaths(options, packageResult, homeDirectory, projectTrusted);
        extensionHost.LoadFromPaths(extensionPaths);
        var packageSkillPaths = options.NoSkills
            ? []
            : packageResult.UserSkillPaths.Concat(projectTrusted ? packageResult.ProjectSkillPaths : []).ToArray();
        var skillPaths = options.SkillPaths.Concat(packageSkillPaths);
        var skillResult = new SkillCatalog().Discover(
            options.WorkingDirectory,
            homeDirectory,
            skillPaths,
            includeDefaults: !options.NoSkills,
            includeProjectDefaults: projectTrusted);
        foreach (var skill in skillResult.Skills)
        {
            tools.AddReadOnlyRoot(skill.BaseDirectory);
        }
        var packagePromptPaths = options.NoPromptTemplates
            ? []
            : packageResult.UserPromptPaths.Concat(projectTrusted ? packageResult.ProjectPromptPaths : []).ToArray();
        var promptPaths = options.PromptTemplatePaths.Concat(packagePromptPaths);
        var promptTemplates = new PromptTemplateCatalog().Discover(
            options.WorkingDirectory,
            homeDirectory,
            promptPaths,
            includeDefaults: !options.NoPromptTemplates,
            includeProjectDefaults: projectTrusted);
        var expandInput = (string text) => PromptTemplateCatalog.Expand(
            SkillCatalog.ExpandCommand(extensionHost.TransformInput(text), skillResult.Skills),
            promptTemplates);
        var skillPrompt = SkillCatalog.FormatForPrompt(skillResult.Skills);
        var systemPrompt = ReadSystemPrompt(options.WorkingDirectory, homeDirectory, projectTrusted);
        var appendSystemPrompt = ReadAppendSystemPrompt(options.WorkingDirectory, homeDirectory, projectTrusted);
        var resourceDiagnostics = packageResult.Diagnostics.Count +
                                   skillResult.Diagnostics.Count +
                                   extensionHost.Diagnostics.Count;

        var instructions = $$"""
            You are PiSharp, a terminal coding agent operating in this repository:
            {{options.WorkingDirectory}}

            Work directly on the repository when the user requests implementation or fixes.
            Inspect relevant files before editing them. Prefer targeted edit operations over rewriting entire existing files.
            Use bash to build and test changes after modifying code when practical.
            Do not claim a build or test passed unless you actually ran it and observed success.
            Keep user-facing responses concise and report concrete changes and verification results.

            {{systemPrompt}}

            {{appendSystemPrompt}}

            {{projectContext.Content}}

            {{skillPrompt}}
            {{(resourceDiagnostics > 0 ? $"\nResource diagnostics: {resourceDiagnostics} warning(s) were found while loading local resources." : string.Empty)}}
            """;

        var openAiOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            openAiOptions.Endpoint = new Uri(options.Endpoint, UriKind.Absolute);
        }

        var turnQueue = new TurnMessageQueue();
        var sessionHistory = new PiSessionChatHistoryProvider();
        var modelClient = new ChatClient(
                options.Model,
                new ApiKeyCredential(options.ApiKey),
                openAiOptions)
            .AsIChatClient();
        // Compaction seam: every model request the Harness function loop issues passes through
        // CompactionChatClient, which lets PiSharp compact the authoritative session (threshold
        // before the request, forced after a provider overflow) and rebuild the outgoing
        // history from the typed context. SteeringChatClient stays inside the wrapper so the
        // threshold check runs before steering injection, matching Pi's prepareNextTurn order.
        var compactionTarget = new CompactionTarget();
        IChatClient chatClient = new CompactionChatClient(
            new SteeringChatClient(modelClient, turnQueue, expandInput),
            () => compactionTarget.Current);

#pragma warning disable MAAI001 // Harness token-limit options are currently marked evaluation-only by MAF.
        var agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            ChatHistoryProvider = sessionHistory,
            Name = "pisharp",
            HarnessInstructions = "Operate as an autonomous coding harness. Continue using tools until the requested task is complete or you are blocked.",
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = aiTools,
            },
            // PiSharp owns context accounting, summaries, cut points, persistence, and
            // overflow recovery. Harness must not silently reduce or rewrite the transcript.
            DisableCompaction = true,
            MaxContextWindowTokens = options.ContextTokens,
            MaxOutputTokens = options.MaxOutputTokens,

            // Pi's core is deliberately small. Keep only the generic function loop from Harness.
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableOpenTelemetry = true,
        });
#pragma warning restore MAAI001

        // Retry budget comes from the settings system (Pi defaults when unset);
        // --no-auto-retry still disables it for this run.
        var retryPolicy = options.AutoRetry ? resolvedSettings.GetRetryPolicy() : RetryPolicyOptions.Disabled;

        return new AgentBootstrap(
            agent,
            modelClient,
            projectContext.Files,
            skillResult.Skills,
            promptTemplates,
            extensionHost,
            retryPolicy,
            turnQueue,
            sessionHistory,
            compactionTarget);
    }

    private static IReadOnlyList<string> BuildExtensionPaths(
        CliOptions options,
        PiPackageDiscoveryResult packageResult,
        string homeDirectory,
        bool projectTrusted)
    {
        var paths = new List<string>();
        if (!options.NoExtensions)
        {
            paths.Add(Path.Combine(homeDirectory, ".pi", "agent", "extensions"));
            paths.AddRange(packageResult.UserExtensionPaths);
            if (projectTrusted)
            {
                paths.Add(Path.Combine(options.WorkingDirectory, ".pi", "extensions"));
                paths.AddRange(packageResult.ProjectExtensionPaths);
            }
        }
        paths.AddRange(options.ExtensionPaths.Select(path => ResolveWorkspacePath(options.WorkingDirectory, path)));
        return paths;
    }

    private static string ResolveWorkspacePath(string workspaceRoot, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path);

    private static string ReadSystemPrompt(string workspaceRoot, string homeDirectory, bool projectTrusted) =>
        ReadFirstExisting(
            projectTrusted ? Path.Combine(workspaceRoot, ".pi", "SYSTEM.md") : null,
            Path.Combine(homeDirectory, ".pi", "agent", "SYSTEM.md"));

    private static string ReadAppendSystemPrompt(string workspaceRoot, string homeDirectory, bool projectTrusted) =>
        ReadFirstExisting(
            projectTrusted ? Path.Combine(workspaceRoot, ".pi", "APPEND_SYSTEM.md") : null,
            Path.Combine(homeDirectory, ".pi", "agent", "APPEND_SYSTEM.md"));

    private static string ReadFirstExisting(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (path is null || !File.Exists(path))
            {
                continue;
            }
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"Resource warning: unable to read '{path}': {exception.Message}";
            }
        }
        return string.Empty;
    }
}
