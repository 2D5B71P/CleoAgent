using System;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class MoveFileTool : IAgentTool
    {
        public string Name => "move_file";
        public string Description =>
            "Moves (relocates) a file or directory from source to destination. Also " +
            "used to rename when source and destination share the same parent. " +
            "Use rename_file for a clearer single-path rename, or move_file with an " +
            "explicit destination for relocating to another folder.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "source": {
              "type": "string",
              "description": "Path of the file or directory to move."
            },
            "destination": {
              "type": "string",
              "description": "Destination path (new location and/or new name)."
            }
          },
          "required": ["source", "destination"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("source", out var sourceElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: source", true);
                }
                if (!document.RootElement.TryGetProperty("destination", out var destinationElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: destination", true);
                }

                var source      = sourceElement.GetString();
                var destination = destinationElement.GetString();

                if (string.IsNullOrWhiteSpace(source))
                {
                    return new ToolResult(call.Id, "Source cannot be empty.", true);
                }
                if (string.IsNullOrWhiteSpace(destination))
                {
                    return new ToolResult(call.Id, "Destination cannot be empty.", true);
                }
                bool sourceIsDir = Directory.Exists(source) && !File.Exists(source);

                if (!File.Exists(source) && !Directory.Exists(source))
                {
                    return new ToolResult(call.Id, $"Source not found: {source}", true);
                }

                if (!sourceIsDir)
                {
                    // Ensure the destination's parent exists so a move can target a
                    // not-yet-existing subfolder.
                    var destDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        await Task.Run(() => Directory.CreateDirectory(destDir), cancellationToken);
                    }
                }

                await Task.Run(() =>
                {
                    if (sourceIsDir)
                    {
                        Directory.Move(source, destination);
                    }
                    else
                    {
                        File.Move(source, destination, overwrite: false);
                    }
                }, cancellationToken);
                return new ToolResult(call.Id, $"Moved {source} -> {destination}");
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            var (source, destination) = (GetStringArgument(call, "source"), GetStringArgument(call, "destination"));
            if (string.IsNullOrWhiteSpace(source))
            {
                return "Moving a file";
            }
            return string.IsNullOrWhiteSpace(destination)
                ? $"Moving {source}"
                : $"Moving {source} -> {destination}";
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