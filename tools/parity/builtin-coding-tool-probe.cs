using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Tools;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var requests = JsonSerializer.Deserialize<List<ProbeRequest>>(File.ReadAllText(args[0]), jsonOptions)!;
var declarations = new AgentToolCatalog(new CodingTools("/tmp"), null, null, false, false, null, null)
    .Registrations.ToDictionary(registration => registration.Function.Name, registration => new
    {
        name = registration.Function.Name,
        description = registration.Function.Description,
        parameters = registration.Function.JsonSchema,
        outputSchema = registration.OutputSchema,
        constrainedSampling = registration.ConstrainedSampling
    });
var results = new List<object>();
foreach (var request in requests)
{
    var updates = new List<object>();
    using var cancellation = new CancellationTokenSource();
    if (request.PreCancelled) cancellation.Cancel();
    var tools = new CodingTools(request.Workspace, request.Options?.ShellPath,
        request.Options?.ResizeOptions, request.Options?.CommandPrefix);
    var function = tools.CreateAll().OfType<AIFunction>().Single(item => item.Name == request.Tool);
    var arguments = new AIFunctionArguments(request.Arguments.ToDictionary(
        pair => pair.Key, pair => (object?)pair.Value))
    {
        Context = new Dictionary<object, object?>
        {
            [CodingTools.BashOutputContextKey] = (Action<string>)(text =>
            {
                lock (updates)
                    updates.Add(new { content = new[] { new { type = "text", text } }, details = (object?)null });
                if (request.CancelOnOutput is not null && text.Contains(request.CancelOnOutput, StringComparison.Ordinal))
                    cancellation.Cancel();
            })
        }
    };
    object result;
    try
    {
        var value = await function.InvokeAsync(arguments, cancellation.Token);
        var content = new List<object>();
        object? structuredContent = null;
        var isError = false;
        var projected = ToolResultOutput.TryRead(value, out var text, out var details);
        if (ToolResultOutput.TryReadContract(value, out var contract))
        {
            text = contract.Text;
            isError = contract.IsError;
            structuredContent = contract.StructuredContent;
            foreach (var image in contract.Images ?? [])
                content.Add(new { type = "image", data = image.DataBase64, mimeType = image.MimeType });
        }
        else if (ReadToolOutput.TryRead(value, out var read) && read.ImageDataBase64 is not null)
        {
            content.Add(new { type = "image", data = read.ImageDataBase64, mimeType = read.ImageMimeType });
        }
        if (!projected)
            text = value is JsonElement element && element.ValueKind == JsonValueKind.String
                ? element.GetString()! : value?.ToString() ?? "";
        content.Insert(0, new { type = "text", text });
        result = new { request.CaseId, content, details, structuredContent, isError, rejected = false, updates };
    }
    catch (Exception error)
    {
        var text = error.Message;
        result = new { request.CaseId, content = new[] { new { type = "text", text } }, details = (object?)null,
            structuredContent = (object?)null, isError = true, rejected = true, updates };
    }
    results.Add(result);
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new { declarations, results }, jsonOptions) + "\n");
}

internal sealed record ProbeRequest(string CaseId, string Tool, string Workspace,
    Dictionary<string, JsonElement> Arguments, bool PreCancelled = false,
    string? CancelOnOutput = null, ProbeOptions? Options = null);

internal sealed record ProbeOptions(string? ShellPath = null, string? CommandPrefix = null,
    ModelImageResizeOptions? ResizeOptions = null);
