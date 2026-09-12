using System.Linq;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class RemoveDirectoryTool : IAgentTool
    {
        public string Name => "remove_dir";
        public string Description =>
            "Deletes an EMPTY directory. Fails if the directory is not empty " +
            "(to guard against accidental data loss). For non-empty trees, the model " +
            "should empty them first with remove_file/move_file, or reconsider.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Path of the (empty) directory to delete."
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

                if (Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return new ToolResult(call.Id, $"Directory not empty, refusing to delete: {path}", true);
                }

                await Task.Run(() => Directory.Delete(path), cancellationToken);
                return new ToolResult(call.Id, $"Deleted directory {path}");
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
                ? "Deleting a directory"
                : $"Deleting directory {path}";
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