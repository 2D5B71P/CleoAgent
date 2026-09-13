using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// Kernel tool (always on, never unloadable): activates a dormant-but-
// requestable module. Activation is LOGICAL and takes effect NEXT TURN (design
// s6): the request is queued by ModuleManager and applied at the next turn
// boundary, so the current turn's registry snapshot never races a mid-turn
// change. Subject to the operator allowlist - a denied module reports the
// denial clearly and can never be enabled by the model (resolution 2).
internal sealed class ModuleEnableTool : IAgentTool
{
    private readonly ModuleManager _manager;

    public string Name => "module_enable";
    public string Description =>
        "Activates a dormant capability module so its tools appear from the " +
        "next turn. Subject to the operator allowlist: a module denied by " +
        "the operator cannot be enabled. See list_modules for ids and which " +
        "modules are requestable. Activation takes effect NEXT TURN - the " +
        "current turn's tool set is never changed mid-flight.";
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
        string? moduleId = GetStringArgument(call, "module_id");
        if (moduleId is null)
        {
            return new ToolResult(call.Id, "Missing required argument: module_id", true);
        }

        string? refusal = _manager.RequestEnable(moduleId);
        if (refusal is not null)
        {
            return new ToolResult(call.Id, refusal, true);
        }

        return new ToolResult(
            call.Id,
            string.Format(
                "module \"{0}\" is activating: its tools will be available " +
                "from the NEXT turn.", moduleId));
    }

    public string Describe(ToolCall call) => "Enabling module " + DescribeId(call);

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