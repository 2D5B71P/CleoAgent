using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class MakeDirectoryTool : IAgentTool
    {
        public string Name => "make_dir";
        public string Description =>
            "Creates a new directory (and any missing parent directories) at the given path. " +
            "Succeeds silently if the directory already exists.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Path of the directory to create."
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

                await Task.Run(() => Directory.CreateDirectory(path), cancellationToken);
                return new ToolResult(call.Id, $"Directory ready: {path}");
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
                ? "Creating a directory"
                : $"Creating directory {path}";
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