using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Providers;

public sealed record ImageModelDescriptor(string Provider, string Id, string Name, string Api, Uri BaseUrl,
    IReadOnlyList<string> Input, IReadOnlyList<string> Output, ModelPricing? Pricing = null,
    ModelInputLimits? InputLimits = null);
