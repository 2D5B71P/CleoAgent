using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Modules;

// Loads modules into the shared ToolRegistry + ServiceRegistry in dependency
// order ("core" = the host kernel, always satisfied). Responsibilities
// (design s4):
//   - validate manifests: unknown requires, duplicate tool names -> the
//     module fails loudly with a named reason;
//   - never take the host down: LoadAllAsync collects per-module failures
//     instead of throwing (a broken module is refused by name, the rest
//     still load);
//   - track tool ownership + activity so later phases can deactivate and
//     re-register modules live.
internal sealed class ModuleManager
{
    private readonly ModuleContext _hostContext;   // registries + host identity, shared by all modules
    private readonly Dictionary<string, IModule> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();  // registration order (Keys are not iterable in this dialect)
    private readonly Dictionary<string, bool> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _toolCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolOwners = new(StringComparer.OrdinalIgnoreCase);

    // "core" is the kernel pseudo-module: always available, never listed.
    public static string KernelId => "core";

    public ModuleManager(ModuleContext hostContext)
    {
        _hostContext = hostContext;
    }

    public void Register(IModule module)
    {
        string id = module.Manifest().Id;
        if (!_registered.ContainsKey(id))
        {
            _order.Add(id);
        }
        _registered[id] = module;
    }

    public bool IsRegistered(string id) => _registered.ContainsKey(id);

    public bool IsActive(string id)
    {
        bool active;
        return _active.TryGetValue(id, out active) ? active : false;
    }

    public int ActiveCount()
    {
        int count = 0;
        foreach (string id in _order)
        {
            bool active;
            if (_active.TryGetValue(id, out active) && active)
            {
                count++;
            }
        }
        return count;
    }

    // Module status snapshot, in registration order.
    public IReadOnlyList<ModuleStatus> Status()
    {
        var result = new List<ModuleStatus>();
        foreach (string id in _order)
        {
            if (!_registered.TryGetValue(id, out var module))
            {
                continue;
            }

            int toolCount;
            result.Add(new ModuleStatus(
                module.Manifest(),
                IsActive(id),
                _toolCounts.TryGetValue(id, out toolCount) ? toolCount : 0));
        }
        return result;
    }

    // Loads every registered module (dependencies first). Returns per-module
    // failure messages; empty = all good. A failed module is left inactive and
    // its partial registrations are rolled back.
    public async Task<IReadOnlyList<string>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        var visited = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in _order)
        {
            await LoadModuleAsync(id, visited, failures, cancellationToken);
        }

        // One-time startup deprecation notes for modules using legacy config.
        foreach (string id in _order)
        {
            if (!_registered.TryGetValue(id, out var module))
            {
                continue;
            }

            ConfigSection? config = ConfigSection.ForModule(
                module.Manifest().Id,
                module.Manifest().ConfigSection);
            config?.WarnIfLegacy();
        }

        return failures;
    }

    private async Task LoadModuleAsync(
        string id,
        Dictionary<string, bool> visited,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        bool done;
        if (visited.TryGetValue(id, out done) && done)
        {
            return;
        }

        if (!_registered.TryGetValue(id, out var module))
        {
            failures.Add($"module \"{id}\": not registered.");
            return;
        }

        visited[id] = true;

        // 1. dependencies first.
        foreach (string required in module.Manifest().Requires)
        {
            if (required == KernelId)
            {
                continue;
            }

            if (!_registered.ContainsKey(required))
            {
                failures.Add($"module \"{id}\": requires unknown module \"{required}\".");
                return;   // leave unloaded; dependents will also report
            }

            await LoadModuleAsync(required, visited, failures, cancellationToken);
        }

        // 2. load, attributing newly registered tools to this module.
        //
        // Collision guard: the shared ToolRegistry silently OVERWRITES on
        // Register, so a name present both before and after is invisible to
        // diffing. Check twice: (a) pre-load, against the module's declared
        // Provides (a module that declares tools another loaded module owns is
        // refused BEFORE it registers anything - no partial state); (b) post-
        // load, any tool name now owned by a DIFFERENT module (undeclared
        // registrations)
        //   is a collision too.
        ModuleContext? ctx = null;
        List<string>? before = null;
        try
        {
            // (a) declared tool ownership.
            foreach (string provided in module.Manifest().Provides)
            {
                string? provider;
                if (_toolOwners.TryGetValue(provided, out provider) && provider != id)
                {
                    failures.Add(
                        $"module \"{id}\": tool \"{provided}\" already provided by " +
                        $"\"{provider}\" (refused before load).");
                    return;
                }
            }

            ctx = new ModuleContext(
                _hostContext.Tools,
                _hostContext.Services,
                _hostContext.AgentId,
                _hostContext.AgentsRoot,
                ConfigSection.ForModule(module.Manifest().Id, module.Manifest().ConfigSection));

            List<string> toolsBefore = _hostContext.Tools.Names.ToList();
            before = toolsBefore;
            await module.LoadAsync(ctx, cancellationToken);
            List<string> after = _hostContext.Tools.Names.ToList();

            int added = 0;
            foreach (string name in after)
            {
                // Names present before this load belong to the kernel or to
                // already-loaded dependencies - attribution for those happens
                // via the declared-Provides pre-check above. Only NEW names are
                // attributed here (and mis-appropriated new names refused).
                if (before.Contains(name))
                {
                    continue;
                }

                string? owner;
                if (_toolOwners.TryGetValue(name, out owner))
                {
                    failures.Add(
                        $"module \"{id}\": tool \"{name}\" already provided by " +
                        $"\"{owner}\".");
                    Rollback(before ?? new List<string>());
                    return;
                }

                _toolOwners[name] = id;
                added++;
            }

            _toolCounts[id] = added;
            _active[id] = true;
        }
        catch (Exception ex)
        {
            failures.Add($"module \"{id}\": failed to load: {ex.Message}");
            Rollback(before ?? new List<string>());
        }
    }

    // Unregisters everything registered after `before` was snapshotted
    // (ownership belongs to whichever module load failed / was rejected).
    private void Rollback(List<string>? before)
    {
        List<string> toolsBefore = before ?? new List<string>();

        foreach (string name in _hostContext.Tools.Names.ToList())
        {
            if (!toolsBefore.Contains(name))
            {
                _hostContext.Tools.Unregister(name);
            }
        }
    }
}

// Public status view of one module.
internal sealed record ModuleStatus(
    ModuleManifest Manifest,
    bool Active,
    int ToolCount)
{
    public string Id => Manifest.Id;
}