using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Context;

// System-prompt advertisement of dormant capability modules (design s5/s6):
// the model can request capabilities, so the preamble lists exactly the
// modules that are inactive but model-requestable (module_enable activates
// them, tools appear NEXT turn). Always-on modules like devtools never appear
// here (not requestable), and denied modules are not requestable either - the
// operator allowlist is the whole gate.
internal sealed class ModuleAdvertisementSource : IContextSource
{
    private readonly ModuleManager _manager;

    public string Name => "Modules";

    // After agent instructions (0) and workspace instructions (1): capability
    // advertisement is useful context but never before the persona.
    public int Priority => 2;

    public ModuleAdvertisementSource(ModuleManager manager)
    {
        _manager = manager;
    }

    public Task<ContextBlock?> BuildAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        var dormant = _manager.DormantRequestable();

        if (dormant.Count == 0)
        {
            return Task.FromResult<ContextBlock?>(null);
        }

        var sb = new StringBuilder();
        sb.Append("The following capability modules are dormant but may be " +
                  "activated on request (module_enable; tools appear next turn):");

        foreach (ModuleStatus status in dormant)
        {
            sb.AppendLine();
            sb.Append("- ").Append(status.Id).Append(": ").Append(status.Manifest.Description);
        }

        return Task.FromResult<ContextBlock?>(new ContextBlock(Name, sb.ToString()));
    }
}