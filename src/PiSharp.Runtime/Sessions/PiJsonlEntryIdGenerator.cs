namespace PiSharp.Runtime.Sessions;

internal static class PiJsonlEntryIdGenerator
{
    public static string GenerateUnique(HashSet<string> usedIds, Func<string>? generateId = null)
    {
        ArgumentNullException.ThrowIfNull(usedIds);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var id = generateId?.Invoke() ?? Guid.NewGuid().ToString("N")[..8];
            if (usedIds.Add(id)) return id;
        }

        string fallback;
        do fallback = Guid.NewGuid().ToString();
        while (!usedIds.Add(fallback));
        return fallback;
    }
}
