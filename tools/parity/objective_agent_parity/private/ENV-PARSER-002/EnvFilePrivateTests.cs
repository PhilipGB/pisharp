using Fixture.Core;

namespace Fixture.PrivateTests;

/// <summary>
/// Private correctness oracle for ENV-PARSER-002. This file is never placed in
/// the coding agents' workspace. It is compiled against a copy of the modified
/// production sources.
/// </summary>
public sealed class EnvFilePrivateTests
{
    [Fact]
    public void H01_OrdinaryAssignmentsParseCorrectly()
    {
        var values = EnvFile.Parse("MODE=prod\nNAME=one");

        Assert.Equal("prod", values["MODE"]);
        Assert.Equal("one", values["NAME"]);
    }

    [Fact]
    public void H02_MultipleEqualsSignsArePreserved()
    {
        var values = EnvFile.Parse("URL=https://example.test/api?token=abc=def");

        Assert.Equal("https://example.test/api?token=abc=def", values["URL"]);
    }

    [Fact]
    public void H03_EmptyValuesAndWhitespaceFollowTheContract()
    {
        var values = EnvFile.Parse("EMPTY=\n KEY =value \nTRAIL=  x  ");

        Assert.True(values.ContainsKey("EMPTY"));
        Assert.Equal(string.Empty, values["EMPTY"]);
        Assert.Equal("value ", values["KEY"]);
        Assert.Equal("  x  ", values["TRAIL"]);
    }

    [Fact]
    public void H04_CommentsInvalidLinesAndCrlfAreHandled()
    {
        var text = "# comment\r\n\r\n   # indented\r\nNO_EQUALS\r\n=orphan\r\nA=1\r\nB=two\r\n";

        var values = EnvFile.Parse(text);

        Assert.Equal(new[] { "A", "B" }, values.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("1", values["A"]);
        Assert.Equal("two", values["B"]);
    }

    [Fact]
    public void H05_DuplicateKeysAndCaseSensitivityBehaveCorrectly()
    {
        var values = EnvFile.Parse("MODE=dev\nMODE=prod\nName=one\nname=two");

        Assert.Equal("prod", values["MODE"]);
        Assert.Equal("one", values["Name"]);
        Assert.Equal("two", values["name"]);
    }

    [Fact]
    public void H06_KnownPlaceholdersExpand()
    {
        var values = EnvFile.Parse("KNOWN=value\nSECOND=two");

        Assert.Equal("value/two", EnvInterpolator.Expand("${KNOWN}/${SECOND}", values));
        Assert.Equal("prefix-value-suffix", EnvInterpolator.Expand("prefix-${KNOWN}-suffix", values));
    }

    [Fact]
    public void H07_UnknownPlaceholdersRemainUnchanged()
    {
        var values = EnvFile.Parse("KNOWN=value");

        Assert.Equal("value ${MISSING} tail", EnvInterpolator.Expand("${KNOWN} ${MISSING} tail", values));
        Assert.Equal("${EMPTY}", EnvInterpolator.Expand("${EMPTY}", values));
    }

    [Fact]
    public void H08_EndToEndParsingAndExpansionWorkTogether()
    {
        var values = EnvFile.Parse("URL=https://example.test/api?token=abc=def\nNAME=service\nEMPTY=");

        var expanded = EnvInterpolator.Expand(
            "url=${URL}; name=${NAME}; unknown=${MISSING}; literal=ok", values);

        Assert.Equal("url=https://example.test/api?token=abc=def; name=service; unknown=${MISSING}; literal=ok",
            expanded);
        Assert.Equal(string.Empty, values["EMPTY"]);
    }
}
