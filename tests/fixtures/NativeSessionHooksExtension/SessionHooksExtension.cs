using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeSessionHooks;

public sealed class SessionHooksExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddSessionBeforeSwitchHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"before-switch:{eventData.Reason}:{eventData.CurrentSessionId}:{eventData.TargetSessionFile}");
            var veto = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CANCEL_SWITCH");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeSwitchResult?>(
                veto is not null && (veto == eventData.Reason.ToString() || veto == eventData.TargetSessionFile)
                    ? new(true)
                    : null);
        });

        builder.AddSessionBeforeForkHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"before-fork:{eventData.EntryId}:{eventData.Position}");
            var veto = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CANCEL_FORK");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeForkResult?>(
                veto is not null && veto == eventData.EntryId ? new(true) : null);
        });

        builder.AddSessionBeforeTreeHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"before-tree:{eventData.OldLeafEntryId}:{eventData.TargetEntryId}");
            var veto = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CANCEL_TREE");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(
                veto is not null && veto == eventData.TargetEntryId ? new(true) : null);
        });

        builder.AddSessionTreeHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"tree:{eventData.OldLeafEntryId}:{eventData.NewLeafEntryId}");
            return ValueTask.CompletedTask;
        });

        builder.AddSessionBeforeCompactHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"before-compact:{eventData.Reason}:{eventData.WillRetry}:{eventData.Preparation.FirstKeptEntryId}");
            if (Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CANCEL_COMPACTION") == "1")
                return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Cancel: true));

            var summary = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CUSTOM_SUMMARY");
            if (summary is null) return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
            using var details = JsonDocument.Parse("{\"fixture\":\"external-session-hooks\"}");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(Compaction:
                new(summary, eventData.Preparation.FirstKeptEntryId,
                    eventData.Preparation.TokensBefore, details.RootElement)));
        });

        builder.AddSessionStartHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"session-start:{eventData.Reason}:{context.Session?.SessionId}");
            return ValueTask.CompletedTask;
        });

        builder.AddSessionShutdownHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"session-shutdown:{eventData.Reason}:{context.Session?.SessionId}");
            return ValueTask.CompletedTask;
        });

        builder.AddSessionCompactHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"compact:{eventData.FromExtension}:{eventData.Reason}:{eventData.EntryId}:{eventData.Result.Summary}");
            return ValueTask.CompletedTask;
        });

        builder.AddSessionCompactFailedHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"compact-failed:{eventData.Aborted}:{eventData.FromExtension}:{eventData.Reason}");
            return ValueTask.CompletedTask;
        });
    }

    private static void Record(string value)
    {
        var path = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_LOG_PATH");
        if (path is not null) File.AppendAllText(path, value + Environment.NewLine);
    }
}
