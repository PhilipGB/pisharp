using System.Text.Json;
using ModelContextProtocol.Authentication;
using PiSharp.Runtime.Mcp;

using var scenarios = JsonDocument.Parse(args[0]);
var results = new List<object>();
foreach (var scenario in scenarios.RootElement.EnumerateArray())
{
    var root = Path.Combine(Path.GetTempPath(), "pisharp-credential-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var cache = new McpTokenCache(root);
        var steps = new List<object>();
        foreach (var operation in scenario.GetProperty("operations").EnumerateArray())
        {
            var action = operation.GetProperty("action").GetString();
            var name = operation.GetProperty("name").GetString()!;
            var url = new Uri(operation.GetProperty("url").GetString()!);
            var account = operation.TryGetProperty("account", out var value) ? value.GetString() : "";
            var tokens = new TokenContainer
            {
                AccessToken = account!, RefreshToken = account + "-refresh", TokenType = "Bearer",
                ObtainedAt = DateTimeOffset.UnixEpoch
            };
            object? result = null;
            var path = Path.Combine(root, "mcp-auth.json");
            if (action == "seed")
            {
                var states = File.Exists(path)
                    ? JsonSerializer.Deserialize<Dictionary<string, TokenContainer>>(await File.ReadAllTextAsync(path),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!
                    : [];
                states[url.AbsoluteUri] = tokens;
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(states, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            else if (action == "save") await cache.ForServer(name, url).StoreTokensAsync(tokens, default);
            else if (action == "remove") result = await cache.RemoveAsync(name, url);
            else
            {
                var server = cache.ForServerWithRefresh(name, url);
                var found = action == "peek"
                    ? await cache.ReadTokensAsync(server.Key, server.LegacyKey, default)
                    : await server.GetTokensAsync(default);
                result = found is null ? null : new { access = found.AccessToken, refresh = found.RefreshToken };
            }
            using var stored = JsonDocument.Parse(File.Exists(path) ? await File.ReadAllTextAsync(path) : "{}");
            var keys = stored.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            steps.Add(new { result, keys });
        }
        results.Add(new { id = scenario.GetProperty("id").GetString(), steps });
    }
    finally { Directory.Delete(root, recursive: true); }
}
Console.WriteLine(JsonSerializer.Serialize(results));
