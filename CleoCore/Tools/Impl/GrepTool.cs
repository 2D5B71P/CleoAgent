using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    // Regex not neccesary for now.
    internal sealed class GrepTool : IAgentTool
    {
        public string Name => "grep";
        public string Description => "Searches for the text pattern inside a specific file (case-insensitive).";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Text pattern to be matched."
            },
            "path": {
              "type": "string",
              "description": "Path to file."
            }
          },
          "required": ["query", "path"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                if (!document.RootElement.TryGetProperty("query", out var queryElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: query", true);
                }
                var query = queryElement.GetString();
                if (string.IsNullOrWhiteSpace(query))
                {
                    return new ToolResult(call.Id, "Query cannot be empty.", true);
                }

                if (!document.RootElement.TryGetProperty("path", out var pathElement))
                {
                    return new ToolResult(call.Id, "Missing required argument: path", true);
                }
                var path    = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    return new ToolResult(call.Id, "Path cannot be empty.", true);
                }

                var output = new StringBuilder();
                await foreach (string line in File.ReadLinesAsync(path, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        output.Append("[at line]  ");
                        output.AppendLine(line);
                    }
                }

                return new ToolResult(call.Id, output.Length == 0
                            ? "(no matches)"
                            : output.ToString());
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            string? query = GetStringArgument(call, "query");
            string? path  = GetStringArgument(call, "path");

            if (string.IsNullOrWhiteSpace(path))
            {
                return "Searching a file";
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return $"Searching {path}";
            }

            return $"Searching\"{query}\" in {path}";
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
