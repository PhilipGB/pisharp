using PiSharp.Runtime.Resources;

namespace PiSharp.Tests;

public sealed class PromptTemplateExpansionTests
{
    [Theory]
    [InlineData("${ARGUMENTS:-fallback}", "", "fallback")]
    [InlineData("${ARGUMENTS:-fallback}", "one two", "one two")]
    [InlineData("$0|$1|$2|$10", "one two", "|one|two|")]
    [InlineData("${@:2}", "one two three", "two three")]
    [InlineData("${@:2:1}", "one two three", "two")]
    [InlineData("${@:0:2}", "one two three", "one two")]
    [InlineData("${@:1:0}", "one two three", "")]
    [InlineData("${@:999999999999999999999}", "one", "")]
    [InlineData("$1|$@", "one\\ two", "one\\|one\\ two")]
    [InlineData("$1|$@", "\"\" second", "second|second")]
    [InlineData("$1|$@", "\ufeffone\ufefftwo", "one|one two")]
    [InlineData("$1|$@", "one\u0085two", "one\u0085two|one\u0085two")]
    [InlineData("${1:-$2}|$2", "", "$2|")]
    [InlineData("$1|$2", "\"$2\" \"${1:-default}\"", "$2|${1:-default}")]
    [InlineData("$١|${١:-fallback}", "one", "$١|${١:-fallback}")]
    [InlineData("${@:١}|${@:1:١}", "one two", "${@:١}|${@:1:١}")]
    [InlineData("$1١|$١1", "one two", "one١|$١1")]
    public void ExpandsOnceUsingPisArgumentAndSliceSemantics(string template, string arguments, string expected)
    {
        Assert.Equal(expected, PromptTemplateExpansion.Expand(template, arguments));
    }
}
