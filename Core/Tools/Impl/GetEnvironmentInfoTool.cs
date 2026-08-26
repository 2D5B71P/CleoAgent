using CleoAgent.Core.Env;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal class GetEnvironmentInfoTool : IAgentTool
    {
        public string Name => "get_environment_info";
        public string Description => "Returns OS information; current .NET SDK information and environment variables.";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                var output = new StringBuilder();

                output.AppendLine($"+-- OS information: --+");
                output.AppendLine($"Description: {RuntimeInformation.OSDescription}");
                output.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");

                output.AppendLine($"+-- .NET information: --+");
                output.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");

                output.AppendLine($"+-- Registered environment variables (names only) --+");
                // Names only, never values: keeps credentials out of the model
                // and out of the conversation history entirely.
                foreach (string key in EnvLoader.RegisteredKeys())
                {
                    output.AppendLine(key);
                }

                var content = output.ToString();
                return new ToolResult(call.Id, content);
            }
            catch (Exception ex)
            {
                return new ToolResult(call.Id, ex.Message, true);
            }
        }

        public string Describe(ToolCall call)
        {
            return "Reading environment info";
        }
    }
}
