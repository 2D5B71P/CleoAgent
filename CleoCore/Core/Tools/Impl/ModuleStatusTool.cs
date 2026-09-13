using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable): reports WHY each module is in
// its state. Phase 2 has no operator/model activation - every registered
// built-in is active by default, so the reason is "registered builtin,
// default-active". Phase 3 fills in config-default vs model-requested as
// the real causes.
internal sealed class ModuleStatusTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "module_status";
    public string Description =>
        "Reports why each capability module is in its state (active or not). " +
        "Use before enabling/disabling a module to understand the current " +
        "configuration.";
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
              .AppendLine(Reason(status));
        }

        return new ToolResult(call.Id, sb.ToString().TrimEnd());
    }

    public string Describe(ToolCall call) => "Reporting module status";

    private static string Reason(ModuleStatus status)
    {
        if (!status.Active)
        {
            return "not loaded (failed or not registered for load)";
        }
        if (!status.Manifest.ModelRequestable)
        {
            return "registered builtin, always on (not model-requestable)";
        }
        return "registered builtin, default-active";
    }
}