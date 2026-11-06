using Fixture.Core;

namespace Fixture.Tests;

public sealed class EnvIntegrationTests
{
    [Fact]
    public void ParsesEmbeddedEqualsAndPreservesUnknownPlaceholders()
    {
        var values = EnvFile.Parse("URL=https://example.test/api?token=abc=def\nKNOWN=value");

        Assert.Equal("https://example.test/api?token=abc=def", values["URL"]);

        var expanded = EnvInterpolator.Expand("known=${KNOWN}; unknown=${MISSING}", values);

        Assert.Equal("known=value; unknown=${MISSING}", expanded);
    }
}
