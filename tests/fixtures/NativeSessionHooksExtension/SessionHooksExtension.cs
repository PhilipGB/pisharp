using System.Collections.Concurrent;
using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.NativeSessionHooks;

public sealed class SessionHooksExtension : IPiSharpExtension
{
    private static readonly ConcurrentQueue<PiSharpExtensionContext> BeforeSwitchContexts = new();
    private static int _newSwitchRequests;

    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddSessionBeforeSwitchHandler((eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"before-switch:{eventData.Reason}:{eventData.CurrentSessionId}:{eventData.TargetSessionFile}");
            Record($"before-switch-context:{context.Session?.SessionId}");
            BeforeSwitchContexts.Enqueue(context);
            var veto = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CANCEL_SWITCH");
            var cancel = veto is not null &&
                (veto == eventData.Reason.ToString() || veto == eventData.TargetSessionFile);
            if (veto == "New" && eventData.Reason == PiSharpExtensionSessionSwitchReason.New)
                cancel = Interlocked.Increment(ref _newSwitchRequests) == 1;
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeSwitchResult?>(
                cancel ? new(true) : null);
        });

        builder.AddSessionBeforeForkHandler((eventData, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"before-fork:{eventData.EntryId}:{eventData.Position}");
            var vetoes = Environment.GetEnvironmentVariable("NATIVE_SESSION_HOOKS_CANCEL_FORK")?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeForkResult?>(
                vetoes.Contains(eventData.EntryId, StringComparer.Ordinal) ||
                vetoes.Contains(eventData.Position.ToString(), StringComparer.Ordinal) ? new(true) : null);
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

        builder.AddSessionStartHandler(async (eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"session-start:{eventData.Reason}:{context.Session?.SessionId}");
            await DrainBeforeSwitchContextsAsync();
        });

        builder.AddSessionShutdownHandler(async (eventData, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record($"session-shutdown:{eventData.Reason}:{context.Session?.SessionId}");
            await DrainBeforeSwitchContextsAsync();
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

    private static async ValueTask DrainBeforeSwitchContextsAsync()
    {
        while (BeforeSwitchContexts.TryDequeue(out var previousContext))
        {
            try
            {
                await previousContext.Session!.SetSessionNameAsync("late switch callback mutation");
                Record("before-switch-context-live");
            }
            catch (ObjectDisposedException)
            {
                Record("before-switch-context-revoked");
            }
        }
    }
}
