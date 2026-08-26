using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Context;

// One unit of context assembly. Each source contributes an optional block.
// Priority controls ordering in the rendered preamble (lower = earlier).
internal interface IContextSource
{
    string Name { get; }

    int Priority { get; }

    Task<ContextBlock?> BuildAsync(ContextRequest request, CancellationToken cancellationToken = default);
}
