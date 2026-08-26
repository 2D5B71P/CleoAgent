using CleoAgent.Core.Tools;
using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Model
{
    internal sealed record ModelRequest(
        string? Prompt,
        ToolResult? ToolResult,
        IReadOnlyList<ToolDefinition> Tools,
        string? ContinuationToken,
        string? AgentId = null,
        bool Ephemeral = false)
    {
        // The owning agent; used by context/memory sources to scope state.
        public string ResolvedAgentId => string.IsNullOrWhiteSpace(AgentId) ? "default" : AgentId!;

        public static ModelRequest ForPrompt(
            string prompt,
            IReadOnlyList<ToolDefinition> tools,
            string? continuationToken = null,
            string? agentId = null,
            bool ephemeral = false)
        {
            return new ModelRequest(prompt, null, tools, continuationToken, agentId, ephemeral);
        }

        public static ModelRequest ForToolResult(
            ToolResult result,
            IReadOnlyList<ToolDefinition> tools,
            string? continuationToken = null,
            string? agentId = null)
        {
            return new ModelRequest(null, result, tools, continuationToken, agentId);
        }
    }
}
