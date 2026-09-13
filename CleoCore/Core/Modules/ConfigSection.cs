using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Config;

namespace CleoAgent.Core.Modules;

// A module's view over config.json. Resolution order (design s8):
//   1. module-owned keys: the "modules.<id>" top-level block, if present;
//   2. legacy fallback: the manifest's ConfigSection name at the root
//      ("embedding", "web") - works with existing config files.
// When the legacy path is actually consulted, WarnIfLegacy() emits a one-time
// startup deprecation note. Missing sections are fine (defaults apply); zero
// config behaves exactly like today.
internal sealed class ConfigSection
{
    private readonly string _moduleId;
    private readonly string? _legacyName;
    private readonly string _activeSection;   // dotted path actually read (e.g. "modules.memory" or "embedding")
    private readonly bool _usedLegacy;        // true when the legacy root section was the source
    private readonly bool _configured;        // true when any section resolved

    private ConfigSection(string moduleId, string? legacyName, string activeSection, bool usedLegacy, bool configured)
    {
        _moduleId = moduleId;
        _legacyName = legacyName;
        _activeSection = activeSection;
        _usedLegacy = usedLegacy;
        _configured = configured;
    }

    // Read-only empty section for modules with no declared config surface so
    // ModuleContext.Config never needs null handling.
    public static ConfigSection Empty => new("", null, "", false, false);

    // Builds the section for a module manifest. Returns null when the module
    // declares no config surface (legacyName null) - nothing to read.
    public static ConfigSection? ForModule(string moduleId, string? legacyName)
    {
        if (legacyName is null)
        {
            return null;
        }
        var info = CleoAgent.Core.Config.Config.SectionInfo(legacyName, moduleId);

        bool moduleOwned = info.ModuleOwned;
        bool legacyPresent = info.LegacyPresent;

        return new ConfigSection(
            moduleId,
            legacyName,
            moduleOwned ? "modules." + moduleId : legacyName,
            /* usedLegacy: */ !moduleOwned && legacyPresent,
            /* configured:  */ moduleOwned || legacyPresent);
    }

    public bool IsConfigured => _configured;

    public string GetString(string key, string fallback = "") =>
        CleoAgent.Core.Config.Config.SectionString(_activeSection, key) ?? fallback;

    public int GetInt(string key, int fallback) =>
        CleoAgent.Core.Config.Config.SectionInt(_activeSection, key) ?? fallback;

    public bool GetBool(string key, bool fallback) =>
        CleoAgent.Core.Config.Config.SectionBool(_activeSection, key) ?? fallback;

    // Emits the one-time startup deprecation note when this module fell back
    // to a legacy top-level section. Called by ModuleManager after LoadAll.
    public void WarnIfLegacy()
    {
        if (_usedLegacy)
        {
            Console.Error.WriteLine(
                $"[config] module \"{_moduleId}\": using legacy top-level " +
                $"\"{_legacyName}\" section - migrate to \"modules.{_moduleId}\" (deprecation).");
        }
    }
}