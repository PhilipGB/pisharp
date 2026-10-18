using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class InteractiveSessionCommands(ConversationStore store, string workingDirectory, bool noSession,
    TextWriter output, Func<Task<string?>> readConfirmation, Action loadTranscript)
{
    public async Task<bool> TryExecuteAsync(string command, string argument, ConversationSession conversation,
        ConversationRun conversationRun, string? sessionPath)
    {
        switch (command)
        {
            case "/export":
                var exportPath = argument.Length == 0 ? Path.Combine(workingDirectory,
                    $"pisharp-{conversation.Id[..12]}.html") : Path.GetFullPath(argument, workingDirectory);
                await SessionExport.ExportHtmlAsync(conversation, exportPath);
                output.WriteLine($"Exported private HTML to {exportPath}. Review before sharing.");
                return true;
            case "/export-jsonl":
                var jsonlPath = argument.Length == 0
                    ? Path.Combine(workingDirectory, $"pisharp-{conversation.Id[..12]}.jsonl")
                    : Path.GetFullPath(SessionPathArgument.Parse(argument), workingDirectory);
                await PiJsonlSessionInterchange.ExportToFileAsync(conversation, jsonlPath);
                output.WriteLine($"Exported private Pi JSONL to {jsonlPath}.");
                return true;
            case "/sessions":
                var listings = SessionCatalog.Search(await SessionCatalog.ListAsync(store), argument);
                foreach (var item in listings)
                    output.WriteLine($"{item.Id[..12]} · {item.Name ?? "(unnamed)"} · {item.Model} · {item.MessageCount} messages · {item.ModifiedAt:yyyy-MM-dd HH:mm}");
                if (listings.Count == 0) output.WriteLine("No saved sessions in this project.");
                return true;
            case "/delete-session":
                if (noSession) throw new InvalidOperationException("There are no saved sessions in --no-session mode.");
                var victim = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), argument);
                if (victim.Path == sessionPath) throw new InvalidOperationException("Switch sessions before deleting the active session.");
                output.WriteLine($"Delete {victim.Id[..12]} · {victim.Name ?? "(unnamed)"}? Type delete {victim.Id[..12]} to confirm:");
                var confirmation = await readConfirmation();
                if (confirmation?.Trim() != "delete " + victim.Id[..12])
                {
                    output.WriteLine("Deletion cancelled.");
                    return true;
                }
                await store.DeleteAsync(victim);
                output.WriteLine($"Deleted {victim.Id[..12]} permanently.");
                return true;
            case "/tree":
                foreach (var node in conversation.Tree.Entries)
                    output.WriteLine($"{(node.Id == conversation.Tree.HeadId ? '>' : ' ')} {node.Id[..12]} ← {node.ParentId?[..12] ?? "root"} {node.Type} {node.Timestamp:HH:mm:ss}");
                return true;
            case "/branch":
                var matches = conversation.Tree.Entries.Where(node => node.Id.StartsWith(argument, StringComparison.Ordinal)).ToArray();
                if (argument.Length == 0 || matches.Length != 1) throw new ArgumentException("Specify a unique entry id prefix from /tree.");
                await conversationRun.SelectAsync(matches[0].Id);
                loadTranscript();
                output.WriteLine($"Selected {matches[0].Id[..12]}");
                return true;
            case "/name":
                await conversationRun.SetSessionNameAsync(argument);
                output.WriteLine($"Name: {conversation.Name ?? "(none)"}");
                return true;
            case "/session":
                var stats = SessionStatistics.Calculate(conversation);
                output.WriteLine($"{sessionPath ?? "(ephemeral)"} · {stats.Id} · {stats.Name ?? "(unnamed)"} · {stats.Model} · head {conversation.Tree.HeadId ?? "(empty)"}");
                var billing = stats.BilledTokens is null ? "provider usage unavailable" :
                    $"{stats.BilledTokens} billed tokens" + (stats.Cost is null ? " · cost unavailable" : $" · ${stats.Cost:0.######}");
                output.WriteLine($"{stats.ActiveMessages} active messages · {stats.UserTurns} turns · {stats.ToolCalls} calls/{stats.ToolResults} results · {stats.Entries} entries/{stats.Leaves} leaves · ~{stats.EstimatedContextTokens} context tokens (estimate) · {billing}");
                return true;
            default:
                return false;
        }
    }
}
