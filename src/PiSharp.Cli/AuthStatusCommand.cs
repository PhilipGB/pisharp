namespace PiSharp.Cli;

/// <summary>Offline credential-status command. It never retrieves or prints a credential value.</summary>
public static class AuthStatusCommand
{
    public static async Task<int> RunAsync(string[] arguments, string agentDirectory,
        Func<string, string?> environment, TextWriter output, TextWriter error)
    {
        try
        {
            if (arguments.Length == 0 || arguments[0] != "check")
                throw new ArgumentException("Use auth check --provider <id> [--model <configured-exact-id>]. Credential printing is not supported.");
            string? providerId = null, modelId = null;
            var local = false;
            for (var i = 1; i < arguments.Length; i++)
            {
                switch (arguments[i])
                {
                    case "--provider" when ++i < arguments.Length && !arguments[i].StartsWith('-'):
                        if (providerId is not null) throw new ArgumentException("--provider may only be specified once.");
                        providerId = arguments[i];
                        break;
                    case "--model" when ++i < arguments.Length && !arguments[i].StartsWith('-'):
                        if (modelId is not null) throw new ArgumentException("--model may only be specified once.");
                        modelId = arguments[i];
                        break;
                    case "--local": local = true; break;
                    default: throw new ArgumentException("Use auth check --provider <id> [--model <configured-exact-id>] [--local]. Credential printing is not supported.");
                }
            }
            if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("auth check requires --provider <id>.");
            if (local && providerId != "local") throw new ArgumentException("--local requires --provider local.");
            using var http = new HttpClient();
            var runtime = await ProviderModelRuntime.CreateAsync(agentDirectory, local, environment, http);
            var provider = runtime.GetProvider(providerId);
            if (modelId is not null && (string.IsNullOrWhiteSpace(modelId) ||
                !provider.Models.Any(model => model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException($"Model '{modelId}' is not configured for provider '{provider.Id}'; auth check does not contact a model catalog.");
            var (_, authenticated, source) = await runtime.ResolveAuthAsync(provider.Id);
            await output.WriteLineAsync($"{provider.Id}{(modelId is null ? "" : "/" + modelId)}: " +
                $"{(authenticated ? "credential available" : "not authenticated")} ({source}). No provider connection was attempted.");
            return authenticated ? 0 : 1;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Do not print exception details: malformed user configuration might contain credentials.
            await error.WriteLineAsync(exception is ArgumentException ? exception.Message : "Could not read provider configuration or credentials safely.");
            return 2;
        }
    }
}
