namespace CleoAgent.Core.Context;

// A single chunk of rendered context, produced by an IContextSource x.
// Title is a human/LLM-facing label (e.g. "Workspace", "Memory"); Content is
// the free-text block injected into the prompt preamble.
internal sealed record ContextBlock(
    string Title,
    string Content)
{
    // Renders a labeled block, e.g. "## Memory\n...". Returns null if Content
    // is null/whitespace so sources can signal "nothing to contribute".
    public string? Render()
    {
        if (string.IsNullOrWhiteSpace(Content))
        {
            return null;
        }

        return $"## {Title}\n{Content.TrimEnd()}";
    }
}
