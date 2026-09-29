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
        Assert.Equal(["direct", "deferred"], Names(loadout.Snapshot.Callable));
        Assert.DoesNotContain("hidden", Names(loadout.Snapshot.Registered));
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
        Assert.Equal(["echo", "helper"], Names(snapshot.Callable));
    }

    [Fact]
    public void DefaultInactiveToolsStayCallableAndLoadoutChangesAreAtomic()
    {
        var loadout = new PiSharpToolRegistry(
        [
            new PiSharpToolRegistration(Function("search"), ToolExposure.Direct, DefaultActive: false),
            Registration("hidden", ToolExposure.Hidden)
        ]).CreateLoadout();
        var before = loadout.Snapshot;

        Assert.Empty(before.Declared);
        Assert.Equal(["search"], Names(before.Callable));
        Assert.Throws<ArgumentException>(() => loadout.SetActiveTools(["search", "missing"]));
        Assert.Same(before, loadout.Snapshot);

        loadout.SetActiveTools(["search"]);
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

    private static PiSharpToolRegistration Registration(string name, ToolExposure exposure) =>
        new(Function(name), exposure);

    private static AIFunction Function(string name) => AIFunctionFactory.Create(
        (string value) => Echo(value), name: name);

    [Description("Returns its input.")]
    private static string Echo([Description("Value to return.")] string value) => value;

    private static string[] Names(IEnumerable<PiSharpToolRegistration> tools) =>
        tools.Select(tool => tool.Function.Name).ToArray();
}
