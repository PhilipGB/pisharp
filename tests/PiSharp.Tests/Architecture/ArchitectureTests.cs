using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using PiSharp.Core;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ArchitectureTests
{
    private static readonly string[] s_providerSdkPrefixes =
        ["OpenAI", "Anthropic", "AWSSDK", "Google.Apis", "Google.Cloud", "Google.GenAI", "Azure.AI",
            "Microsoft.Extensions.AI.OpenAI"];
    private static readonly HashSet<string> s_volatileCoreTypes = new(StringComparer.Ordinal)
    {
        "System.Console", "System.IO.File", "System.IO.Directory", "System.IO.FileStream",
        "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.DriveInfo", "System.IO.FileSystemWatcher",
        "System.IO.StreamReader", "System.IO.StreamWriter", "System.IO.RandomAccess",
        "System.Diagnostics.Process", "System.Net.Http.HttpClient", "System.Net.WebRequest",
        "System.Net.Sockets.Socket", "System.Net.Sockets.TcpClient", "System.Net.Sockets.TcpListener"
    };
    private static readonly IReadOnlyDictionary<ushort, OpCode> s_opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => unchecked((ushort)code.Value));

    [Theory]
    [InlineData("PiSharp.Core", "")]
    [InlineData("PiSharp.Runtime", "PiSharp.Core")]
    [InlineData("PiSharp.Cli", "PiSharp.Core,PiSharp.Runtime")]
    public void ProductionProjectReferencesFollowDependencyDirection(string project, string allowedReferences)
    {
        var allowed = allowedReferences.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var document = ReadProject(project);
        var references = document.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')));
        Assert.All(references, reference => Assert.Contains(reference, allowed));
    }

    [Fact]
    public void CoreAndRuntimeKeepCliAndProviderSdksOut()
    {
        foreach (var assembly in new[] { typeof(ConversationTree).Assembly, typeof(ConversationRun).Assembly })
        {
            var dependencies = assembly.GetReferencedAssemblies().Select(reference => reference.Name!)
                .Concat(ReadProject(assembly.GetName().Name!).Descendants("PackageReference")
                    .Select(reference => reference.Attribute("Include")!.Value));
            Assert.DoesNotContain(dependencies, name => name.StartsWith("PiSharp.Cli", StringComparison.OrdinalIgnoreCase) ||
                s_providerSdkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        }
        var coreDependencies = typeof(ConversationTree).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!)
            .Concat(ReadProject("PiSharp.Core").Descendants("PackageReference")
                .Select(reference => reference.Attribute("Include")!.Value));
        Assert.DoesNotContain(coreDependencies, name => name.Equals("PiSharp.Runtime", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.Agents.AI", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Microsoft.Extensions.AI", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ModelContextProtocol", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System.ClientModel", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BoundaryScannerRecognizesFilesystemCallsInTheRepositoryReader()
    {
        var reader = typeof(ArchitectureTests).GetMethod(nameof(ReadProject), BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Contains(ReferencedMembers(reader), member => member.DeclaringType?.FullName == "System.IO.File");
        Assert.Contains(ReferencedMembers(reader), member => member.DeclaringType?.FullName == "System.IO.DirectoryInfo");
    }

    [Fact]
    public void CoreDoesNotCallTerminalFilesystemProcessOrNetworkApis()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var type in typeof(ConversationTree).Assembly.GetTypes())
        {
            var methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
            foreach (var method in methods)
            {
                foreach (var member in ReferencedMembers(method))
                {
                    var dependency = member as Type ?? member.DeclaringType;
                    Assert.False(dependency is not null && s_volatileCoreTypes.Contains(dependency.FullName!),
                        $"{type.FullName}.{method.Name} references {dependency?.FullName}.{member.Name}");
                }
            }
        }
    }

    private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) yield break;
        var offset = 0;
        while (offset < bytes.Length)
        {
            var first = bytes[offset++];
            var code = s_opCodes[first == 0xfe ? (ushort)(0xfe00 | bytes[offset++]) : first];
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(bytes, offset);
                var member = method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (member is not null) yield return member;
            }
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, offset),
                _ => 4
            };
        }
    }

    private static XDocument ReadProject(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return XDocument.Load(Path.Combine(directory.FullName, "src", project, project + ".csproj"));
    }
}
