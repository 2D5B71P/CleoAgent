using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable). Phase 2 stub: deactivation is
// wired in Phase 3. Registered now for a stable model-facing surface; errors
// loudly instead of surfacing as "unknown tool".
internal sealed class ModuleDisableTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "module_disable";
    public string Description =>
        "Deactivates an active module so its tools disappear from the next " +
        "turn. Not yet wired: activation/deactivation arrives in Phase 3 of " +
        "the module system; every registered built-in is currently active by " +
        "default.";
    public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "module_id": {
              "type": "string",
              "description": "The module id to deactivate (see list_modules)."
            }
          },
          "required": ["module_id"],
          "additionalProperties": false
        }
        """;

    public ModuleDisableTool(ModuleManager manager)
    {
        _manager = manager;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        return new ToolResult(
            call.Id,
            "module deactivation is not wired yet (Phase 3). Every registered " +
            "built-in module is active by default; see list_modules.",
            true);
    }

    public string Describe(ToolCall call) => "Disabling a module (not wired yet)";
}