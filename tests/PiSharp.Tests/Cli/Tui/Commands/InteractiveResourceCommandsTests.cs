using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class InteractiveResourceCommandsTests
{
    [Fact]
    public async Task DisabledSkillCommandsCannotBeInvokedByTypingTheCommandDirectly()
    {
        var invoked = false;

        await InteractiveResourceCommands.ExecuteAsync("/skill:review", "", "/skill:review",
            getResources: () => throw new InvalidOperationException("Skill dispatch should not need resource enumeration."),
            skillCommandsEnabled: () => false,
            getExtensions: () => throw new InvalidOperationException("Skill dispatch should not need extension enumeration."),
            run: _ => { invoked = true; return Task.CompletedTask; }, editor: null, screen: null,
            applicationAction: _ => Task.CompletedTask, reload: () => Task.CompletedTask, getMcpServers: () => []);

        Assert.False(invoked);
    }

    [Fact]
    public async Task EnabledSkillCommandsStillInvokeTheSkillCommand()
    {
        var invoked = false;

        await InteractiveResourceCommands.ExecuteAsync("/skill:review", "", "/skill:review",
            getResources: () => throw new InvalidOperationException("Skill dispatch should not need resource enumeration."),
            skillCommandsEnabled: () => true,
            getExtensions: () => throw new InvalidOperationException("Skill dispatch should not need extension enumeration."),
            run: _ => { invoked = true; return Task.CompletedTask; }, editor: null, screen: null,
            applicationAction: _ => Task.CompletedTask, reload: () => Task.CompletedTask, getMcpServers: () => []);

        Assert.True(invoked);
    }
}
