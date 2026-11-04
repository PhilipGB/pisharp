using System.Diagnostics;

namespace PiSharp.Runtime.Sessions;

public sealed record SessionDeleteOutcome(bool Succeeded, string? Method, string? Error);

/// <summary>Pi-compatible session deletion: try the trash CLI first, then fall back to unlink.</summary>
public static class SessionFileDeletion
{
    public static Task<SessionDeleteOutcome> DeleteAsync(ConversationStore store, SessionListing listing,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(store, listing, TryTrashAsync, cancellationToken);

    internal static async Task<SessionDeleteOutcome> DeleteAsync(ConversationStore store, SessionListing listing,
        Func<string, CancellationToken, Task<bool>> trash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(trash);
        var path = Path.GetFullPath(listing.Path);
        try
        {
            await store.ValidateListingUnchangedAsync(listing, cancellationToken);
        }
        catch (Exception error) when (IsExpected(error) || error is OperationCanceledException)
        {
            if (error is OperationCanceledException) throw;
            return new(false, null, error.Message);
        }

        if (await trash(path, cancellationToken)) return new(true, "trash", null);
        if (!File.Exists(path)) return new(true, "trash", null);

        try
        {
            await store.DeleteAsync(listing, cancellationToken);
            return new(true, "unlink", null);
        }
        catch (Exception error) when (IsExpected(error) || error is OperationCanceledException)
        {
            if (error is OperationCanceledException) throw;
            return new(false, "unlink", error.Message);
        }
    }

    private static async Task<bool> TryTrashAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("trash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or System.Text.Json.JsonException or ArgumentException;
}
