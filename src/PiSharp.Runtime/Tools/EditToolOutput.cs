using PiSharp.Core;

namespace PiSharp.Runtime.Tools;

internal sealed record EditToolOutput(string Text, string Diff, string Patch, int? FirstChangedLine) : IStructuredToolOutput
{
    public object EventDetails => Details();

    public override string ToString() => Text;

    public FileEditDetails Details() => new(Diff, Patch, FirstChangedLine);
}
