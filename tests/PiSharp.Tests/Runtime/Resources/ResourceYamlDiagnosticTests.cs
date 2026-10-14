using PiSharp.Runtime.Resources;
using YamlDotNet.Core;

namespace PiSharp.Tests;

public sealed class ResourceYamlDiagnosticTests
{
    [Theory]
    [InlineData("flow-map-unclosed", "---\nname: guide\ndescription: {bad\n---\nbody", "Flow map in block collection must be sufficiently indented and end with a } at line 2, column 18:\n\ndescription: {bad\n                 ^\n")]
    [InlineData("flow-map-colon", "---\nname: guide\ndescription: {one: two other: three}\n---\nbody", "Block collections are not allowed within flow collections at line 2, column 20:\n\ndescription: {one: two other: three}\n                   ^^^^^^^^^^^^^^^^\n")]
    [InlineData("single-quote-unclosed", "---\nname: guide\ndescription: 'broken\n---\nbody", "Missing closing 'quote at line 2, column 21:\n\ndescription: 'broken\n                    ^\n")]
    [InlineData("double-quote-unclosed", "---\nname: guide\ndescription: \"broken\n---\nbody", "Missing closing \"quote at line 2, column 21:\n\ndescription: \"broken\n                    ^\n")]
    [InlineData("double-quote-invalid-escape", "---\nname: guide\ndescription: \"bad\\q\"\n---\nbody", "Invalid escape sequence \\q at line 2, column 18:\n\ndescription: \"bad\\q\"\n                 ^\n")]
    [InlineData("double-quote-invalid-hex", "---\nname: guide\ndescription: \"bad\\xZZ\"\n---\nbody", "Invalid escape sequence \\xZZ at line 2, column 18:\n\ndescription: \"bad\\xZZ\"\n                 ^\n")]
    [InlineData("unknown-alias", "---\nname: guide\ndescription: *missing\n---\nbody", "Unresolved alias (the anchor must be set before the alias): missing")]
    [InlineData("indent-tab", "---\nname: guide\n\tdescription: Guide\n---\nbody", "Tabs are not allowed as indentation at line 2, column 1:\n\nname: guide\n\tdescription: Guide\n^\n")]
    [InlineData("directive-version", "---\n%YAML 1.1\n---\nname: guide\ndescription: yes\n---\nbody", "Missing directives-end indicator line at line 1, column 10:\n\n%YAML 1.1\n         ^\n")]
    [InlineData("key-alias", "---\nkey: &key description\nname: guide\n*key: Guide\n---\nbody", "Implicit map keys need to be followed by map values at line 3, column 1:\n\nname: guide\n*key: Guide\n^^^^^\n")]
    [InlineData("numeric-key", "---\nname: guide\ndescription: Guide\n1: one\n01: two\n---\nbody", "Map keys must be unique at line 4, column 1:\n\n1: one\n01: two\n^\n")]
    [InlineData("long-quote-error", "---\nname: guide\ndescription: \"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n---\nbody", "Missing closing \"quote at line 2, column 115:\n\n…xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n                                                                                ^\n")]
    [InlineData("block-header-invalid", "---\nname: guide\ndescription: |x\n  Guide\n---\nbody", "Block scalar header includes extra characters: |x at line 2, column 15:\n\ndescription: |x\n              ^\n")]
    [InlineData("scanner-invalid-state", "---\nname: guide\nunused: [bad\ndescription: *missing\n---\nbody", "Flow sequence in block collection must be sufficiently indented and end with a ] at line 3, column 1:\n\nunused: [bad\ndescription: *missing\n^\n")]
    [InlineData("root-explicit-set-invalid", "---\n!!set {name: guide, description: Guide}\n---\nbody", "Set items must all have null values at line 1, column 1:\n\n!!set {name: guide, description: Guide}\n^^^^^\n")]
    [InlineData("value-explicit-set-invalid", "---\nname: guide\ndescription: Guide\nunused: !!set {one: Two}\n---\nbody", "Set items must all have null values at line 3, column 9:\n\nunused: !!set {one: Two}\n        ^^^^^\n")]
    [InlineData("root-mapping-trailing-garbage", "---\n{name: guide, description: Guide} garbage\n---\nbody", "Unexpected scalar at node end at line 1, column 35:\n\n{name: guide, description: Guide} garbage\n                                  ^^^^^^^\n")]
    public void ParseFailureMatchesCurrentPiDiagnosticText(string scenario, string content, string expected)
    {
        Assert.NotEmpty(scenario);
        var error = Assert.ThrowsAny<YamlException>(() => ResourceFrontmatter.Parse(content));

        Assert.Equal(expected, error.Message);
    }
}
