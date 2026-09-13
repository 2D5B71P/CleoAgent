using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable): deactivates an active module so
// its tools disappear from the NEXT turn. Logical activation (design s6): the
// request is queued and applied at the next turn boundary - no mid-turn
// registry races. Always-on modules (devtools) are not model-requestable and
// refuse here; an operator who wants a capability fully off uses the
// allowlist, not module_disable.
internal sealed class ModuleDisableTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "module_disable";
    public string Description =>
        "Deactivates an active capability module so its tools disappear from " +
        "the next turn. Only model-requestable modules can be disabled; " +
        "always-on modules (e.g. devtools) refuse. Deactivation takes effect " +
        "NEXT TURN - the current turn's tool set is never changed mid-flight.";
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
        string? moduleId = GetStringArgument(call, "module_id");
        if (moduleId is null)
        {
            return new ToolResult(call.Id, "Missing required argument: module_id", true);
        }

        string? refusal = _manager.RequestDisable(moduleId);
        if (refusal is not null)
        {
            return new ToolResult(call.Id, refusal, true);
        }

        return new ToolResult(
            call.Id,
            string.Format(
                "module \"{0}\" is deactivating: its tools will disappear from " +
                "the NEXT turn.", moduleId));
    }

    public string Describe(ToolCall call) => "Disabling module " + DescribeId(call);

    private static string DescribeId(ToolCall call)
    {
        string? id = GetStringArgument(call, "module_id");
        return id is null ? "(no module_id)" : "\"" + id + "\"";
    }

    private static string? GetStringArgument(ToolCall call, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            if (!document.RootElement.TryGetProperty(name, out JsonElement element))
            {
                return null;
            }
            return element.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}