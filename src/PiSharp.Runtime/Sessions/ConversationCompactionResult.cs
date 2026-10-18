using System.Text.Json.Serialization;
using System.Text.Json;

namespace PiSharp.Runtime.Sessions;

public sealed record ConversationCompactionResult(string Summary, string FirstKeptEntryId, int TokensBefore,
    int EstimatedTokensAfter, UsageRecord? Usage, ConversationCompactionDetails Details,
    bool FromExtension = false, JsonElement? ExtensionDetails = null);

public sealed record ConversationCompactionDetails(
    [property: JsonPropertyName("readFiles")] IReadOnlyList<string> ReadFiles,
    [property: JsonPropertyName("modifiedFiles")] IReadOnlyList<string> ModifiedFiles);
