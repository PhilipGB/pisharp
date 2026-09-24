using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Explicit context budget for a model whose window size is known. Never assume a default model limit.</summary>
public sealed record AutoCompactionPolicy(int ContextWindowTokens, int ReserveTokens = 16_384, int? KeepRecentTokens = null)
{
    public static AutoCompactionPolicy? FromEnvironment(Func<string, string?> get)
    {
        var windowText = get("PISHARP_CONTEXT_WINDOW_TOKENS");
        if (windowText is null) return null; // Unknown limits must not trigger speculative summaries.
        if (!int.TryParse(windowText, out var window) || window <= 0)
            throw new ArgumentException("PISHARP_CONTEXT_WINDOW_TOKENS must be a positive integer.");
        var reserveText = get("PISHARP_CONTEXT_RESERVE_TOKENS");
        if (reserveText is not null && (!int.TryParse(reserveText, out var parsed) || parsed < 0))
            throw new ArgumentException("PISHARP_CONTEXT_RESERVE_TOKENS must be a nonnegative integer.");
        var reserve = reserveText is null ? Math.Min(16_384, window / 4) : int.Parse(reserveText);
        var policy = new AutoCompactionPolicy(window, reserve);
        _ = policy.TriggerTokens;
        return policy;
    }

    public int TriggerTokens
    {
        get
        {
            if (ContextWindowTokens <= 0 || ReserveTokens < 0 || ReserveTokens >= ContextWindowTokens)
                throw new ArgumentOutOfRangeException(nameof(ReserveTokens), "Reserve must be nonnegative and smaller than the context window.");
            return ContextWindowTokens - ReserveTokens;
        }
    }

    /// <summary>Conservative heuristic, not provider billing tokens; counts serialised tool arguments and results.</summary>
    public static int Estimate(IReadOnlyList<ChatMessage> context, string prompt)
    {
        long chars = prompt.Length;
        foreach (var message in context)
        {
            using var counter = new SerializedCharacterCounter();
            JsonSerializer.Serialize(counter, message, AIJsonUtilities.DefaultOptions);
            chars += counter.CharacterCount + 64;
            if (chars >= 2L * int.MaxValue) return int.MaxValue;
        }
        return (int)Math.Min(int.MaxValue, (chars + 1) / 2 + 512);
    }

    private sealed class SerializedCharacterCounter : Stream
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private long _characters;
        private long _position;
        public long CharacterCount => _characters + _decoder.GetCharCount(ReadOnlySpan<byte>.Empty, flush: true);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _characters += _decoder.GetCharCount(buffer, flush: false);
            _position += buffer.Length;
        }
    }
}
