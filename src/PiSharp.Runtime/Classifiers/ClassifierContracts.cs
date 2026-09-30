using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Classifiers;

public sealed record ClassifierModel(string Provider, string Id, string Api, Uri BaseUrl,
    int ContextWindow = 0, ModelPricing? Pricing = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClassifierChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ClassifierScoreQuestion), "score")]
[JsonDerivedType(typeof(ClassifierBoolQuestion), "bool")]
public abstract record ClassifierQuestion(string Instructions);
public sealed record ClassifierChoiceQuestion(string Instructions, IReadOnlyDictionary<string, string> Criteria) : ClassifierQuestion(Instructions);
public sealed record ClassifierScoreQuestion(string Instructions, IReadOnlyList<string> Criteria) : ClassifierQuestion(Instructions);
public sealed record ClassifierBoolQuestion(string Instructions, IReadOnlyDictionary<string, string> Criteria) : ClassifierQuestion(Instructions);
public sealed record ClassifierContext(JsonElement State, IReadOnlyDictionary<string, ClassifierQuestion> Questions);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClassifierChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ClassifierScoreAnswer), "score")]
[JsonDerivedType(typeof(ClassifierBoolAnswer), "bool")]
public abstract record ClassifierAnswer;
public sealed record ClassifierChoiceAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence) : ClassifierAnswer;
public sealed record ClassifierScoreAnswer(double Score, double Confidence) : ClassifierAnswer;
public sealed record ClassifierBoolAnswer(double Probability) : ClassifierAnswer;
public sealed record ClassifierResult(string Api, string Provider, string Model,
    IReadOnlyDictionary<string, ClassifierAnswer> Answers, string StopReason, UsageRecord? Usage = null,
    string? ErrorMessage = null, long Timestamp = 0);

public interface IClassifierClient
{
    Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, string? apiKey,
        CancellationToken cancellationToken = default);
}
