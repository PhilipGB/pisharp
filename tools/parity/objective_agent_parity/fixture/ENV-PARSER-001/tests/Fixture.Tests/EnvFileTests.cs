using Fixture.Core;

namespace Fixture.Tests;

public sealed class EnvFileTests
{
    [Fact]
    public void StandardAssignmentsParseSuccessfully()
    {
        var values = EnvFile.Parse("MODE=prod\nNAME=one");

        Assert.Equal("prod", values["MODE"]);
        Assert.Equal("one", values["NAME"]);
    }

    [Fact]
    public void CommentsAndBlankLinesAreIgnored()
    {
        var values = EnvFile.Parse("# comment\n\n   # indented comment\nMODE=prod\n");

        Assert.Equal(new[] { "MODE" }, values.Keys);
        Assert.Equal("prod", values["MODE"]);
    }

    [Fact]
    public void ValuesContainingAdditionalEqualsSignsArePreserved()
    {
        var values = EnvFile.Parse("TOKEN=abc=def");

        Assert.Equal("abc=def", values["TOKEN"]);
    }
}
