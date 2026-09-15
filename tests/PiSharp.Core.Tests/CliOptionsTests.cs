using PiSharp.Cli;

namespace PiSharp.Core.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void ParsesRepeatableResourcePathsAndDisableFlags()
    {
        var options = CliOptions.Parse(
        [
            "--model", "test-model",
            "--endpoint", "http://localhost:8000/v1",
            "--extension", "extensions/one.dll",
            "-e", "extensions/two.dll",
            "--skill", "skills/review",
            "--prompt-template", "prompts/review.md",
            "--mode", "rpc",
            "--print",
            "--no-extensions",
            "--no-skills",
            "--no-prompt-templates",
        ]);

        Assert.Equal(["extensions/one.dll", "extensions/two.dll"], options.ExtensionPaths);
        Assert.Equal(["skills/review"], options.SkillPaths);
        Assert.Equal(["prompts/review.md"], options.PromptTemplatePaths);
        Assert.True(options.NoExtensions);
        Assert.True(options.NoSkills);
        Assert.True(options.NoPromptTemplates);
        Assert.Equal(OutputMode.Rpc, options.OutputMode);
        Assert.True(options.PrintMode);
    }

    [Fact]
    public void ParsesRpcCommandEnvelopeWithoutLosingCorrelationFields()
    {
        var command = RpcProtocol.Parse("{\"id\":\"42\",\"type\":\"prompt\",\"message\":\"inspect\",\"streamingBehavior\":\"steer\"}");

        Assert.Equal("42", command.Id);
        Assert.Equal("prompt", command.Type);
        Assert.Equal("inspect", command.Message);
        Assert.Equal("steer", command.StreamingBehavior);
    }

    [Fact]
    public void EmitsPiShapedJsonLifecycleEvents()
    {
        using var output = new StringWriter();
        var chatOutput = new JsonChatOutput(new JsonLineWriter(output));

        chatOutput.AgentStarted();
        chatOutput.AssistantMessageStarted();
        chatOutput.WriteText("hello");
        chatOutput.AssistantMessageFinished("hello");
        chatOutput.AgentFinished("hello", cancelled: false);

        var events = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["agent_start", "message_start", "message_update", "message_end", "agent_end"],
            events.Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()));
    }
}
