using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Context;

// Reads per-project guidance from a workspace instructions file. The file is
// discovered in the current working directory (or a configured path via
// Extras["workspace"]). Named after the common AGENTS.md/instruction convention.
internal sealed class WorkspaceInstructionsSource : IContextSource
{
    public string Name => "Workspace";

    public int Priority => 1;

    private const string DefaultFileName = "AGENTS.md";

    public Task<ContextBlock?> BuildAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        string? path = null;

        if (request.Extras is not null &&
            request.Extras.TryGetValue("workspace", out string? workspace) &&
            !string.IsNullOrWhiteSpace(workspace))
        {
            string candidate = Path.Combine(workspace, DefaultFileName);
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        if (path is null)
        {
            string cwd = System.Environment.CurrentDirectory;
            string candidate = Path.Combine(cwd, DefaultFileName);

            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        if (path is null)
        {
            return Task.FromResult<ContextBlock?>(null);
        }

        string content = File.ReadAllText(path).Trim();

        return Task.FromResult<ContextBlock?>(
            string.IsNullOrEmpty(content) ? null : new ContextBlock(Name, content));
    }
}
