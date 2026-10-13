using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Tools;

/// <summary>Preserves Pi's tool schemas and descriptions independently of execution.</summary>
internal static class CodingToolDeclarations
{
    private static readonly JsonElement s_readToolSchema = CreateReadToolSchema();
    private static readonly JsonElement s_bashToolSchema = CreateBashToolSchema();
    private static readonly JsonElement s_writeToolSchema = CreateWriteToolSchema();
    private static readonly JsonElement s_editToolSchema = CreateEditToolSchema();
    private const string BashToolDescription = "Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.";
    private const string WriteToolDescription = "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.";
    private const string EditToolDescription = "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.";

    public static AIFunction Read(AIFunction implementation) => new ReadToolFunction(implementation);
    public static AIFunction Bash(AIFunction implementation) => new BashToolFunction(implementation);
    public static AIFunction Write(AIFunction implementation) => new WriteToolFunction(implementation);
    public static AIFunction Edit(AIFunction implementation) => new EditToolFunction(implementation);

    private static JsonElement CreateReadToolSchema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["path"],
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Path to the file to read (relative or absolute)"
                },
                "offset": {
                  "type": "number",
                  "description": "Line number to start reading from (1-indexed)"
                },
                "limit": {
                  "type": "number",
                  "description": "Maximum number of lines to read"
                }
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateBashToolSchema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["command"],
              "properties": {
                "command": {
                  "type": "string",
                  "description": "Shell command to execute"
                },
                "timeout": {
                  "type": "number",
                  "description": "Timeout in seconds (optional, no default timeout)"
                }
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateWriteToolSchema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["path", "content"],
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Path to the file to write (relative or absolute)"
                },
                "content": {
                  "type": "string",
                  "description": "Content to write to the file"
                }
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private static JsonElement CreateEditToolSchema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "required": ["path", "edits"],
              "properties": {
                "path": {
                  "type": "string",
                  "description": "Path to the file to edit (relative or absolute)"
                },
                "edits": {
                  "type": "array",
                  "description": "One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead.",
                  "items": {
                    "type": "object",
                    "required": ["oldText", "newText"],
                    "properties": {
                      "oldText": {
                        "type": "string",
                        "description": "Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call."
                      },
                      "newText": {
                        "type": "string",
                        "description": "Replacement text for this targeted edit."
                      }
                    }
                  }
                }
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed class ReadToolFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        public override JsonElement JsonSchema => s_readToolSchema;
    }

    private sealed class BashToolFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        public override string Description => BashToolDescription;
        public override JsonElement JsonSchema => s_bashToolSchema;
    }

    private sealed class WriteToolFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        public override string Description => WriteToolDescription;
        public override JsonElement JsonSchema => s_writeToolSchema;
    }

    private sealed class EditToolFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        public override string Description => EditToolDescription;
        public override JsonElement JsonSchema => s_editToolSchema;
    }

}
