using System.Text.Json;
using PiSharp.Runtime.Classifiers;

namespace PiSharp.Runtime.Codemode;

/// <summary>Host-owned catalog and classifier access; guest descriptors never supply request authority.</summary>
public interface ICodemodeModels
{
    IReadOnlyList<JsonElement> GetModels(string type, string? provider = null);
    Task<IReadOnlyList<JsonElement>> GetAvailableAsync(string type, string? provider, CancellationToken cancellationToken);
    Task<ClassifierResult> ClassifyAsync(string provider, string id, ClassifierContext context, CancellationToken cancellationToken);
}
