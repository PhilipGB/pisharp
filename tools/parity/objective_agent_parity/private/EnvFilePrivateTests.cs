using Fixture.Core;

namespace Fixture.PrivateTests;

/// <summary>
/// Private correctness oracle. This file is never placed in the coding agents'
/// workspace. It is compiled against a copy of the modified production source.
/// </summary>
public sealed class EnvFilePrivateTests
{
    [Fact]
    public void H01_SimpleAssignment()
    {
        Assert.Equal("prod", EnvFile.Parse("MODE=prod")["MODE"]);
    }

    [Fact]
    public void H02_EqualsInsideValue()
    {
        Assert.Equal("abc=def", EnvFile.Parse("TOKEN=abc=def")["TOKEN"]);
    }

    [Fact]
    public void H03_ValueStartsWithAndContainsEquals()
    {
        Assert.Equal("=abc==", EnvFile.Parse("TOKEN==abc==")["TOKEN"]);
    }

    [Fact]
    public void H04_EmptyValue()
    {
        var values = EnvFile.Parse("EMPTY=");

        Assert.True(values.ContainsKey("EMPTY"));
        Assert.Equal(string.Empty, values["EMPTY"]);
    }

    [Fact]
    public void H05_KeyTrimmingPreservesValueWhitespace()
    {
        var values = EnvFile.Parse(" KEY =value ");

        Assert.Equal(new[] { "KEY" }, values.Keys);
        Assert.Equal("value ", values["KEY"]);
    }

    [Fact]
    public void H06_IgnoresCommentsBlanksInvalidLinesAndAcceptsCrlf()
    {
        var text = "# comment\r\n\r\n   # indented\r\nNO_EQUALS\r\n=orphan\r\nA=1\r\nB=two\r\n";

        var values = EnvFile.Parse(text);

        Assert.Equal(new[] { "A", "B" }, values.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("1", values["A"]);
        Assert.Equal("two", values["B"]);
    }

    [Fact]
    public void H07_LastDuplicateWins()
    {
        var values = EnvFile.Parse("MODE=dev\nMODE=prod");

        Assert.Equal("prod", values["MODE"]);
    }

    [Fact]
    public void H08_KeysAreCaseSensitive()
    {
        var values = EnvFile.Parse("Name=one\nname=two");

        Assert.Equal(2, values.Count);
        Assert.Equal("one", values["Name"]);
        Assert.Equal("two", values["name"]);
    }
}
