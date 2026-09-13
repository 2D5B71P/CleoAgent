using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Modules;

// Identity + metadata for a capability module. Mirrors the module.json a
// future external module would ship (kind/trust fields arrive with external
// loading); static built-ins carry the same manifest shape so the contract is
// identical whether a module is linked in or discovered on disk.
internal sealed record ModuleManifest(
    string Id,
    string Version,
    string Description,
    IReadOnlyList<string> Requires,     // "core" = the host kernel (always satisfied)
    IReadOnlyList<string> Provides,     // tool names / service names this module registers
    string? ConfigSection,              // legacy top-level config section name ("web", "embedding"); null = no config surface
    bool DefaultActive = true,
    bool ModelRequestable = false)
{
    public static ModuleManifest Create(
        string id,
        string version,
        string description,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<string>? provides = null,
        string? configSection = null,
        bool defaultActive = true,
        bool modelRequestable = false)
    {
        return new ModuleManifest(
            id,
            version,
            description,
            requires ?? new List<string>(),
            provides ?? new List<string>(),
            configSection,
            defaultActive,
            modelRequestable);
    }
}