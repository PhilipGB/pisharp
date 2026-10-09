using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Classifiers;

public sealed class SystemOneClassifierClient(HttpClient http) : IClassifierClient
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, string? apiKey,
        CancellationToken cancellationToken = default, ClassifierRequestOptions? options = null)
    {
        var result = new ClassifierResult(model.Api, model.Provider, model.Id, new Dictionary<string, ClassifierAnswer>(),
            "stop", Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        try
        {
            var cloudflare = model.Api == "cloudflare-workers-ai-system-one";
            if (!cloudflare && model.Api != "typesafe-system-one")
                throw new NotSupportedException($"Unsupported classifier API: {model.Api}");
            if (context.Images is { Count: > 0 })
                throw new InvalidOperationException($"{(cloudflare ? "Cloudflare Workers AI" : "System One API")} does not support image input");
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException($"No API key for provider: {model.Provider}");
            if (context.State.ValueKind != JsonValueKind.Object) throw new ArgumentException("Classifier state must be an object.");
            var questions = new JsonObject();
            foreach (var (id, question) in context.Questions)
            {
                var wire = JsonSerializer.SerializeToNode<ClassifierQuestion>(question, s_json)!.AsObject();
                if (question is ClassifierBoolQuestion) wire["type"] = "noul";
                questions[id] = wire;
            }
            var input = new JsonObject { ["state"] = JsonNode.Parse(context.State.GetRawText()), ["questions"] = questions };
            var payload = cloudflare ? new JsonObject { ["model"] = model.Id, ["input"] = input } : input;
            if (!cloudflare) payload["model"] = model.Id;
            var url = new Uri(model.BaseUrl.AbsoluteUri.TrimEnd('/') + "/" + (cloudflare ? "run" : "systemone"));
            using var document = await new ClassifierHttpTransport(http).SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: s_json) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                return request;
            }, options ?? new ClassifierRequestOptions(), cancellationToken).ConfigureAwait(false);
            var output = document.RootElement;
            if (cloudflare)
            {
                if (output.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                    throw new InvalidDataException("Cloudflare Workers AI request failed.");
                var run = output.GetProperty("result");
                if (run.GetProperty("state").GetString() != "Completed") throw new InvalidDataException("Cloudflare Workers AI run did not complete.");
                output = run.GetProperty("result");
            }
            if (output.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                (usage.TryGetProperty("input_tokens", out _) || usage.TryGetProperty("output_tokens", out _)))
                result = result with
                {
                    Usage = UsageRecord.Create(model.Id, "classifier", new UsageDetails
                    {
                        InputTokenCount = Tokens(usage, "input_tokens"),
                        OutputTokenCount = Tokens(usage, "output_tokens")
                    }, model.Pricing)
                };
            var answers = output.GetProperty("answers");
            if (answers.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Classifier answers must be an object.");
            var parsed = new Dictionary<string, ClassifierAnswer>(StringComparer.Ordinal);
            foreach (var (id, question) in context.Questions)
            {
                var answer = answers.GetProperty(id);
                var type = answer.GetProperty("type").GetString();
                parsed[id] = question switch
                {
                    ClassifierBoolQuestion when type == "noul" => new ClassifierBoolAnswer(Number(answer, "noul")),
                    ClassifierScoreQuestion when type == "score" => new ClassifierScoreAnswer(Number(answer, "score"), Number(answer, "confidence")),
                    ClassifierChoiceQuestion when type == "choice" => new ClassifierChoiceAnswer(
                        answer.GetProperty("choice").GetString() ?? throw new InvalidDataException("Invalid classifier choice."),
                        answer.GetProperty("probabilities").EnumerateObject().ToDictionary(property => property.Name,
                            property => Finite(property.Value.GetDouble())), Number(answer, "confidence")),
                    _ => throw new InvalidDataException($"Invalid classifier answer for {id}.")
                };
            }
            return result with { Answers = parsed };
        }
        catch (Exception error)
        {
            return result with { StopReason = cancellationToken.IsCancellationRequested ? "aborted" : "error", ErrorMessage = string.IsNullOrEmpty(apiKey) ? error.Message : error.Message.Replace(apiKey, "[redacted]", StringComparison.Ordinal) };
        }
    }

    private static long Tokens(JsonElement usage, string name) => usage.TryGetProperty(name, out var count) &&
        count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value) && value > 0 ? value : 0;
    private static double Number(JsonElement value, string name) => Finite(value.GetProperty(name).GetDouble());
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new InvalidDataException("Invalid classifier number.");
}
