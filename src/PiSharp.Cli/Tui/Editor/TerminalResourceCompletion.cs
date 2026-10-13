using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Cli.Tui;
using PiSharp.Cli.Protocols;
using PiSharp.Cli.Sessions;
using PiSharp.Cli.Startup;
using PiSharp.Cli.Composition;

using PiSharp.Runtime.Codemode;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalResourceCompletion(ActiveProjectRuntime projectBinding, ExtensionLease extensionLease)
{
    public IReadOnlyList<string> Commands()
    {
        var skillCommands = projectBinding.Settings.SkillCommandsEnabled
            ? projectBinding.Resources.Skills.Select(item => "/skill:" + item.Name)
            : Enumerable.Empty<string>();
        return skillCommands.Concat(projectBinding.Resources.Prompts.Select(item => "/" + item.Name))
            .Concat(extensionLease.Current.Registration.Commands.Keys.Select(name => "/" + name)).ToArray();
    }

    public string? Describe(string command)
    {
        string? description;
        ResourceSourceInfo? sourceInfo;
        if (command.StartsWith("/skill:", StringComparison.Ordinal))
        {
            var skillName = command[7..];
            var skill = projectBinding.Resources.Skills.FirstOrDefault(item => item.Name == skillName);
            if (skill is null) return null;
            description = skill.Description;
            sourceInfo = skill.SourceInfo;
        }
        else if (command.StartsWith('/') && command.Length > 1 &&
            projectBinding.Resources.Prompts.FirstOrDefault(item => "/" + item.Name == command) is { } prompt)
        {
            description = prompt.Description;
            sourceInfo = prompt.SourceInfo;
        }
        else if (command.StartsWith('/') &&
            extensionLease.Current.Registration.CommandInfo.TryGetValue(command[1..], out var extensionCommand))
        {
            description = extensionCommand.Description;
            sourceInfo = extensionCommand.SourceInfo;
        }
        else return null;

        var scope = sourceInfo.Scope == "user" ? "u" : sourceInfo.Scope == "project" ? "p" : "t";
        var source = sourceInfo.Source.Trim();
        var tag = source is "auto" or "local" or "cli" ? scope :
            source.StartsWith("npm:", StringComparison.Ordinal) ? $"{scope}:{source}" : scope;
        var visibleDescription = description?.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return string.IsNullOrWhiteSpace(visibleDescription) ? $"[{tag}]" : $"[{tag}] {visibleDescription}";
    }
}
