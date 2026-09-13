using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Modules;

// Name-keyed locator for module-provided services - deliberately NOT a DI
// framework. Modules publish what they provide ("memory", "embedding", ...)
// and future modules consume by name only. String keys keep it simple and
// JSON-able for agent-authored modules.
internal sealed class ServiceRegistry
{
    private readonly Dictionary<string, object> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();   // registration order (Keys are not iterable in this dialect)

    public void Register(string name, object service)
    {
        if (!_services.ContainsKey(name))
        {
            _order.Add(name);
        }
        _services[name] = service;
    }

    public object? Get(string name)
    {
        object? service;
        return _services.TryGetValue(name, out service) ? service : null;
    }

    public bool Contains(string name) => _services.ContainsKey(name);

    public IReadOnlyList<string> Names => _order.ToList();
}