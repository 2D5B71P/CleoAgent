using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class WriteFileTool : IAgentTool
    {
        public string Name => "write_file";
        public string Description => "Creates or overwrites a new file.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Path of new file."
            },
            "content": {
              "type": "string",
              "description": "Contents for new file. If empty, the output file will be empty."
            }
          },
          "required": ["path"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("path", out var pathElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: path", true);
                }

                bool hasContent = document.RootElement.TryGetProperty("content", out var contentElement);

                var path = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult(call.Id, "Path cannot be empty.", true);
                }

                await File.WriteAllTextAsync(path, hasContent? contentElement.GetString() : null, cancellationToken);
                return new ToolResult(call.Id, "File written succesfully");
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? path = GetStringArgument(call, "path");
            return string.IsNullOrWhiteSpace(path)
                ? "Writing a file"
                : $"Writing {path}";
        }

        private static string? GetStringArgument(ToolCall call, string name)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                return document.RootElement.TryGetProperty(name, out var element)
                    ? element.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}