using System.Buffers;
using System.Text;

namespace PiSharp.Cli;

/// <summary>Limits redirected initial prompt input before it is composed with positional arguments.</summary>
public static class CliStdin
{
    public const int MaximumCharacters = 1024 * 1024;

    public static async Task<string> ReadAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var rented = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            var content = new StringBuilder();
            while (true)
            {
                var count = await reader.ReadAsync(rented.AsMemory(0, Math.Min(rented.Length, MaximumCharacters + 1 - content.Length)), cancellationToken);
                if (count == 0) return content.ToString().Trim();
                content.Append(rented, 0, count);
                if (content.Length > MaximumCharacters)
                    throw new InvalidDataException("Redirected stdin exceeds the 1M-character prompt limit.");
            }
        }
        finally { ArrayPool<char>.Shared.Return(rented); }
    }
}
