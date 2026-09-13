using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Modules;

// Identity + metadata for a capability module. Mirrors the module.json an
// external module ships; static built-ins carry the same manifest shape so
// the contract is identical whether a module is linked in or discovered on
// disk. Config lives at "modules.<id>" for every module (2026-09-13: the
// legacy top-level config section names were removed with the pre-modular
// config, so there is no ConfigSection field anymore).
internal sealed record ModuleManifest(
    string Id,
    string Version,
    string Description,
    IReadOnlyList<string> Requires,     // "core" = the host kernel (always satisfied)
    IReadOnlyList<string> Provides,     // tool names / service names this module registers
    bool DefaultActive = true,
    bool ModelRequestable = false,
    string? Author = null,              // external-module audit fields (Phase 4); null for builtins
    string? Created = null,
    string? Modified = null)
{
    public static ModuleManifest Create(
        string id,
        string version,
        string description,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<string>? provides = null,
        bool defaultActive = true,
        bool modelRequestable = false,
        string? author = null,
        string? created = null,
        string? modified = null)
    {
        return new ModuleManifest(
            id,
            version,
            description,
            requires ?? new List<string>(),
            provides ?? new List<string>(),
            defaultActive,
            modelRequestable,
            author,
            created,
            modified);
    }
}