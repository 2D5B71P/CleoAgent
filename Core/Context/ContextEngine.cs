using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Context;

// Orchestrates all IContextSource contributions into a single context preamble
// that is prepended to every turn's model input. Sources run in Priority order,
// and an optional character budget trims the render (dropping trailing/lowest
// priority blocks) if the preamble would exceed the cap.
internal sealed class ContextEngine
{
    private readonly List<IContextSource> _sources = new();

    public ContextEngine(IEnumerable<IContextSource>? sources = null)
    {
        if (sources is not null)
        {
            _sources.AddRange(sources);
        }
    }

    public void Register(IContextSource source) => _sources.Add(source);

    // Renders the full preamble (System instructions first, then ordered blocks).
    // If budget is non-null and positive, blocks beyond the cap are dropped,
    // lowest-priority last. Returns an empty string when nothing to contribute.
    public async Task<string> RenderAsync(
        ContextRequest request,
        int? budgetChars = null,
        CancellationToken cancellationToken = default)
    {
        var blocks = new List<ContextBlock>();

        foreach (IContextSource source in _sources.OrderBy(s => s.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ContextBlock? block = await source.BuildAsync(request, cancellationToken);

            if (block is not null && !string.IsNullOrWhiteSpace(block.Content))
            {
                blocks.Add(block);
            }
        }

        // Budget trimming: remove trailing (later-priority) blocks until the
        // combined rendered preamble fits.
        var render = new StringBuilder();

        foreach (ContextBlock block in blocks)
        {
            string? rendered = block.Render();
            if (rendered is null)
            {
                continue;
            }

            if (budgetChars is { } cap && cap > 0 && render.Length + rendered.Length + 1 > cap)
            {
                break;
            }

            if (render.Length > 0)
            {
                render.Append('\n');
            }

            render.Append(rendered);
        }

        return render.ToString();
    }
}
