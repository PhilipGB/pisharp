using System.Text.Json;
using PiSharp.Cli.Protocols;

namespace PiSharp.Tests;

public sealed class JsonLineWriterTests
{
    [Fact]
    public async Task RecordsUseLiteralLfRegardlessOfTextWriterNewLine()
    {
        using var output = new StringWriter { NewLine = "\r\n" };
        var writer = new JsonLineWriter(output);

        await writer.EmitAsync(new { type = "event", text = "first\u2028second\u2029third" });

        var lines = output.ToString();
        Assert.EndsWith("\n", lines);
        Assert.DoesNotContain("\r\n", lines);
        Assert.DoesNotContain('\u2028', lines);
        Assert.DoesNotContain('\u2029', lines);
        using var record = JsonDocument.Parse(lines.TrimEnd('\n'));
        Assert.Equal("first\u2028second\u2029third", record.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ResponseOmitsAbsentIdAndPreservesExplicitNullId()
    {
        using var output = new StringWriter();
        var writer = new JsonLineWriter(output);
        JsonElement explicitNull;
        using (var document = JsonDocument.Parse("null")) explicitNull = document.RootElement.Clone();

        await writer.EmitAsync(new { id = (JsonElement?)null, type = "response", command = "get_state", success = true });
        await writer.EmitAsync(new { id = (JsonElement?)explicitNull, type = "response", command = "get_state", success = true });

        var records = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.False(records[0].RootElement.TryGetProperty("id", out _));
            Assert.True(records[1].RootElement.TryGetProperty("id", out var id));
            Assert.Equal(JsonValueKind.Null, id.ValueKind);
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }

    [Fact]
    public async Task CompletesARecordWhenCancellationArrivesAfterWritingStarts()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelDuringWriteTextWriter(cancellation);
        var writer = new JsonLineWriter(output);

        await writer.EmitAsync(new { type = "event", text = "committed" }, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(output.FlushCalled);
        using var record = JsonDocument.Parse(output.ToString().TrimEnd('\n'));
        Assert.Equal("committed", record.RootElement.GetProperty("text").GetString());
        Assert.Equal(CancellationToken.None, output.FlushToken);
    }

    private sealed class CancelDuringWriteTextWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public bool FlushCalled { get; private set; }
        public CancellationToken FlushToken { get; private set; }

        public override Task WriteAsync(string? value)
        {
            var write = base.WriteAsync(value);
            cancellation.Cancel();
            return write;
        }

        public override Task FlushAsync()
        {
            FlushCalled = true;
            FlushToken = CancellationToken.None;
            return base.FlushAsync();
        }
    }
}
