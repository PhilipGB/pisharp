using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal sealed class RpcPromptCommandHandler(JsonLineWriter output, Func<ExtensionRegistration?> extensions,
    Func<string, ExtensionCommandInfo, PiSharpExtensionCommandContext>? createCommandContext = null)
{
    private readonly ConcurrentDictionary<Guid, Operation> _operations = new();

    public bool TryStart(string prompt, JsonElement? id, CancellationToken cancellationToken)
    {
        if (prompt.Length == 0 || prompt[0] != '/') return false;
        var separator = prompt.IndexOf(' ');
        var name = separator < 0 ? prompt[1..] : prompt[1..separator];
        if (name.Length == 0) return false;
        var activeExtensions = extensions();
        if (activeExtensions is null) return false;
        Func<CancellationToken, Task<string>> execute;
        if (activeExtensions.ContextCommands.TryGetValue(name, out var contextHandler))
        {
            if (createCommandContext is null || !activeExtensions.CommandInfo.TryGetValue(name, out var info)) return false;
            var contextArguments = separator < 0 ? "" : prompt[(separator + 1)..];
            execute = async token =>
            {
                using var context = createCommandContext(name, info);
                return await contextHandler(contextArguments, context, token);
            };
        }
        else if (activeExtensions.Commands.TryGetValue(name, out var handler))
        {
            var arguments = separator < 0 ? "" : prompt[(separator + 1)..];
            execute = token => handler(arguments, token);
        }
        else return false;

        var operation = new Operation(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        var key = Guid.NewGuid();
        if (!_operations.TryAdd(key, operation))
        {
            operation.Cancellation.Dispose();
            throw new InvalidOperationException("Could not register the extension command operation.");
        }

        _ = ExecuteAsync(key, operation, id, name, execute);
        return true;
    }

    public async Task CancelAndWaitAsync()
    {
        var operations = _operations.Values.ToArray();
        foreach (var operation in operations)
        {
            try { operation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (operations.Length > 0)
            await Task.WhenAll(operations.Select(operation => operation.Completed.Task));
    }

    private async Task ExecuteAsync(Guid key, Operation operation, JsonElement? id, string name,
        Func<CancellationToken, Task<string>> execute)
    {
        try
        {
            var result = await execute(operation.Cancellation.Token);
            if (operation.Cancellation.IsCancellationRequested) return;
            if (!string.IsNullOrEmpty(result))
                await output.EmitAsync(new
                {
                    type = "event",
                    format = "pisharp",
                    data = new AgentLifecycleEvent("extension_command_output", Text: result, Tool: name,
                        OperationId: CorrelationId(id))
                }, CancellationToken.None);
            await RespondHandledAsync(id);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (operation.Cancellation.IsCancellationRequested) return;
            await output.EmitAsync(new
            {
                type = "extension_error",
                extensionPath = "command:" + name,
                @event = "command",
                error = error.Message
            }, CancellationToken.None);
            await RespondHandledAsync(id);
        }
        finally
        {
            _operations.TryRemove(key, out _);
            operation.Cancellation.Dispose();
            operation.Completed.TrySetResult();
        }
    }

    private Task RespondHandledAsync(JsonElement? id) => output.EmitAsync(new
    {
        id,
        type = "response",
        command = "prompt",
        success = true,
        data = new { disposition = "handled" }
    }, CancellationToken.None);

    private static string? CorrelationId(JsonElement? id) => id is not { } value ||
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null :
        value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

    private sealed class Operation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
