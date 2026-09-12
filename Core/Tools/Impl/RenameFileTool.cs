using System;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class RenameFileTool : IAgentTool
    {
        public string Name => "rename_file";
        public string Description =>
            "Renames a file or directory in place (same parent folder). For relocating " +
            "to a different folder, use move_file instead.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Current path of the file or directory."
            },
            "new_name": {
              "type": "string",
              "description": "New name (just the name, not a full path)."
            }
          },
          "required": ["path", "new_name"],
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
                if (!document.RootElement.TryGetProperty("new_name", out var newNameElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: new_name", true);
                }

                var path    = pathElement.GetString();
                var newName = newNameElement.GetString();

                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult(call.Id, "Path cannot be empty.", true);
                }
                if (string.IsNullOrWhiteSpace(newName))
                {
                    return new ToolResult(call.Id, "New name cannot be empty.", true);
                }

                var parent = Path.GetDirectoryName(path);
                var destination = string.IsNullOrEmpty(parent)
                    ? newName
                    : Path.Combine(parent, newName);

                bool isDir = Directory.Exists(path) && !File.Exists(path);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    return new ToolResult(call.Id, $"Path not found: {path}", true);
                }

                await Task.Run(() =>
                {
                    if (isDir)
                    {
                        Directory.Move(path, destination);
                    }
                    else
                    {
                        File.Move(path, destination);
                    }
                }, cancellationToken);
                return new ToolResult(call.Id, $"Renamed {path} -> {destination}");
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            var (path, newName) = (GetStringArgument(call, "path"), GetStringArgument(call, "new_name"));
            if (string.IsNullOrWhiteSpace(path))
            {
                return "Renaming a file";
            }
            return string.IsNullOrWhiteSpace(newName)
                ? $"Renaming {path}"
                : $"Renaming {path} to {newName}";
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