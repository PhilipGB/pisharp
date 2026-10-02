using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

internal static class AnthropicToolNames
{
    private static readonly Dictionary<string, string> ClaudeCode = new[]
    {
        "Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion", "EnterPlanMode", "ExitPlanMode",
        "KillShell", "NotebookEdit", "Skill", "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch"
    }.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);

    internal static string WireName(string name) => ClaudeCode.GetValueOrDefault(name, name);

    internal static IList<AIContent> MapCalls(IList<AIContent> contents, Func<string, string> map) =>
        contents.Select(content => content is FunctionCallContent call && map(call.Name) is { } name && name != call.Name
            ? new FunctionCallContent(call.CallId, name, call.Arguments)
            {
                Exception = call.Exception,
                AdditionalProperties = call.AdditionalProperties?.Clone(),
                RawRepresentation = call.RawRepresentation
            } : content).ToList();
}
