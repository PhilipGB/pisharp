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
}
