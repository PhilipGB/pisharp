using System.ComponentModel;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Tests;

public sealed class ToolLoadoutTests
{
    [Fact]
    public void DefaultLoadoutSeparatesRegisteredCallableAndDeclaredTools()
    {
        var registry = new PiSharpToolRegistry(
        [
            Registration("direct", ToolExposure.Direct),
            Registration("model", ToolExposure.ModelOnly),
            Registration("codemode", ToolExposure.CodeMode),
            Registration("deferred", ToolExposure.Deferred),
            Registration("hidden", ToolExposure.Hidden)
        ]);

        var snapshot = registry.CreateLoadout().Snapshot;

        Assert.Equal(["direct", "model", "codemode", "deferred", "hidden"], Names(snapshot.Registered));
        Assert.Equal(["direct", "codemode", "deferred"], Names(snapshot.Callable));
        Assert.Equal(["direct", "model"], Names(snapshot.Declared.Select(tool => tool.Registration)));
        Assert.Equal(["direct", "model"], snapshot.ActiveToolNames);
    }

    [Fact]
    public void ExplicitActivationDeclaresDeferredToolsButCannotReachHiddenTools()
    {
        var loadout = new PiSharpToolRegistry(
        [
            Registration("direct", ToolExposure.Direct),
            Registration("model", ToolExposure.ModelOnly),
            Registration("deferred", ToolExposure.Deferred),
            Registration("hidden", ToolExposure.Hidden)
        ]).CreateLoadout();

        loadout.SetActiveTools(["deferred", "model", "hidden"]);

        Assert.Equal(["deferred", "model"], loadout.Snapshot.ActiveToolNames);
        Assert.Equal(["deferred", "model"], Names(loadout.Snapshot.Declared.Select(tool => tool.Registration)));
        Assert.Equal(["deferred"], Names(loadout.Snapshot.Callable));
        Assert.Contains("hidden", Names(loadout.Snapshot.Registered));
    }

    [Fact]
    public void ActiveLoadoutHooksCanDescribeOrHideDeclarationsWithoutChangingCallability()
    {
        var orchestration = new PiSharpToolRegistration(
            Function("orchestrate"),
            ToolExposure.ModelOnly,
            PrepareLoadout: loadout => new ToolLoadoutChanges(
                Descriptions: new Dictionary<string, string>
                {
                    ["orchestrate"] = $"Can call: {string.Join(", ", Names(loadout.Callable))}"
                },
                HiddenDeclarations: ["echo"]));
        var loadout = new PiSharpToolRegistry(
        [
            Registration("echo", ToolExposure.Direct),
            Registration("helper", ToolExposure.CodeMode),
            orchestration
        ]).CreateLoadout();

        var snapshot = loadout.Snapshot;

        Assert.Equal(["orchestrate"], Names(snapshot.Declared.Select(tool => tool.Registration)));
        Assert.Equal("Can call: echo, helper", snapshot.Declared[0].Description);
        Assert.Equal(["echo", "orchestrate"], snapshot.ActiveToolNames);
        Assert.Equal(["echo", "helper"], Names(snapshot.Callable));
    }

    [Fact]
    public void DefaultInactiveDeferredToolsStayCallableAndUnknownNamesAreIgnored()
    {
        var loadout = new PiSharpToolRegistry(
        [
            new PiSharpToolRegistration(Function("search"), ToolExposure.Deferred, DefaultActive: false),
            Registration("hidden", ToolExposure.Hidden)
        ]).CreateLoadout();
        var before = loadout.Snapshot;

        Assert.Empty(before.Declared);
        Assert.Equal(["search"], Names(before.Callable));
        Assert.Throws<ArgumentNullException>(() => loadout.SetActiveTools(null!));
        Assert.Same(before, loadout.Snapshot);

        loadout.SetActiveTools(["search", "missing"]);
        Assert.Equal(["search"], Names(loadout.Snapshot.Declared.Select(tool => tool.Registration)));
        Assert.Empty(before.ActiveToolNames);
    }

    [Fact]
    public void RegistryRejectsDuplicateNames()
    {
        Assert.Throws<ArgumentException>(() => new PiSharpToolRegistry(
        [
            Registration("duplicate", ToolExposure.Direct),
            Registration("duplicate", ToolExposure.Deferred)
        ]));
    }

    [Fact]
    public void ExtensionRegistrationRetainsToolExposureAndNamespace()
    {
        var tool = Function("deferred");
        var definition = new PiSharpToolRegistration(tool, ToolExposure.Deferred,
            Namespace: new PiSharpToolNamespace("mcp__docs", "Documentation tools."));
        var extension = new ExtensionRegistration();

        extension.AddTool(definition);

        Assert.Same(tool, Assert.Single(extension.Tools));
        Assert.Same(definition, Assert.Single(extension.ToolDefinitions));
        var loadout = new PiSharpToolRegistry(extension.ToolDefinitions).CreateLoadout();
        Assert.Equal(ToolExposure.Deferred, loadout.Snapshot.GetExposure("deferred"));
        Assert.Equal("mcp__docs", loadout.Snapshot.GetNamespace("deferred")?.Name);
        Assert.Empty(loadout.Snapshot.Declared);
        Assert.Equal(["deferred"], Names(loadout.Snapshot.Callable));
    }

    private static PiSharpToolRegistration Registration(string name, ToolExposure exposure) =>
        new(Function(name), exposure);

    private static AIFunction Function(string name) => AIFunctionFactory.Create(
        (string value) => Echo(value), name: name);

    [Description("Returns its input.")]
    private static string Echo([Description("Value to return.")] string value) => value;

    private static string[] Names(IEnumerable<PiSharpToolRegistration> tools) =>
        tools.Select(tool => tool.Function.Name).ToArray();
}
