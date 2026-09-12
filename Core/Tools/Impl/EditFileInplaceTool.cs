using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    // Edits a file in place by locating a unique text block and replacing it,
    // avoiding a full read_file -> write_file round trip. The model must supply
    // an old_text that is unique enough to identify the target location.
    internal sealed class EditFileInplaceTool : IAgentTool
    {
        public string Name => "edit_file_inplace";
        public string Description =>
            "Edits a file in place by replacing a single occurrence of old_text with new_text. " +
            "Keeps the rest of the file untouched (unlike write_file, which overwrites everything). " +
            "old_text must appear exactly ONCE in the file; if it appears multiple times, provide " +
            "more surrounding context to make it unique. To REMOVE text, set new_text to an empty string.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "path": {
              "type": "string",
              "description": "Path of the file to edit."
            },
            "old_text": {
              "type": "string",
              "description": "Existing text to find. Must match exactly once; include enough surrounding context to be unique."
            },
            "new_text": {
              "type": "string",
              "description": "Replacement text. Use an empty string to delete old_text."
            }
          },
          "required": ["path", "old_text", "new_text"],
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
                if (!document.RootElement.TryGetProperty("old_text", out var oldTextElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: old_text", true);
                }
                if (!document.RootElement.TryGetProperty("new_text", out var newTextElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: new_text", true);
                }

                var path    = pathElement.GetString();
                var oldText = oldTextElement.GetString();
                var newText = newTextElement.GetString() ?? string.Empty;

                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult(call.Id, "Path cannot be empty.", true);
                }
                if (oldText is null)
                {
                    return new ToolResult(call.Id, "old_text cannot be null.", true);
                }

                if (!File.Exists(path))
                {
                    return new ToolResult(call.Id, $"File not found: {path}", true);
                }

                string content = await File.ReadAllTextAsync(path, cancellationToken);

                int first  = content.IndexOf(oldText, StringComparison.Ordinal);
                int second = first < 0 ? -1 : content.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal);

                if (first < 0)
                {
                    return new ToolResult(call.Id, $"old_text not found in file: {Shorten(oldText)}", true);
                }
                if (second >= 0)
                {
                    return new ToolResult(call.Id,
                        $"old_text matched MULTIPLE times ({CountOccurrences(content, oldText)}). " +
                        $"Include more surrounding context so it is unique. Match preview: {Shorten(oldText)}",
                        true);
                }

                string updated = content.Remove(first, oldText.Length).Insert(first, newText);

                await File.WriteAllTextAsync(path, updated, cancellationToken);
                return new ToolResult(call.Id,
                    $"Edited {path} (replaced 1 occurrence, {oldText.Length} -> {newText.Length} chars).");
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
                ? "Editing a file in place"
                : $"Editing {path} in place";
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        private static string Shorten(string s, int max = 60)
        {
            if (s is null) return string.Empty;
            var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat.Substring(0, max) + "…";
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