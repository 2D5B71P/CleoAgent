using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable). Phase 2 stub: activation is
// wired in Phase 3 (allowlist from the [modules] config section + next-turn
// logical activation). It is registered now so the model-facing surface is
// stable and errors are loud instead of "unknown tool".
internal sealed class ModuleEnableTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "module_enable";
    public string Description =>
        "Activates a dormant module so its tools appear from the next turn. " +
        "(Subject to the operator allowlist.) Not yet wired: activation " +
        "arrives in Phase 3 of the module system; every registered built-in is " +
        "currently active by default.";
    public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "module_id": {
              "type": "string",
              "description": "The module id to activate (see list_modules)."
            }
          },
          "required": ["module_id"],
          "additionalProperties": false
        }
        """;

    public ModuleEnableTool(ModuleManager manager)
    {
        _manager = manager;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        return new ToolResult(
            call.Id,
            "module activation is not wired yet (Phase 3). Every registered " +
            "built-in module is active by default; see list_modules.",
            true);
    }

    public string Describe(ToolCall call) => "Enabling a module (not wired yet)";
}