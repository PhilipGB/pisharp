using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Tools;

/// <summary>Pinned built-in declarations, separate from their existing execution implementations.</summary>
internal static class CodingToolDeclarations
{
    private static readonly IReadOnlyDictionary<string, JsonElement> s_definitions = LoadDefinitions();

    public static AIFunction Read(AIFunction implementation) => new BuiltinToolFunction(implementation);
    public static AIFunction Bash(AIFunction implementation) => new BuiltinToolFunction(implementation);
    public static AIFunction Write(AIFunction implementation) => new BuiltinToolFunction(implementation);
    public static AIFunction Edit(AIFunction implementation) => new BuiltinToolFunction(implementation);
    public static AIFunction Grep(AIFunction implementation) => new BuiltinToolFunction(implementation);
    public static AIFunction Find(AIFunction implementation) => new BuiltinToolFunction(implementation);
    public static AIFunction Ls(AIFunction implementation) => new BuiltinToolFunction(implementation);

    public static JsonElement? OutputSchema(string name) => Property(name, "outputSchema");
    public static JsonElement? ConstrainedSampling(string name) => Property(name, "constrainedSampling");

    private static JsonElement? Property(string name, string property) =>
        s_definitions[name].TryGetProperty(property, out var value) ? value : null;

    internal static void PrepareArguments(AIFunction function, AIFunctionArguments arguments)
    {
        if (function is BuiltinToolFunction)
            BuiltinToolArguments.PrepareAndValidate(function.Name, function.JsonSchema, arguments);
    }

    private static IReadOnlyDictionary<string, JsonElement> LoadDefinitions()
    {
        using var stream = typeof(CodingToolDeclarations).Assembly.GetManifestResourceStream(
            "PiSharp.Runtime.Tools.CodingToolDeclarations.json") ??
            throw new InvalidOperationException("Built-in tool declarations are missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private sealed class BuiltinToolFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        private readonly JsonElement _definition = s_definitions[inner.Name];
        public override string Description => _definition.GetProperty("description").GetString()!;
        public override JsonElement JsonSchema => _definition.GetProperty("parameters");

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var prepared = new AIFunctionArguments(arguments.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal))
            { Context = arguments.Context };
            BuiltinToolArguments.PrepareAndValidate(Name, JsonSchema, prepared);
            if (Name == "read" && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Operation aborted", cancellationToken);
            return base.InvokeCoreAsync(prepared, cancellationToken);
        }
    }
}
