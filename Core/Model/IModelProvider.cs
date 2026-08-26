using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using CleoAgent.Core.Tools;

namespace CleoAgent.Core.Model
{
    internal interface IModelProvider
    {
        IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default);

        // Convenience: run a single non-streaming chat exchange and return the
        // model's full text reply. Used by the memory summarizer / reflection for
        // LLM Q/A extraction without going through the tool-calling loop. Renders
        // context exactly like StreamAsync (same provider path), concatenating all
        // text deltas. Returns null on an error event or empty output.
        async Task<string?> CompleteAsync(
            string prompt,
            string? agentId = null,
            CancellationToken cancellationToken = default)
        {
            var request = ModelRequest.ForPrompt(
                prompt,
                Array.Empty<ToolDefinition>(),
                agentId: agentId,
                ephemeral: true);

            var text = new StringBuilder();

            await foreach (ModelEvent evt in StreamAsync(request, cancellationToken))
            {
                switch (evt)
                {
                    case ModelTextDelta delta:
                        text.Append(delta.Text);
                        break;

                    case ModelError error:
                        throw new InvalidOperationException($"Model CompleteAsync failed: {error.Message}");
                }
            }

            return text.Length > 0 ? text.ToString() : null;
        }
    }
}
