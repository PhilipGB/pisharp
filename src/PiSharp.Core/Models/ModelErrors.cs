namespace PiSharp.Core.Models;

/// <summary>
/// Typed errors for the model/runtime domain. These are intentionally thrown across the
/// service boundary (mirroring Pi's throw style, e.g. "No API key for provider/model")
/// rather than converted to results.
/// </summary>
public abstract class ModelDomainException : Exception
{
    protected ModelDomainException(string message) : base(message)
    {
    }
}

/// <summary>Raised when a provider has no resolvable credential for the requested model.</summary>
public sealed class NoApiKeyException : ModelDomainException
{
    public NoApiKeyException(string provider, string model)
        : base($"No API key for {provider}/{model}")
    {
    }
}

/// <summary>Raised for a model/provider pair that is unknown to the runtime.</summary>
public sealed class UnknownModelException : ModelDomainException
{
    public UnknownModelException(string reference)
        : base($"Unknown model: {reference}")
    {
    }
}

/// <summary>Raised when a provider's API/transport is not implemented in PiSharp.</summary>
public sealed class UnsupportedCapabilityException : ModelDomainException
{
    public UnsupportedCapabilityException(string provider, string api)
        : base($"Unsupported capability: provider '{provider}' uses api '{api}' which PiSharp does not implement yet")
    {
    }
}

/// <summary>Raised when an explicit model switch targets a model the provider cannot serve.</summary>
public sealed class InvalidModelException : ModelDomainException
{
    public InvalidModelException(string reference, string reason)
        : base($"Invalid model '{reference}': {reason}")
    {
    }
}
