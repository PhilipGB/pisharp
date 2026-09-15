using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using PiSharp.Core;

namespace PiSharp.Cli;

internal static class AgentFactory
{
    public static async Task<AgentBootstrap> CreateAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var tools = new CodingTools(options.WorkingDirectory);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        Func<string, int, int, CancellationToken, Task<string>> read = tools.ReadAsync;
        Func<string, string, CancellationToken, Task<string>> write = tools.WriteAsync;
        Func<string, IReadOnlyList<EditOperation>, CancellationToken, Task<string>> edit = tools.EditAsync;
        Func<string, int, CancellationToken, Task<string>> bash = tools.BashAsync;

        AITool[] aiTools =
        [
            AIFunctionFactory.Create(read, "read",
                "Read a UTF-8 text file from the workspace. Returns line-numbered text. Use offset and limit for large files.",
                serializerOptions),
            AIFunctionFactory.Create(write, "write",
                "Write an entire UTF-8 text file in the workspace. Creates parent directories and replaces existing content.",
                serializerOptions),
            AIFunctionFactory.Create(edit, "edit",
                "Edit one file using exact text replacements. Every oldText must uniquely match the original file and edits must not overlap.",
                serializerOptions),
            AIFunctionFactory.Create(bash, "bash",
                "Run a shell command from the workspace root. Use for builds, tests, git, search, and repository operations.",
                serializerOptions),
        ];

        var projectContext = await new AgentsFileLoader().LoadWithSourcesAsync(
            options.WorkingDirectory,
            contextRoot: options.ContextRoot,
            cancellationToken: cancellationToken);

        var instructions = $$"""
            You are PiSharp, a terminal coding agent operating in this repository:
            {{options.WorkingDirectory}}

            Work directly on the repository when the user requests implementation or fixes.
            Inspect relevant files before editing them. Prefer targeted edit operations over rewriting entire existing files.
            Use bash to build and test changes after modifying code when practical.
            Do not claim a build or test passed unless you actually ran it and observed success.
            Keep user-facing responses concise and report concrete changes and verification results.

            {{projectContext.Content}}
            """;

        var openAiOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            openAiOptions.Endpoint = new Uri(options.Endpoint, UriKind.Absolute);
        }

        var turnQueue = new TurnMessageQueue();
        IChatClient chatClient = new SteeringChatClient(
            new ChatClient(
                    options.Model,
                    new ApiKeyCredential(options.ApiKey),
                    openAiOptions)
                .AsIChatClient(),
            turnQueue);

#pragma warning disable MAAI001 // Harness token-limit options are currently marked evaluation-only by MAF.
        var agent = chatClient.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "pisharp",
            HarnessInstructions = "Operate as an autonomous coding harness. Continue using tools until the requested task is complete or you are blocked.",
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = aiTools,
            },
            MaxContextWindowTokens = options.ContextTokens,
            MaxOutputTokens = options.MaxOutputTokens,

            // Pi's core is deliberately small. Keep Harness features that support the loop
            // and compaction, but avoid silently changing Pi's product semantics here.
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableOpenTelemetry = true,
        });
#pragma warning restore MAAI001

        return new AgentBootstrap(agent, projectContext.Files, turnQueue);
    }
}
