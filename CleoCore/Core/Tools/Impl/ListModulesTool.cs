using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable): lists registered modules with
// their id, version, activity, whether the model may request them, and what
// they provide. This is how the "model can request a capability" story starts:
// the system prompt advertises inactive-but-requestable modules, and the model
// calls module_enable to activate one (Phase 3 wires the requestable path).
internal sealed class ListModulesTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "list_modules";
    public string Description =>
        "Lists this host's capability modules: id, version, active?, whether " +
        "the model may request the module, and what tools/services it " +
        "provides. Use this to see which capabilities are loaded.";
    public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
          },
          "additionalProperties": false
        }
        """;

    public ListModulesTool(ModuleManager manager)
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
        sb.Append(statuses.Count).Append(" module(s):").AppendLine();

        foreach (ModuleStatus status in statuses)
        {
            sb.Append("[module] ").AppendLine(status.Id);
            sb.Append("  version:    ").AppendLine(status.Manifest.Version);
            sb.Append("  active:     ").AppendLine(status.Active ? "yes" : "no");
            sb.Append("  requestable:").AppendLine(status.Manifest.ModelRequestable ? "yes" : "no");
            sb.Append("  tools:      ").Append(status.ToolCount).AppendLine();
            sb.Append("  requires:   ").AppendLine(Join(status.Manifest.Requires));
            sb.Append("  provides:   ").AppendLine(Join(status.Manifest.Provides));
            sb.Append("  description:").Append(' ').AppendLine(status.Manifest.Description);
        }

        return new ToolResult(call.Id, sb.ToString().TrimEnd());
    }

    public string Describe(ToolCall call) => "Listing capability modules";

    private static string Join(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return "(none)";
        }

        var sb = new StringBuilder();
        bool first = true;
        foreach (string item in items)
        {
            if (!first)
            {
                sb.Append(", ");
            }
            sb.Append(item);
            first = false;
        }
        return sb.ToString();
    }
}