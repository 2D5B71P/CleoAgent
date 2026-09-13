using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Modules;

// Loads modules into the shared ToolRegistry + ServiceRegistry in dependency
// order ("core" = the host kernel, always satisfied). Responsibilities
// (design s4, s6, Phase 3):
//   - validate manifests: unknown requires, duplicate tool names -> the
//     module fails loudly with a named reason;
//   - enforce the OPERATOR ALLOWLIST (design s6): a denied module is neither
//     loaded at boot nor model-requestable - it can never be re-enabled by the
//     model (resolution 2, 2026-09-13). The allowlist is the whole gate: no
//     model-to-human approval loop exists;
//   - never take the host down: LoadAllAsync collects per-module failures
//     instead of throwing (a broken module is refused by name, the rest
//     still load);
//   - LOGICAL activation (Phase 3): module_enable/module_disable queue a
//     desired state; ApplyPendingAsync applies it at the next turn boundary
//     (no mid-turn registry races). Activation = registration: deactivating a
//     module unregisters its tools + services from the live registries;
//   - track tool + service ownership so deactivation and rollback are exact.
internal sealed class ModuleManager
{
    private readonly ModuleContext _hostContext;   // registries + host identity, shared by all modules
    private readonly Dictionary<string, IModule> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();  // registration order (Keys are not iterable in this dialect)
    private readonly Dictionary<string, bool> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);   // desired active state, applied next turn
    private readonly Dictionary<string, int> _toolCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _toolOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _serviceOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _moduleTools = new(StringComparer.OrdinalIgnoreCase);     // id -> tools attributed to it
    private readonly Dictionary<string, List<string>> _moduleServices = new(StringComparer.OrdinalIgnoreCase);   // id -> services attributed to it
    private readonly Dictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _modelDisabled = new(StringComparer.OrdinalIgnoreCase); // last state change was a model disable
    private readonly ModuleAllowlist _allowlist;

    // "core" is the kernel pseudo-module: always available, never listed.
    public static string KernelId => "core";

    public ModuleManager(ModuleContext hostContext, ModuleAllowlist? allowlist = null)
    {
        _hostContext = hostContext;
        // null = the documented default: allow everything, deny nothing
        // (matches Phase 2 behavior when no [modules] section exists). The
        // COMPOSITION ROOT (Program.cs / CompositionSelfTest) loads the real
        // allowlist from config and passes it in; unit tests stay hermetic.
        _allowlist = allowlist ?? ModuleAllowlist.AllowEverything;
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

    // Whether the model may request this module (enable when dormant, disable
    // when active). Gates on BOTH the manifest flag (devtools is permanently
    // on) and the operator allowlist (denied modules stay unrequestable).
    public bool IsRequestable(string id)
    {
        if (!_registered.TryGetValue(id, out var module))
        {
            return false;
        }
        return module.Manifest().ModelRequestable && _allowlist.IsAllowed(id);
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

    // Module status snapshot, in registration order. Reason explains WHY each
    // module is in its state (config default / model request / deny / failure)
    // for module_status.
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
                _toolCounts.TryGetValue(id, out toolCount) ? toolCount : 0,
                IsRequestable(id),
                Reason(id, module.Manifest())));
        }
        return result;
    }

    // Dormant-but-requestable modules for the system-prompt advertisement:
    // inactive NOW, model may activate them (module_enable). Always-on modules
    // (devtools) never appear here; denied modules are not requestable either.
    public IReadOnlyList<ModuleStatus> DormantRequestable()
    {
        var result = new List<ModuleStatus>();
        foreach (ModuleStatus status in Status())
        {
            if (!status.Active && status.Requestable)
            {
                result.Add(status);
            }
        }
        return result;
    }

    // Loads every registered module (dependencies first). Returns per-module
    // failure messages; empty = all good. A failed module is left inactive and
    // its partial registrations are rolled back. Boot gating (Phase 3):
    // modules that are not default-active OR are denied by the operator
    // allowlist are NOT loaded - they stay registered-but-inactive, ready for
    // module_enable (dormant) or permanently off (denied).
    public async Task<IReadOnlyList<string>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        var visited = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (string id in _order)
        {
            if (!_registered.TryGetValue(id, out var module))
            {
                continue;
            }

            if (!module.Manifest().DefaultActive || !_allowlist.IsAllowed(id))
            {
                visited[id] = true;   // explicitly skipped, not a failure
                continue;
            }

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

    // ---- logical activation (Phase 3) -----------------------------------

    // Queues an activation (next turn). Returns null on success, or a clear
    // refusal: unknown module, already active, not model-requestable, denied by
    // the operator allowlist, or a required module that is missing/denied.
    public string? RequestEnable(string id)
    {
        if (!_registered.TryGetValue(id, out var module))
        {
            return string.Format("module \"{0}\" is not registered.", id);
        }

        if (IsActive(id))
        {
            return string.Format("module \"{0}\" is already active.", id);
        }

        var manifest = module.Manifest();
        if (!manifest.ModelRequestable)
        {
            return string.Format(
                "module \"{0}\" is not model-requestable and cannot be enabled " +
                "by the model (permanently on / operator-controlled).", id);
        }

        if (!_allowlist.IsAllowed(id))
        {
            return string.Format(
                "module \"{0}\" is denied by the operator allowlist and cannot " +
                "be enabled.", id);
        }

        // Requirements must be satisfiable: each required module must exist,
        // be allowed, and be queued for activation too (dependencies first).
        foreach (string required in manifest.Requires)
        {
            if (required == KernelId)
            {
                continue;
            }

            if (!_registered.ContainsKey(required))
            {
                return string.Format(
                    "module \"{0}\" requires \"{1}\", which is not registered.", id, required);
            }

            if (IsActive(required))
            {
                continue;
            }

            string? depRefusal = RequestEnable(required);
            if (depRefusal is not null)
            {
                return string.Format(
                    "module \"{0}\" requires \"{1}\": {2}", id, required, depRefusal);
            }
        }

        _pending[id] = true;
        return null;
    }

    // Queues a deactivation (next turn). Returns null on success, or a clear
    // refusal: unknown module, not active, not model-requestable (always-on),
    // or still required by an active module.
    public string? RequestDisable(string id)
    {
        if (!_registered.TryGetValue(id, out var module))
        {
            return string.Format("module \"{0}\" is not registered.", id);
        }

        if (!IsActive(id))
        {
            return string.Format("module \"{0}\" is not active.", id);
        }

        var manifest = module.Manifest();
        if (!manifest.ModelRequestable)
        {
            return string.Format(
                "module \"{0}\" is not model-requestable and cannot be " +
                "deactivated (permanently on).", id);
        }

        // Refuse when an active module depends on this one: deactivating it
        // would strand a live dependent's tools/services.
        foreach (string other in _order)
        {
            if (other == id || !IsActive(other))
            {
                continue;
            }

            if (!_registered.TryGetValue(other, out var otherModule))
            {
                continue;
            }

            if (otherModule.Manifest().Requires.Contains(id))
            {
                return string.Format(
                    "module \"{0}\" is required by active module \"{1}\"; " +
                    "deactivate that first.", id, other);
            }
        }

        _pending[id] = false;
        return null;
    }

    // Applies queued enable/disable requests at the next turn boundary.
    // Returns failures from enable loads (a module that queued fine but fails
    // to load reports here by name; the host is never taken down). Callers
    // invoke this once per turn (Program.cs does it before each prompt).
    public async Task<IReadOnlyList<string>> ApplyPendingAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        // Disables first (they cannot strand dependents - RequestDisable
        // refuses those), then enables in registration order (dependencies
        // first via LoadModuleAsync).
        foreach (string id in _order)
        {
            bool want;
            if (_pending.TryGetValue(id, out want) && !want && IsActive(id))
            {
                DeactivateModule(id);
            }
        }

        var visited = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in _order)
        {
            bool want;
            if (_pending.TryGetValue(id, out want) && want && !IsActive(id))
            {
                await LoadModuleAsync(id, visited, failures, cancellationToken);
            }
        }

        foreach (string id in _order)
        {
            _pending.Remove(id);
        }

        return failures;
    }

    // Deactivates a module: unregisters its attributed tools + services from
    // the live registries (logical activation = registration). Ownership maps
    // keep this exact - nothing a kernel or another module owns is touched.
    private void DeactivateModule(string id)
    {
        List<string>? tools;
        if (_moduleTools.TryGetValue(id, out tools))
        {
            foreach (string name in tools)
            {
                _hostContext.Tools.Unregister(name);
                _toolOwners.Remove(name);
            }
            _moduleTools.Remove(id);
        }

        List<string>? services;
        if (_moduleServices.TryGetValue(id, out services))
        {
            foreach (string name in services)
            {
                _hostContext.Services.Unregister(name);
                _serviceOwners.Remove(name);
            }
            _moduleServices.Remove(id);
        }

        _toolCounts[id] = 0;
        _active[id] = false;
        _modelDisabled[id] = true;
    }

    // Human-readable explanation of a module's state, for module_status. The
    // state word (active/inactive) is rendered by the calling tool, so reasons
    // here carry only the explanation ("config default", "denied by ...").
    private string Reason(string id, ModuleManifest manifest)
    {
        if (!_allowlist.IsAllowed(id))
        {
            return "denied by the operator allowlist (cannot be enabled)";
        }

        bool active = IsActive(id);
        bool want;
        bool queued = _pending.TryGetValue(id, out want);

        if (queued && !want && active)
        {
            return "deactivation queued (takes effect next turn)";
        }
        if (queued && want && !active)
        {
            return "activation queued (takes effect next turn)";
        }

        if (active)
        {
            if (!manifest.ModelRequestable)
            {
                return "always-on builtin (not model-requestable)";
            }
            bool wasModelDisabled;
            if (_modelDisabled.TryGetValue(id, out wasModelDisabled) && wasModelDisabled)
            {
                return "model-requested";
            }
            return "config default (allowlist allows)";
        }

        if (!manifest.DefaultActive)
        {
            return "dormant - default off; the model may request it (module_enable)";
        }

        string? error;
        if (_loadErrors.TryGetValue(id, out error))
        {
            return "failed to load: " + error;
        }

        bool modelDisabled;
        if (_modelDisabled.TryGetValue(id, out modelDisabled) && modelDisabled)
        {
            return "disabled by the model (requestable again)";
        }

        return "not loaded";
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
                _loadErrors[id] = "requires unknown module \"" + required + "\"";
                failures.Add($"module \"{id}\": requires unknown module \"{required}\".");
                return;   // leave unloaded; dependents will also report
            }

            if (!_allowlist.IsAllowed(required))
            {
                _loadErrors[id] = "requires \"" + required + "\", which is denied by the allowlist";
                failures.Add(
                    $"module \"{id}\": requires \"{required}\", which is denied " +
                    "by the operator allowlist.");
                return;
            }

            await LoadModuleAsync(required, visited, failures, cancellationToken);
        }

        // 2. load, attributing newly registered tools + services to this module.
        //
        // Collision guard: the shared registries silently OVERWRITE on
        // Register, so a name present both before and after is invisible to
        // diffing. Check twice: (a) pre-load, against the module's declared
        // Provides; (b) post-load, any name now owned by a DIFFERENT module
        // (undeclared registrations) is a collision too.
        ModuleContext? ctx = null;
        List<string>? before = null;
        List<string> servicesBefore = new List<string>();
        try
        {
            // (a) declared tool ownership.
            foreach (string provided in module.Manifest().Provides)
            {
                string? provider;
                if (_toolOwners.TryGetValue(provided, out provider) && provider != id)
                {
                    _loadErrors[id] = "tool \"" + provided + "\" already provided by \"" + provider + "\"";
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
            servicesBefore = _hostContext.Services.Names.ToList();
            before = toolsBefore;
            await module.LoadAsync(ctx, cancellationToken);

            var ownedTools = new List<string>();
            foreach (string name in _hostContext.Tools.Names.ToList())
            {
                // Names present before this load belong to the kernel or to
                // already-loaded dependencies - attribution for those happens
                // via the declared-Provides pre-check above. Only NEW names are
                // attributed here (and mis-appropriated new names refused).
                if (toolsBefore.Contains(name))
                {
                    continue;
                }

                string? owner;
                if (_toolOwners.TryGetValue(name, out owner))
                {
                    failures.Add(
                        $"module \"{id}\": tool \"{name}\" already provided by " +
                        $"\"{owner}\".");
                    Rollback(before ?? new List<string>(), servicesBefore);
                    _loadErrors[id] = "tool \"" + name + "\" already provided";
                    return;
                }

                _toolOwners[name] = id;
                ownedTools.Add(name);
            }

            var ownedServices = new List<string>();
            foreach (string name in _hostContext.Services.Names.ToList())
            {
                if (servicesBefore.Contains(name))
                {
                    continue;
                }

                string? owner;
                if (_serviceOwners.TryGetValue(name, out owner))
                {
                    failures.Add(
                        $"module \"{id}\": service \"{name}\" already provided by " +
                        $"\"{owner}\".");
                    Rollback(before ?? new List<string>(), servicesBefore);
                    _loadErrors[id] = "service \"" + name + "\" already provided";
                    return;
                }

                _serviceOwners[name] = id;
                ownedServices.Add(name);
            }

            _moduleTools[id] = ownedTools;
            _moduleServices[id] = ownedServices;
            _toolCounts[id] = ownedTools.Count;
            _active[id] = true;
            _modelDisabled.Remove(id);
            _loadErrors.Remove(id);
        }
        catch (Exception ex)
        {
            _loadErrors[id] = ex.Message;
            failures.Add($"module \"{id}\": failed to load: {ex.Message}");
            Rollback(before ?? new List<string>(), servicesBefore);
        }
    }

    // Unregisters everything registered after `before` was snapshotted
    // (ownership belongs to whichever module load failed / was rejected).
    // `servicesBefore` is the pre-load service-name snapshot so both registries
    // revert to exactly their prior state.
    private void Rollback(List<string>? before, List<string> servicesBefore)
    {
        List<string> toolsBefore = before ?? new List<string>();

        // Tools: revert the exact set snapshot (removes only what this load
        // added; ownership-tracking removals mirror DeactivateModule).
        foreach (string name in _hostContext.Tools.Names.ToList())
        {
            if (!toolsBefore.Contains(name))
            {
                _hostContext.Tools.Unregister(name);
            }
        }

        // Services: same diffing against the pre-load snapshot.
        foreach (string name in _hostContext.Services.Names.ToList())
        {
            if (!servicesBefore.Contains(name))
            {
                _hostContext.Services.Unregister(name);
            }
        }
    }
}

// Public status view of one module.
internal sealed record ModuleStatus(
    ModuleManifest Manifest,
    bool Active,
    int ToolCount,
    bool Requestable,
    string Reason)
{
    public string Id => Manifest.Id;
}