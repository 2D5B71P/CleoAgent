using System;
using System.Collections.Generic;
using System.Text;
using CleoAgent.Core.Config;

namespace CleoAgent.Core.Modules;

// The operator's module gate (design s6): which modules may run / be requested
// by the model. Shape: "modules": { "allow": ["*"], "deny": [] } where "*"
// means "all shipped modules allowed". The allowlist is the WHOLE gate - there
// is no model-to-human approval loop; an operator tightens this to restrict
// what the model may enable, and a denied module can never be re-enabled by
// the model (resolution 2, 2026-09-13).
//
// Per-agent narrowing (resolution 3, 2026-09-13): agents/<id>/config.json may
// carry the same [modules] section; the merge is NARROWING-ONLY - per-agent
// deny adds to host deny, per-agent allow intersects host allow. A per-agent
// grant can never widen what the host allowed, so a host denial cannot be
// reopened by an agent file.
internal sealed record ModuleAllowlist(
    bool AllowAll,                    // allow == "*"
    IReadOnlyList<string> Allow,      // explicit allow list (empty when AllowAll)
    IReadOnlyList<string> Deny)
{
    public static ModuleAllowlist AllowEverything =>
        new ModuleAllowlist(true, new List<string>(), new List<string>());

    public bool IsDenied(string id) => Deny.Contains(id);

    // Allowed = not denied AND (allow is "*" OR id is listed). This gates both
    // boot activation and model requestability.
    public bool IsAllowed(string id) =>
        !Deny.Contains(id) && (AllowAll || Allow.Contains(id));

    // Host-wide allowlist loaded from the [modules] config section. Missing
    // section = allow everything, deny nothing (today's behavior).
    public static ModuleAllowlist LoadFromConfig(string agentId, string agentsRoot)
    {
        IReadOnlyList<string>? hostAllow = CleoAgent.Core.Config.Config.SectionStrings("modules", "allow");
        IReadOnlyList<string>? hostDeny = CleoAgent.Core.Config.Config.SectionStrings("modules", "deny");

        string agentConfigPath = System.IO.Path.Combine(
            System.IO.Path.Combine(agentsRoot, agentId), "config.json");

        IReadOnlyList<string>? agentAllow = CleoAgent.Core.Config.Config.SectionStringsAt(agentConfigPath, "modules", "allow");
        IReadOnlyList<string>? agentDeny = CleoAgent.Core.Config.Config.SectionStringsAt(agentConfigPath, "modules", "deny");

        return Narrow(hostAllow, hostDeny, agentAllow, agentDeny);
    }

    // Narrowing-only merge: agent allow intersects host allow, agent deny adds
    // to host deny. Absent lists are no-ops. Never widens.
    private static ModuleAllowlist Narrow(
        IReadOnlyList<string>? hostAllow,
        IReadOnlyList<string>? hostDeny,
        IReadOnlyList<string>? agentAllow,
        IReadOnlyList<string>? agentDeny)
    {
        IReadOnlyList<string> hostAllowList = hostAllow ?? new List<string>();
        IReadOnlyList<string> agentAllowList = agentAllow ?? new List<string>();

        // Absent allow list = NO RESTRICTION (equivalent to "*"). Only an
        // explicit list narrows; only two explicit "*" keep the wildcard.
        bool hostAll = hostAllow is null || hostAllowList.Contains("*");
        bool agentAll = agentAllow is null || agentAllowList.Contains("*");

        var effectiveAllow = new List<string>();
        if (hostAll && agentAll)
        {
            effectiveAllow.Add("*");
        }
        else if (hostAll)
        {
            AddAll(effectiveAllow, agentAllowList);
        }
        else if (agentAll)
        {
            AddAll(effectiveAllow, hostAllowList);
        }
        else
        {
            foreach (string id in hostAllowList)
            {
                if (agentAllowList.Contains(id))
                {
                    effectiveAllow.Add(id);
                }
            }
        }

        var effectiveDeny = new List<string>();
        AddAll(effectiveDeny, hostDeny ?? new List<string>());
        AddAll(effectiveDeny, agentDeny ?? new List<string>());

        bool allowAll = effectiveAllow.Contains("*");
        var allowIds = new List<string>();
        foreach (string id in effectiveAllow)
        {
            if (id != "*" && !allowIds.Contains(id))
            {
                allowIds.Add(id);
            }
        }

        return new ModuleAllowlist(allowAll, allowIds, effectiveDeny);
    }

    private static void AddAll(List<string> target, IReadOnlyList<string> source)
    {
        foreach (string id in source)
        {
            if (!target.Contains(id))
            {
                target.Add(id);
            }
        }
    }
}