using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class RemoveFileTool : IAgentTool
    {
        public string Name => "remove_file";
        public string Description =>
            "Permanently deletes a single file. This cannot be undone, so the model " +
            "should only call it when confident and after confirming the path is correct.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Path of the file to delete."
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

                var path = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult(call.Id, "Path cannot be empty.", true);
                }

                if (!File.Exists(path))
                {
                    return new ToolResult(call.Id, $"File not found: {path}", true);
                }

                await Task.Run(() => File.Delete(path), cancellationToken);
                return new ToolResult(call.Id, $"Deleted {path}");
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
                ? "Deleting a file"
                : $"Deleting {path}";
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