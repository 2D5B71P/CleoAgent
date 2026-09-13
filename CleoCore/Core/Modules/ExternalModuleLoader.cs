using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CleoAgent.Core.Config;

namespace CleoAgent.Core.Modules;

// Reads and validates one external module directory (design s7, tier 2:
// agent-authored, script-backed). Layout:
//
//   <modules-root>/<id>/module.json        manifest (described below)
//   <modules-root>/<id>/...                scripts the tools invoke
//
// module.json is JSON5 (comments allowed, ${ENV} interpolated - same authoring
// surface as config.json):
//
//   {
//     "id": "weather",
//     "kind": "external",                 // "compiled"/"dll" refused (Phase 5)
//     "version": "1.0.0",
//     "description": "Weather helpers",
//     "author": "Cleo",                   // audit fields (Phase 4)
//     "created": "2026-09-13",
//     "modified": "2026-09-13",
//     "requires": ["core"],               // module ids; unknown -> refuse by name
//     "activation": { "default": true, "model_requestable": true },
//     "tools": [
//       {
//         "name": "weather_now",
//         "description": "Current weather for a city",
//         "parameters": { "type": "object", "properties": {...} },   // JSON schema
//         "impl": { "command": ["python", "weather.py"] }
//       }
//     ]
//   }
//
// Tool-call contract (out-of-process, same primitives as run_command): the
// host spawns impl.command with the working directory = module dir, pipes the
// tool-call arguments JSON to the child's stdin, and returns its stdout as the
// tool result. Exit code 0 = success; non-zero = error (stderr attached).
// The agent's own code NEVER runs inside the host process.
internal static class ExternalModuleLoader
{
    // Reserved for the host kernel (design s5: always on, never unloadable).
    // An external module claiming one would silently lose to the kernel tool
    // registered after LoadAllAsync in Program.cs - refuse by name instead.
    private static readonly string[] s_KernelToolNames =
    {
        "list_modules", "module_status", "module_enable", "module_disable"
    };

    // Parses the manifest inside one module directory. Returns the module, or
    // null with a named reason (the caller reports it and keeps going - a bad
    // module must never take the host down, design s7).
    public static ExternalModule? Load(string moduleDir, string expectedId, out string? error)
    {
        error = null;
        string manifestPath = Path.Combine(moduleDir, "module.json");

        if (!File.Exists(manifestPath))
        {
            error = "missing module.json";
            return null;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            error = "cannot read module.json: " + ex.Message;
            return null;
        }

        try
        {
            string valid = CleoAgent.Core.Config.Config.StripJsonComments(raw);
            using JsonDocument document = JsonDocument.Parse(valid);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "module.json must be a JSON object";
                return null;
            }

            // kind: only script-backed external modules load. Contract says the
            // field stays for later kinds, but nothing else is accepted yet.
            if (root.TryGetProperty("kind", out JsonElement kindElement) && kindElement.ValueKind == JsonValueKind.String)
            {
                string kind = kindElement.GetString() ?? "";
                if (kind != "" && kind != "external")
                {
                    error = string.Format(
                        "kind \"{0}\" is not loadable: only script-backed external " +
                        "modules are supported (compiled plugins are a later phase).", kind);
                    return null;
                }
            }

            string id = ReadRequiredString(root, "id", out error);
            if (id is null)
            {
                return null;
            }
            id = id.Trim();
            if (id != expectedId)
            {
                error = string.Format(
                    "module.json id \"{0}\" does not match the module directory \"{1}\"", id, expectedId);
                return null;
            }

            string version = ReadRequiredString(root, "version", out error);
            if (version is null)
            {
                return null;
            }

            string description = ReadOptionalString(root, "description") ?? "";
            string? author = ReadOptionalString(root, "author");
            string? created = ReadOptionalString(root, "created");
            string? modified = ReadOptionalString(root, "modified");

            var requires = new List<string>();
            if (root.TryGetProperty("requires", out JsonElement requiresElement) && requiresElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in requiresElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var required = item.GetString();
                        if (required is not null && required.Length > 0)
                        {
                            requires.Add(required);
                        }
                    }
                }
            }

            bool defaultActive = true;
            bool modelRequestable = true;
            if (root.TryGetProperty("activation", out JsonElement activationElement) && activationElement.ValueKind == JsonValueKind.Object)
            {
                if (activationElement.TryGetProperty("default", out JsonElement def))
                {
                    if (def.ValueKind == JsonValueKind.True)
                        defaultActive = true;
                    else if (def.ValueKind == JsonValueKind.False)
                        defaultActive = false;
                }
                if (activationElement.TryGetProperty("model_requestable", out JsonElement mr))
                {
                    if (mr.ValueKind == JsonValueKind.True)
                        modelRequestable = true;
                    else if (mr.ValueKind == JsonValueKind.False)
                        modelRequestable = false;
                }
            }

            // Tools: at least one, each with a valid name, description, and an
            // impl.command argv. Tool names are exposed verbatim to the model,
            // so validate them like any other tool name (lowercase snake).
            if (!root.TryGetProperty("tools", out JsonElement toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
            {
                error = "module.json must declare a \"tools\" array";
                return null;
            }

            var tools = new List<ExternalToolSpec>();
            var seenNames = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var provides = new List<string>();

            foreach (JsonElement toolElement in toolsElement.EnumerateArray())
            {
                if (toolElement.ValueKind != JsonValueKind.Object)
                {
                    error = "each tool must be a JSON object";
                    return null;
                }

                string toolName = ReadRequiredString(toolElement, "name", out error);
                if (toolName is null)
                {
                    return null;
                }
                toolName = toolName.Trim();

                if (!IsValidToolName(toolName))
                {
                    error = string.Format(
                        "tool name \"{0}\" is not a valid tool name (lowercase letters, digits, underscores)", toolName);
                    return null;
                }

                if (seenNames.ContainsKey(toolName))
                {
                    error = string.Format("duplicate tool name \"{0}\"", toolName);
                    return null;
                }
                seenNames[toolName] = true;

                if (IsKernelToolName(toolName))
                {
                    error = string.Format(
                        "tool name \"{0}\" is reserved for the host kernel", toolName);
                    return null;
                }

                string toolDescription = ReadRequiredString(toolElement, "description", out error);
                if (toolDescription is null)
                {
                    return null;
                }

                string parametersJson = "{\"type\":\"object\"}";
                if (toolElement.TryGetProperty("parameters", out JsonElement parametersElement))
                {
                    if (parametersElement.ValueKind != JsonValueKind.Object)
                    {
                        error = string.Format("tool \"{0}\": \"parameters\" must be a JSON schema object", toolName);
                        return null;
                    }
                    parametersJson = parametersElement.ToString();
                }

                if (!toolElement.TryGetProperty("impl", out JsonElement implElement) || implElement.ValueKind != JsonValueKind.Object)
                {
                    error = string.Format("tool \"{0}\": missing \"impl\" object", toolName);
                    return null;
                }

                if (!implElement.TryGetProperty("command", out JsonElement commandElement) || commandElement.ValueKind != JsonValueKind.Array)
                {
                    error = string.Format("tool \"{0}\": impl.command must be an array (executable + args)", toolName);
                    return null;
                }

                var command = new List<string>();
                foreach (JsonElement argElement in commandElement.EnumerateArray())
                {
                    if (argElement.ValueKind == JsonValueKind.String)
                    {
                        var arg = argElement.GetString();
                        if (arg is not null)
                        {
                            command.Add(arg);
                        }
                    }
                }
                if (command.Count == 0)
                {
                    error = string.Format("tool \"{0}\": impl.command cannot be empty", toolName);
                    return null;
                }

                tools.Add(new ExternalToolSpec(
                    toolName,
                    toolDescription,
                    parametersJson,
                    command));
                provides.Add(toolName);
            }

            if (tools.Count == 0)
            {
                error = "module.json must declare at least one tool";
                return null;
            }

            var manifest = ModuleManifest.Create(
                id,
                version,
                description,
                requires: requires,
                provides: provides,
                configSection: null,
                defaultActive: defaultActive,
                modelRequestable: modelRequestable,
                author: author,
                created: created,
                modified: modified);

            return new ExternalModule(manifest, tools, moduleDir);
        }
        catch (JsonException ex)
        {
            error = "module.json is not valid JSON: " + ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            error = "cannot parse module.json: " + ex.Message;
            return null;
        }
    }

    private static bool IsValidToolName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool lower = c >= 'a' && c <= 'z';
            bool digit = c >= '0' && c <= '9';
            bool underscore = c == '_';
            if (!lower && !(digit && i > 0) && !(underscore && i > 0))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsKernelToolName(string name)
    {
        foreach (string kernel in s_KernelToolNames)
        {
            if (kernel == name)
            {
                return true;
            }
        }
        return false;
    }

    private static string? ReadRequiredString(JsonElement root, string name, out string? error)
    {
        // Out-params need up-front assignment: flow analysis can't prove the
        // failure branch always runs before the success return.
        error = null;

        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            error = "missing required field \"" + name + "\"";
            return null;
        }
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "field \"" + name + "\" cannot be empty";
            return null;
        }
        return value;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }
}