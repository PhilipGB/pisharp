using System.Text.Json.Serialization;

namespace PiSharp.Runtime.Sessions;

public sealed record ConversationCompactionResult(string Summary, string FirstKeptEntryId, int TokensBefore,
    int EstimatedTokensAfter, UsageRecord? Usage, ConversationCompactionDetails Details);

public sealed record ConversationCompactionDetails(
    [property: JsonPropertyName("readFiles")] IReadOnlyList<string> ReadFiles,
    [property: JsonPropertyName("modifiedFiles")] IReadOnlyList<string> ModifiedFiles);
