using PiSharp.Extensions.Abstractions;

namespace PiSharp.TestExtensions.RpcParity;

public sealed class RpcProjectRebindExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddSessionStartHandler((_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.Error.WriteLine($"r12-project-context:{context.WorkingDirectory}:" +
                context.IsProjectTrusted.ToString().ToLowerInvariant());
            return ValueTask.CompletedTask;
        });
    }
}
