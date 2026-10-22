using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Owns configured Pi tool exposure, live catalog replacement and session-local loadouts.</summary>
internal sealed class AgentToolCatalog
{
    private readonly PiSharpToolRegistry _toolRegistry;
    private readonly IReadOnlyList<string>? _initialActiveToolNames;
    private readonly ConditionalWeakTable<AgentSession, ToolLoadout> _sessionToolLoadouts = new();
    private readonly IReadOnlyList<PiSharpToolRegistration> _builtinRegistrations;
    private readonly IReadOnlyList<string>? _selectedExtensionTools;
    private readonly IReadOnlySet<string> _excludedExtensionTools;
    private readonly bool _noExtensionTools;

    public IReadOnlyList<PiSharpToolRegistration> Registrations { get; private set; }
    internal bool RestoresRecordedToolLoadout => _selectedExtensionTools is null && !_noExtensionTools;

    public AgentToolCatalog(CodingTools tools, IReadOnlyList<string>? selectedTools,
        IReadOnlyList<string>? excludedTools, bool noTools, bool noBuiltinTools,
        IReadOnlyCollection<AIFunction>? extensionTools,
        IReadOnlyCollection<PiSharpToolRegistration>? extensionToolRegistrations,
        IReadOnlyList<string>? initialActiveToolNames = null)
    {
        var registeredExtensions = new List<PiSharpToolRegistration>();
        foreach (var function in extensionTools ?? [])
        {
            ArgumentNullException.ThrowIfNull(function);
            registeredExtensions.Add(new(function));
        }
        foreach (var definition in extensionToolRegistrations ?? [])
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(definition.Function);
            registeredExtensions.Add(definition);
        }
        _selectedExtensionTools = selectedTools?.ToArray();
        _excludedExtensionTools = new HashSet<string>(excludedTools ?? [], StringComparer.Ordinal);
        _noExtensionTools = noTools;
        var extensionNames = registeredExtensions.Select(registration => registration.Function.Name)
            .ToHashSet(StringComparer.Ordinal);
        var builtin = selectedTools is null
            ? (!noTools && !noBuiltinTools
                ? tools.CreateAll().OfType<AIFunction>().Where(function => excludedTools?.Contains(function.Name) != true).ToArray()
                : [])
            : tools.Create(selectedTools.Where(name => !extensionNames.Contains(name)).ToArray(), excludedTools,
                noTools || noBuiltinTools).OfType<AIFunction>().ToArray();
        var external = registeredExtensions.Where(registration =>
                (selectedTools is null ? !noTools :
                    selectedTools.Contains(registration.Function.Name, StringComparer.Ordinal) ||
                    (!noTools && registration.Exposure is ToolExposure.CodeMode or ToolExposure.Deferred)) &&
                excludedTools?.Contains(registration.Function.Name) != true)
            .ToArray();
        var reservedBuiltinNames = new[] { "read", "bash", "edit", "write", "grep", "find", "ls" };
        if (registeredExtensions.Any(registration => reservedBuiltinNames.Contains(registration.Function.Name, StringComparer.Ordinal)) ||
            builtin.Select(function => function.Name).Concat(external.Select(registration => registration.Function.Name))
                .GroupBy(name => name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Extension tool conflicts with a built-in tool name.");
        var defaultBuiltinNames = new HashSet<string>(["read", "bash", "edit", "write"], StringComparer.Ordinal);
        _builtinRegistrations = builtin.Select(function => new PiSharpToolRegistration(function,
                DefaultActive: defaultBuiltinNames.Contains(function.Name),
                OutputSchema: CodingToolDeclarations.OutputSchema(function.Name),
                PromptSnippet: PiSystemPromptBuilder.BuiltinSnippet(function.Name),
                PromptGuidelines: PiSystemPromptBuilder.BuiltinGuidelines(function.Name),
                ConstrainedSampling: CodingToolDeclarations.ConstrainedSampling(function.Name)))
            .ToArray();
        Registrations = _builtinRegistrations.Concat(external).ToArray();
        _toolRegistry = new PiSharpToolRegistry(Registrations);
        var initialNames = initialActiveToolNames ?? selectedTools;
        _initialActiveToolNames = initialNames is null
            ? null
            : initialNames.Where(name => _toolRegistry.Registered.Any(registration =>
                string.Equals(registration.Function.Name, name, StringComparison.Ordinal))).ToArray();
    }

    public IReadOnlyList<AIFunctionDeclaration> ToolDeclarations => Array.AsReadOnly(
        _toolRegistry.CreateLoadout(_initialActiveToolNames).Snapshot.Declared.Select(declaration =>
            (AIFunctionDeclaration)(string.Equals(declaration.Description, declaration.Registration.Function.Description,
                StringComparison.Ordinal) && declaration.Registration.ConstrainedSampling is null
                ? declaration.Registration.Function
                : new DeclaredAIFunction(declaration.Registration.Function, declaration.Description,
                    declaration.Registration.ConstrainedSampling))).ToArray());
    public ToolLoadout GetToolLoadout(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessionToolLoadouts.GetValue(session, _ => _toolRegistry.CreateLoadout(_initialActiveToolNames));
    }

    internal void RestoreToolLoadout(AgentSession session, IEnumerable<string> activeToolNames) =>
        GetToolLoadout(session).RestoreActiveTools(activeToolNames.Where(name =>
            !_excludedExtensionTools.Contains(name) && (_selectedExtensionTools is null
                ? !_noExtensionTools : _selectedExtensionTools.Contains(name, StringComparer.Ordinal))));

    public ToolLoadout CreateLoadout() => _toolRegistry.CreateLoadout(_initialActiveToolNames);

    public void ReplaceExtensions(IReadOnlyCollection<PiSharpToolRegistration> definitions,
        Action<IReadOnlyList<PiSharpToolRegistration>> prepareExecutables)
    {
        var external = definitions.Where(registration =>
                (_selectedExtensionTools is null ? !_noExtensionTools :
                    _selectedExtensionTools.Contains(registration.Function.Name, StringComparer.Ordinal) ||
                    (!_noExtensionTools && registration.Exposure is ToolExposure.CodeMode or ToolExposure.Deferred)) &&
                !_excludedExtensionTools.Contains(registration.Function.Name))
            .ToArray();
        var registrations = _builtinRegistrations.Concat(external).ToArray();
        prepareExecutables(registrations);
        Registrations = registrations;
        _toolRegistry.Replace(registrations);
    }
}
