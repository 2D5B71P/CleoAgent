using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Config;

namespace CleoAgent.Core.Modules;

// A module's view over config.json. Since 2026-09-13 the module-owned
// "modules.<id>" block is the ONLY config surface - the legacy top-level
// sections ("embedding", "web") were removed with the pre-modular config.
// Missing sections are fine (defaults apply); zero config behaves exactly
// like today.
internal sealed class ConfigSection
{
    private readonly string _sectionPath;   // "modules.<id>" actually read
    private readonly bool _configured;      // true when the section exists

    private ConfigSection(string sectionPath, bool configured)
    {
        _sectionPath = sectionPath;
        _configured = configured;
    }

    // Read-only empty section for modules with no declared config surface so
    // ModuleContext.Config never needs null handling.
    public static ConfigSection Empty => new("", false);

    // Builds the section for a module manifest. Every module's config lives at
    // "modules.<id>" - no fallback to a legacy root section anymore.
    public static ConfigSection? ForModule(string moduleId)
    {
        string path = "modules." + moduleId;
        return new ConfigSection(path, CleoAgent.Core.Config.Config.SectionExists(path));
    }

    public bool IsConfigured => _configured;

    public string GetString(string key, string fallback = "") =>
        CleoAgent.Core.Config.Config.SectionString(_sectionPath, key) ?? fallback;

    public int GetInt(string key, int fallback) =>
        CleoAgent.Core.Config.Config.SectionInt(_sectionPath, key) ?? fallback;

    public bool GetBool(string key, bool fallback) =>
        CleoAgent.Core.Config.Config.SectionBool(_sectionPath, key) ?? fallback;

    // Reads a key inside a nested sub-block of the module's section:
    //   cfg.GetStringAt("fetch", "provider") -> "modules.web.fetch.provider"
    // Keeps "modules.<id>" as the section root while allowing structured
    // sub-blocks (the web module's fetch/search split).
    public string GetStringAt(string subPath, string key, string fallback = "") =>
        CleoAgent.Core.Config.Config.SectionString(_sectionPath + "." + subPath, key) ?? fallback;

    public int GetIntAt(string subPath, string key, int fallback) =>
        CleoAgent.Core.Config.Config.SectionInt(_sectionPath + "." + subPath, key) ?? fallback;

    public bool GetBoolAt(string subPath, string key, bool fallback) =>
        CleoAgent.Core.Config.Config.SectionBool(_sectionPath + "." + subPath, key) ?? fallback;
}