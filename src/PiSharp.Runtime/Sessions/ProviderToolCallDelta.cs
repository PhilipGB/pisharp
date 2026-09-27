namespace PiSharp.Runtime.Sessions;

internal sealed record ProviderToolCallDelta(int Index, string? CallId, string? Name, string? Arguments);

internal interface IProviderToolCallDeltaCapture : IDisposable
{
    IAsyncEnumerable<ProviderToolCallDelta> ReadAllAsync(CancellationToken cancellationToken);
}

internal interface IProviderToolCallDeltaSource
{
    IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture();
}
