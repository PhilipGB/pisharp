using System.Text.Json;
using Json.Schema;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Tools;

internal sealed class ToolResultSchemaValidator
{
    private readonly JsonSchema? _schema;

    public ToolResultSchemaValidator(JsonElement? outputSchema)
    {
        if (outputSchema is { } source)
            _schema = JsonSchema.Build(source, new BuildOptions { Dialect = Dialect.Draft202012 });
    }

    public void Validate(object? value)
    {
        if (_schema is null) return;
        if (!ToolResultOutput.TryReadContract(value, out PiSharpToolResult result) ||
            result.StructuredContent is not { } content)
            throw new InvalidDataException("Tool output schema requires structured content.");
        if (!_schema.Evaluate(content, new EvaluationOptions()).IsValid)
            throw new InvalidDataException("Tool structured content does not match its output schema.");
    }
}
