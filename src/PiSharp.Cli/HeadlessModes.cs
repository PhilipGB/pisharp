using PiSharp.Core;

namespace PiSharp.Cli;

internal static class HeadlessModes
{
    public static async Task<int> RunPrintModeAsync(
        AgentBootstrap bootstrap,
        SessionController sessions,
        LiveTurnCoordinator liveTurns,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var promptInput = await ResolvePromptAsync(
            options.Prompt,
            options.FilePaths,
            options.WorkingDirectory,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(promptInput.Text))
        {
            throw new ArgumentException("A prompt is required in print or JSON mode.");
        }

        var writer = new JsonLineWriter(Console.Out);
        IChatOutput output = options.OutputMode == OutputMode.Json
            ? new JsonChatOutput(writer)
            : new SilentChatOutput();
        if (options.OutputMode == OutputMode.Json && sessions.Document is not null)
        {
            writer.Write(sessions.Document.Header);
        }

        var result = await AgentTurnRunner.RunAsync(
            bootstrap,
            sessions,
            liveTurns,
            promptInput.Text,
            options.WorkingDirectory,
            output,
            cancellationToken,
            promptInput.Images);
        await sessions.PersistTurnAsync(
            promptInput.Text,
            result.AssistantText,
            cancellationToken,
            result.ToolRecords);
        if (options.OutputMode != OutputMode.Json)
        {
            Console.WriteLine(result.AssistantText);
        }

        await PublishShutdownAsync(bootstrap, options.WorkingDirectory, liveTurns.Queue);
        return result.Cancelled ? 130 : 0;
    }

    public static async Task<int> RunRpcModeAsync(
        AgentBootstrap bootstrap,
        SessionController sessions,
        LiveTurnCoordinator liveTurns,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var writer = new JsonLineWriter(Console.Out);
        Task? activeTurn = null;
        var shouldExit = false;
        while (!shouldExit)
        {
            var line = await Console.In.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var command = RpcProtocol.Parse(line);
                activeTurn = await HandleRpcCommandAsync(
                    command,
                    activeTurn,
                    bootstrap,
                    sessions,
                    liveTurns,
                    options,
                    writer,
                    cancellationToken);
                shouldExit = command.Type.Equals("shutdown", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                writer.Write(new { type = "response", command = "unknown", success = false, error = exception.Message });
            }
        }

        if (activeTurn is not null)
        {
            await activeTurn;
        }
        await PublishShutdownAsync(bootstrap, options.WorkingDirectory, liveTurns.Queue);
        return 0;
    }

    private static async Task<Task?> HandleRpcCommandAsync(
        RpcCommandEnvelope command,
        Task? activeTurn,
        AgentBootstrap bootstrap,
        SessionController sessions,
        LiveTurnCoordinator liveTurns,
        CliOptions options,
        JsonLineWriter writer,
        CancellationToken cancellationToken)
    {
        var type = command.Type.ToLowerInvariant();
        switch (type)
        {
            case "prompt":
                return await HandlePromptAsync(command, activeTurn, bootstrap, sessions, liveTurns, options, writer, cancellationToken);
            case "steer":
                return QueueRpcMessage(command, liveTurns.Queue, QueuedMessageKind.Steering, writer, activeTurn);
            case "follow_up":
            case "follow-up":
                return QueueRpcMessage(command, liveTurns.Queue, QueuedMessageKind.FollowUp, writer, activeTurn);
            case "abort":
                liveTurns.Abort();
                WriteSuccess(writer, command, "abort");
                return activeTurn;
            case "clear_queue":
                WriteClearedQueue(writer, command, liveTurns.Queue.Clear());
                return activeTurn;
            case "get_state":
                WriteState(writer, command, sessions, liveTurns);
                return activeTurn;
            case "get_tree":
                WriteSuccess(writer, command, "get_tree", new { tree = sessions.FormatTree() });
                return activeTurn;
            case "get_last_assistant_text":
                WriteSuccess(writer, command, "get_last_assistant_text", new { text = sessions.Document?.LatestTurn?.AssistantMessage });
                return activeTurn;
            case "get_commands":
                WriteCommands(writer, command, bootstrap);
                return activeTurn;
            case "set_steering_mode":
                SetQueueMode(command, liveTurns.Queue, steering: true, writer);
                return activeTurn;
            case "set_follow_up_mode":
                SetQueueMode(command, liveTurns.Queue, steering: false, writer);
                return activeTurn;
            case "new_session":
                await sessions.NewAsync(cancellationToken);
                WriteSuccess(writer, command, "new_session", new { cancelled = false });
                return null;
            case "shutdown":
                WriteSuccess(writer, command, "shutdown");
                return activeTurn;
            default:
                WriteError(writer, command, $"Unknown RPC command '{command.Type}'.");
                return activeTurn;
        }
    }

    private static async Task<Task?> HandlePromptAsync(
        RpcCommandEnvelope command,
        Task? activeTurn,
        AgentBootstrap bootstrap,
        SessionController sessions,
        LiveTurnCoordinator liveTurns,
        CliOptions options,
        JsonLineWriter writer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Message))
        {
            WriteError(writer, command, "prompt requires a non-empty message.");
            return activeTurn;
        }
        if (activeTurn is not null && !activeTurn.IsCompleted)
        {
            var kind = string.Equals(command.StreamingBehavior, "steer", StringComparison.OrdinalIgnoreCase)
                ? QueuedMessageKind.Steering
                : QueuedMessageKind.FollowUp;
            liveTurns.Queue.Enqueue(kind, command.Message);
            WriteSuccess(writer, command, "prompt");
            return activeTurn;
        }

        var task = RunRpcTurnAsync(
            bootstrap,
            sessions,
            liveTurns,
            options.WorkingDirectory,
            command.Message,
            writer,
            cancellationToken);
        WriteSuccess(writer, command, "prompt");
        return task;
    }

    private static Task? QueueRpcMessage(
        RpcCommandEnvelope command,
        TurnMessageQueue queue,
        QueuedMessageKind kind,
        JsonLineWriter writer,
        Task? activeTurn)
    {
        if (string.IsNullOrWhiteSpace(command.Message))
        {
            WriteError(writer, command, "queued commands require a non-empty message.");
            return activeTurn;
        }
        queue.Enqueue(kind, command.Message);
        WriteSuccess(writer, command, command.Type);
        return activeTurn;
    }

    private static async Task RunRpcTurnAsync(
        AgentBootstrap bootstrap,
        SessionController sessions,
        LiveTurnCoordinator liveTurns,
        string workspaceRoot,
        string prompt,
        JsonLineWriter writer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await AgentTurnRunner.RunAsync(
                bootstrap,
                sessions,
                liveTurns,
                prompt,
                workspaceRoot,
                new JsonChatOutput(writer),
                cancellationToken);
            await sessions.PersistTurnAsync(prompt, result.AssistantText, cancellationToken, result.ToolRecords);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            writer.Write(new { type = "turn_end", cancelled = true, error = exception.Message });
        }
    }

    private static void WriteState(
        JsonLineWriter writer,
        RpcCommandEnvelope command,
        SessionController sessions,
        LiveTurnCoordinator liveTurns)
    {
        var snapshot = liveTurns.Queue.Snapshot();
        WriteSuccess(writer, command, "get_state", new
        {
            isStreaming = liveTurns.IsRunning,
            steeringMode = liveTurns.Queue.SteeringMode == QueueDrainMode.All ? "all" : "one-at-a-time",
            followUpMode = liveTurns.Queue.FollowUpMode == QueueDrainMode.All ? "all" : "one-at-a-time",
            sessionId = sessions.Document?.Header.SessionId,
            messageCount = sessions.Document?.Turns.Count ?? 0,
            pendingMessageCount = snapshot.Steering.Count + snapshot.FollowUp.Count,
        });
    }

    private static void WriteCommands(JsonLineWriter writer, RpcCommandEnvelope command, AgentBootstrap bootstrap)
    {
        var commands = bootstrap.ExtensionHost.Commands
            .Select(extension => new { name = extension.Name, extension.Description, source = "extension" })
            .Concat(bootstrap.PromptTemplates.Select(template => new { name = template.Name, template.Description, source = "prompt" }))
            .Concat(bootstrap.Skills.Select(skill => new { name = $"skill:{skill.Name}", skill.Description, source = "skill" }));
        WriteSuccess(writer, command, "get_commands", new { commands });
    }

    private static void SetQueueMode(
        RpcCommandEnvelope command,
        TurnMessageQueue queue,
        bool steering,
        JsonLineWriter writer)
    {
        var mode = command.Mode?.ToLowerInvariant();
        if (mode is not ("all" or "one-at-a-time"))
        {
            WriteError(writer, command, "mode must be 'all' or 'one-at-a-time'.");
            return;
        }
        var drainMode = mode == "all" ? QueueDrainMode.All : QueueDrainMode.OneAtATime;
        if (steering)
        {
            queue.SteeringMode = drainMode;
        }
        else
        {
            queue.FollowUpMode = drainMode;
        }
        WriteSuccess(writer, command, command.Type);
    }

    private static void WriteClearedQueue(JsonLineWriter writer, RpcCommandEnvelope command, ClearedTurnQueue cleared)
    {
        WriteSuccess(writer, command, "clear_queue", new
        {
            steering = cleared.Steering.Select(message => message.Text),
            followUp = cleared.FollowUp.Select(message => message.Text),
        });
    }

    private static void WriteSuccess(JsonLineWriter writer, RpcCommandEnvelope command, string commandName, object? data = null)
    {
        writer.Write(data is null
            ? new { id = command.Id, type = "response", command = commandName, success = true }
            : new { id = command.Id, type = "response", command = commandName, success = true, data });
    }

    private static void WriteError(JsonLineWriter writer, RpcCommandEnvelope command, string message) =>
        writer.Write(new { id = command.Id, type = "response", command = command.Type, success = false, error = message });

    private static async Task<FilePrompt> ResolvePromptAsync(
        string? prompt,
        IReadOnlyList<string> filePaths,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (filePaths.Count > 0)
        {
            return await FileArgumentLoader.LoadAsync(prompt, filePaths, workspaceRoot, cancellationToken);
        }
        return new FilePrompt(prompt ?? await Console.In.ReadToEndAsync(cancellationToken), []);
    }

    private static Task PublishShutdownAsync(
        AgentBootstrap bootstrap,
        string workspaceRoot,
        TurnMessageQueue queue) =>
        bootstrap.ExtensionHost.PublishAsync(
            PiSharpExtensionEvent.Shutdown,
            new PiSharpExtensionContext(workspaceRoot, queue, CancellationToken.None));
}
