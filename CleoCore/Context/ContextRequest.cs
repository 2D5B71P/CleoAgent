using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Context;

// Carry-along data describing the current request, so a source can decide
// whether to render and what to pull. Minimal now; grows as needs do
// (e.g. current message text for memory relevance).
internal sealed record ContextRequest(
    string AgentId,
    string? UserPrompt,
    System.Collections.Generic.IReadOnlyDictionary<string, string>? Extras = null);
