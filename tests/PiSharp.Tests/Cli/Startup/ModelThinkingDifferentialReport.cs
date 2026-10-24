using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Tests;

internal static class ModelThinkingDifferentialReport
{
    private static readonly object s_gate = new();

    public static void Record(string repository, string outputVariable, string sourceName, string id,
        JsonNode? piSharpResult)
    {
        var outputPath = Environment.GetEnvironmentVariable(outputVariable);
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        var sourcePath = Path.Combine(repository, "docs", "parity", "fixtures", sourceName);
        lock (s_gate)
        {
            var report = File.Exists(outputPath)
                ? JsonNode.Parse(File.ReadAllText(outputPath))!.AsObject()
                : JsonNode.Parse(File.ReadAllText(sourcePath))!.AsObject();
            var item = report["cases"]!.AsArray().Select(node => node!.AsObject())
                .Single(candidate => candidate["id"]!.GetValue<string>() == id);
            item["piSharpResult"] = piSharpResult?.DeepClone();
            item["classification"] = "strict-match";
            if (item["provenance"] is JsonObject provenance &&
                Environment.GetEnvironmentVariable("PISHARP_DIFFERENTIAL_SHA") is { Length: > 0 } sha)
                provenance["piSharpSha"] = sha;

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            Directory.CreateDirectory(directory);
            var temporary = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
                File.Move(temporary, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
