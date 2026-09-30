using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Runtime.Mcp;

internal static class McpToolIdentifiers
{
    public static string Namespace(string serverName) => "mcp__" + serverName.Replace('-', '_');

    public static IReadOnlyList<string> CreateNames(string serverName, IReadOnlyList<string> toolNames,
        ISet<string> taken)
    {
        var bases = toolNames.Select(toolName => Sanitize("mcp__" + serverName + "__" + toolName)).ToArray();
        var collisions = bases.GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(taken, StringComparer.Ordinal);
        var names = new string[bases.Length];
        for (var index = 0; index < bases.Length; index++)
        {
            var baseName = bases[index];
            var needsSuffix = baseName.Length > 64 || collisions.Contains(baseName) || used.Contains(baseName);
            var name = needsSuffix
                ? AddStableSuffix(baseName, serverName, toolNames[index])
                : baseName;
            if (!used.Add(name)) throw new InvalidDataException("MCP tool name collision: " + name);
            names[index] = name;
        }
        return names;
    }

    private static string Sanitize(string name) => new(name.Select(character =>
        char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

    private static string AddStableSuffix(string name, string serverName, string toolName)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serverName + "\0" + toolName)))[..8];
        return name[..Math.Min(name.Length, 55)] + "_" + hash;
    }
}
