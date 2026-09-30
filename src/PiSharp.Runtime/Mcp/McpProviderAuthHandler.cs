using System.Net;
using System.Net.Http.Headers;

namespace PiSharp.Runtime.Mcp;

internal sealed class McpProviderAuthHandler : DelegatingHandler
{
    private readonly string _serverName;
    private readonly string _provider;
    private readonly Func<string, CancellationToken, Task<string?>>? _providerTokenResolver;

    public McpProviderAuthHandler(string serverName, string provider,
        Func<string, CancellationToken, Task<string?>>? providerTokenResolver)
        : base(new SocketsHttpHandler())
    {
        _serverName = serverName;
        _provider = provider;
        _providerTokenResolver = providerTokenResolver;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = _providerTokenResolver is null
            ? null
            : await _providerTokenResolver(_provider, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        throw new McpSignInRequiredException(_provider, _serverName);
    }
}
