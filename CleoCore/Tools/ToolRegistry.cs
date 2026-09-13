using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Tools
{
    internal sealed class ToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> m__Tools = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<IAgentTool> Tools => m__Tools.Values;

        public ToolRegistry() { }

        public ToolRegistry(params IAgentTool[] registerTools)
        {
            m__Tools = new Dictionary<string, IAgentTool>(registerTools.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var tool in registerTools)
            {
                m__Tools[tool.Name] = tool;
            }
        }

        public void Register(IAgentTool tool)
        {
            m__Tools[tool.Name] = tool;
        }

        public bool TryGetValue(string name, out IAgentTool? tool)
        {
            return m__Tools.TryGetValue(name, out tool);
        }

        // Tool names in registration order (dictionary Keys are not iterable
        // in this dialect; the tools already carry their names).
        public IReadOnlyList<string> Names =>
            m__Tools.Values.Select(tool => tool.Name).ToList();

        // Unregisters a tool by name; used when a module fails to load (its
        // partial registrations are rolled back) and by future module
        // deactivation. Returns true when the tool was present.
        public bool Unregister(string name)
        {
            return m__Tools.Remove(name);
        }

        public IReadOnlyList<ToolDefinition> GetDefinitions()
        {
            return m__Tools.Values
                .Select(tool => new ToolDefinition(
                    tool.Name,
                    tool.Description,
                    tool.ParametersJson))
                .ToList();
        }

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            if (!m__Tools.TryGetValue(call.Name, out var tool)) {
                return new ToolResult(call.Id, $"Unknown tool: {call.Name}", true);
            }

            return await tool.ExecuteAsync(call, cancellationToken);
        }
    }
}
