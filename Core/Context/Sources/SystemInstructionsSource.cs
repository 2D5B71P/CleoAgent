using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Context;

// Reads the agent's own persona/behavior instructions from
// <agents>\<agentId>\agent.md. Falls back to a built-in default when the file
// is absent so there is always a coherent system prompt.
internal sealed class SystemInstructionsSource : IContextSource
{
    public string Name => "System";

    public int Priority => 0;

    private const string DefaultInstructions =
        "You are a capable software agent. You operate a set of tools to inspect, " +
        "modify, and reason about the user's projects. Be concise, accurate, and " +
        "prefer concrete action over speculation.";

    public Task<ContextBlock?> BuildAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        string path = AgentPaths.AgentInstructionsFile(request.AgentId);

        string content = File.Exists(path)
            ? File.ReadAllText(path).Trim()
            : DefaultInstructions;

        if (string.IsNullOrWhiteSpace(content))
        {
            content = DefaultInstructions;
        }

        return Task.FromResult<ContextBlock?>(new ContextBlock(Name, content));
    }
}
