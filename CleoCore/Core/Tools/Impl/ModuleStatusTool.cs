using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable): reports WHY each module is in
// its state (config default / model request / operator deny / load failure /
// pending next-turn change). This is the diagnostic companion to list_modules
// and the place the model checks before calling module_enable/module_disable.
internal sealed class ModuleStatusTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "module_status";
    public string Description =>
        "Reports why each capability module is in its state (active or not): " +
        "config default, model request (pending or applied), operator " +
        "allowlist deny, or load failure. Use this before enabling/disabling " +
        "a module to understand its current configuration.";
    public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
          },
          "additionalProperties": false
        }
        """;

    public ModuleStatusTool(ModuleManager manager)
    {
        _manager = manager;
    }

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        var statuses = _manager.Status();

        if (statuses.Count == 0)
        {
            return new ToolResult(call.Id, "No modules registered.");
        }

        var sb = new StringBuilder();
        sb.Append("Active ").Append(_manager.ActiveCount()).Append(" of ")
          .Append(statuses.Count).Append(" module(s):").AppendLine();

        foreach (ModuleStatus status in statuses)
        {
            sb.Append("[module] ").Append(status.Id).Append(": ")
              .Append(status.Active ? "active" : "inactive").Append(" - ")
              .AppendLine(status.Reason);
        }

        return new ToolResult(call.Id, sb.ToString().TrimEnd());
    }

    public string Describe(ToolCall call) => "Reporting module status";
}