using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal class ListDirectoryTool : IAgentTool
    {
        public string Name => "list_directory";
        public string Description => "Enumerate files and directories in a specified path.";

        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "The path to the directory to search."
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

                if (!Directory.Exists(path))
                {
                    return new ToolResult(call.Id, $"Directory not found: {path}", true);
                }

                var directories = Directory.EnumerateDirectories(path).OrderBy(path => path);
                var files       = Directory.EnumerateFiles(path).OrderBy(path => path);

                var output = new StringBuilder();

                foreach (var directory in directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    output.Append("[dir]  ");
                    output.AppendLine(Path.GetFileName(directory));
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    output.Append("[file] ");
                    output.AppendLine(Path.GetFileName(file));
                }

                return new ToolResult(call.Id, output.Length == 0
                            ? "(empty directory)"
                            : output.ToString());
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
                ? "Listing a directory"
                : $"Listing {path}";
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
