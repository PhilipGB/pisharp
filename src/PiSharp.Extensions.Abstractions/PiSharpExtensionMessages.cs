namespace PiSharp.Extensions.Abstractions;

/// <summary>Identifies the message that starts an agent run.</summary>
public enum PiSharpTurnMessageType
{
    /// <summary>The run starts from submitted user input.</summary>
    User,
    /// <summary>The run starts from an extension custom message.</summary>
    Custom
}

/// <summary>A small immutable view of an image attached to a user turn.</summary>
public sealed record PiSharpImageAttachment(string MediaType, long? ByteLength);

/// <summary>A durable extension message that can participate in an agent request.</summary>
public sealed record PiSharpCustomMessage(string CustomType, string Content, bool Display = true);

/// <summary>How a custom message is delivered relative to an active or upcoming agent run.</summary>
public enum PiSharpMessageDeliveryMode
{
    /// <summary>Deliver to the active run as steering input.</summary>
    Steer,
    /// <summary>Run after the active run's steering input has drained.</summary>
    FollowUp,
    /// <summary>Hold the message aside until a later run, after that run's starting message.</summary>
    NextTurn
}

/// <summary>Live command capability for submitting conversation messages through the host run.</summary>
public sealed class PiSharpExtensionCommandContext
{
    private readonly Func<string, CancellationToken, Task> _sendUserMessageAsync;
    private readonly Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task>
        _sendCustomMessageAsync;

    /// <summary>Creates the command context used by the host to provide message sending.</summary>
    public PiSharpExtensionCommandContext(string workingDirectory, bool isIdle, bool isProjectTrusted,
        bool hasPendingMessages, Func<string, CancellationToken, Task> sendUserMessageAsync,
        Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task> sendCustomMessageAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(sendUserMessageAsync);
        ArgumentNullException.ThrowIfNull(sendCustomMessageAsync);

        WorkingDirectory = workingDirectory;
        IsIdle = isIdle;
        IsProjectTrusted = isProjectTrusted;
        HasPendingMessages = hasPendingMessages;
        _sendUserMessageAsync = sendUserMessageAsync;
        _sendCustomMessageAsync = sendCustomMessageAsync;
    }

    /// <summary>The working directory captured when this command started.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Whether the host was idle when this command started.</summary>
    public bool IsIdle { get; }

    /// <summary>Whether project-local native extensions are trusted.</summary>
    public bool IsProjectTrusted { get; }

    /// <summary>Whether message work was waiting when this command started.</summary>
    public bool HasPendingMessages { get; }

    /// <summary>Submits text through the normal user-turn path.</summary>
    public Task SendUserMessageAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return _sendUserMessageAsync(prompt, cancellationToken);
    }

    /// <summary>Sends a custom message with the default trigger and steering behavior.</summary>
    public Task SendCustomMessageAsync(PiSharpCustomMessage message, CancellationToken cancellationToken) =>
        SendCustomMessageAsync(message, triggerTurn: true, PiSharpMessageDeliveryMode.Steer, cancellationToken);

    /// <summary>
    /// Sends a custom message with independent idle-trigger and delivery behavior. While busy,
    /// triggerTurn false appends the message after the current turn's tool results without a new
    /// model request. NextTurn holds it for a later run after that run's starting message.
    /// </summary>
    public Task SendCustomMessageAsync(PiSharpCustomMessage message, bool triggerTurn,
        PiSharpMessageDeliveryMode deliveryMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.CustomType);
        ArgumentNullException.ThrowIfNull(message.Content);
        return _sendCustomMessageAsync(message, triggerTurn, deliveryMode, cancellationToken);
    }
}

/// <summary>Runs an extension command with access to its live host message capabilities.</summary>
public delegate Task<string> PiSharpExtensionCommandHandler(string arguments,
    PiSharpExtensionCommandContext context, CancellationToken cancellationToken);
