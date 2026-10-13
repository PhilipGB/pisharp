using System.Text;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Tools;

namespace PiSharp.Tests;

public sealed class StreamingReadTests
{
    [Theory]
    [InlineData("long-line")]
    [InlineData("many-lines")]
    [InlineData("unicode")]
    public async Task LargeFileReadsMatchPinnedTextPlanner(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var text = scenario switch
            {
                "long-line" => new string('x', 2_100_000) + "\nsmall\n",
                "many-lines" => string.Concat(Enumerable.Repeat("small line\r\n", 210_000)),
                _ => string.Concat(Enumerable.Repeat("😀é\n", 400_000))
            };
            Assert.True(Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024);
            var path = Path.Combine(root, "large.txt");
            await File.WriteAllTextAsync(path, text);
            var tools = new CodingTools(root);
            var last = text.Split('\n').Length;
            foreach (var (offset, limit) in new (int, int?)[]
            {
                (1, null), (1, 1), (1, 2001), (2, 3), (last - 2, null), (last, 1)
            })
            {
                var expected = ReadTextPlanner.Select(text, "large.txt", offset, limit);
                Assert.Equal(expected, await tools.Read("large.txt", offset, limit));
            }
            await Assert.ThrowsAsync<ToolFailureException>(() => tools.Read("large.txt", last + 1));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LargeUtf8ReadPreservesBomLikePinnedNodeDecoder()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-read-bom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var text = "\uFEFF" + string.Concat(Enumerable.Repeat(new string('x', 1_300) + "\n", 1_700));
            Assert.True(Encoding.UTF8.GetByteCount(text) > 2 * 1024 * 1024);
            await File.WriteAllTextAsync(Path.Combine(root, "bom.txt"), text);
            Assert.Equal(ReadTextPlanner.Select(text, "bom.txt"), await new CodingTools(root).Read("bom.txt"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
