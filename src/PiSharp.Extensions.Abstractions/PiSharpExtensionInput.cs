using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>Identifies where intercepted user input originated.</summary>
public enum PiSharpInputSource
{
    /// <summary>Input typed in the interactive terminal.</summary>
    Interactive,
    /// <summary>Input submitted through the RPC protocol.</summary>
    Rpc,
    /// <summary>Input submitted by a compiled extension.</summary>
    Extension
}

/// <summary>Identifies how input is queued while an agent run is active.</summary>
public enum PiSharpInputStreamingBehavior
{
    /// <summary>Queue input as steering ahead of follow-up work.</summary>
    Steer,
    /// <summary>Queue input after steering work has drained.</summary>
    FollowUp
}

/// <summary>The action returned by an input callback.</summary>
public enum PiSharpInputResultAction
{
    /// <summary>Leave the current input unchanged.</summary>
    Continue,
    /// <summary>Replace the current text and optionally its images.</summary>
    Transform,
    /// <summary>Consume the input without sending it to the agent.</summary>
    Handled
}

/// <summary>Raw user input supplied before prompt and resource expansion.</summary>
public sealed record PiSharpInputEvent
{
    /// <summary>Creates an input event with detached image snapshots.</summary>
    public PiSharpInputEvent(string text, IReadOnlyList<DataContent>? images, PiSharpInputSource source,
        PiSharpInputStreamingBehavior? streamingBehavior)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Images = PiSharpInputSnapshots.CloneImages(images);
        Source = source;
        StreamingBehavior = streamingBehavior;
    }

    /// <summary>The current text after transforms from earlier handlers.</summary>
    public string Text { get; }

    /// <summary>Detached image snapshots after transforms from earlier handlers.</summary>
    public IReadOnlyList<DataContent> Images { get; }

    /// <summary>Where the input originated.</summary>
    public PiSharpInputSource Source { get; }

    /// <summary>How input will be delivered during an active run, or null while idle.</summary>
    public PiSharpInputStreamingBehavior? StreamingBehavior { get; }
}

/// <summary>A decision returned by an input handler.</summary>
public sealed record PiSharpInputResult
{
    private PiSharpInputResult(PiSharpInputResultAction action, string? text = null,
        IReadOnlyList<DataContent>? images = null)
    {
        Action = action;
        Text = text;
        Images = images is null ? null : PiSharpInputSnapshots.CloneImages(images);
    }

    /// <summary>A result that leaves the current input unchanged.</summary>
    public static PiSharpInputResult Continue { get; } = new(PiSharpInputResultAction.Continue);

    /// <summary>A result that consumes the input without sending it to the agent.</summary>
    public static PiSharpInputResult Handled { get; } = new(PiSharpInputResultAction.Handled);

    /// <summary>The action selected by the handler.</summary>
    public PiSharpInputResultAction Action { get; }

    /// <summary>Replacement text for a transform result.</summary>
    public string? Text { get; }

    /// <summary>
    /// Replacement image snapshots for a transform result. Null preserves existing images;
    /// an explicitly empty collection clears them.
    /// </summary>
    public IReadOnlyList<DataContent>? Images { get; }

    /// <summary>Creates a transform result. Omit <paramref name="images"/> to preserve current images.</summary>
    public static PiSharpInputResult Transform(string text, IReadOnlyList<DataContent>? images = null) =>
        new(PiSharpInputResultAction.Transform, text ?? throw new ArgumentNullException(nameof(text)), images);
}

/// <summary>Handles raw user input before normal prompt processing.</summary>
public delegate ValueTask<PiSharpInputResult?> PiSharpExtensionInputHandler(
    PiSharpInputEvent eventData,
    PiSharpExtensionContext context,
    CancellationToken cancellationToken);

internal static class PiSharpInputSnapshots
{
    internal static IReadOnlyList<DataContent> CloneImages(IReadOnlyList<DataContent>? images)
    {
        if (images is null || images.Count == 0) return Array.Empty<DataContent>();
        var copies = new List<DataContent>(images.Count);
        foreach (var image in images)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true) continue;
            var copy = image.Data is { } data
                ? new DataContent(data.ToArray(), image.MediaType)
                : new DataContent(image.Uri, image.MediaType);
            copy.Name = image.Name;
            copies.Add(copy);
        }
        return Array.AsReadOnly(copies.ToArray());
    }
}
