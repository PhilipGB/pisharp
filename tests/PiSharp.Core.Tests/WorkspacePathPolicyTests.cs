using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class WorkspacePathPolicyTests
{
    [Fact]
    public void Resolve_AllowsRelativePathInsideWorkspace()
    {
        using var temp = TempDirectory.Create();
        var policy = new WorkspacePathPolicy(temp.Path);

        var result = policy.Resolve(Path.Combine("src", "file.cs"));

        Assert.Equal(Path.Combine(temp.Path, "src", "file.cs"), result);
    }

    [Fact]
    public void Resolve_RejectsParentTraversal()
    {
        using var temp = TempDirectory.Create();
        var policy = new WorkspacePathPolicy(temp.Path);

        Assert.Throws<InvalidOperationException>(() => policy.Resolve(Path.Combine("..", "outside.txt")));
    }
}
