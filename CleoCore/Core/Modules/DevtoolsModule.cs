using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

namespace CleoAgent.Core.Modules;

// The devtools core module: file-management + command-execution tools. This
// is the agent's "hands" - permanently on, NOT model-requestable (design:
// resolving open question 2, 2026-09-13). The model can never toggle it; an
// operator can still deny it per agent via the allowlist ([modules] deny).
internal sealed class DevtoolsModule : IModule
{
    public ModuleManifest Manifest() => ModuleManifest.Create(
        "devtools",
        "1.0.0",
        "File management + command execution: run_command, read/write_file, " +
        "list_directory, grep, get_environment_info (names only), move/rename/" +
        "remove file, make/remove directory, edit_file_inplace.",
        modelRequestable: false);

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        ctx.Tools.Register(new RunCommandTool());
        ctx.Tools.Register(new ReadFileTool());
        ctx.Tools.Register(new WriteFileTool());
        ctx.Tools.Register(new ListDirectoryTool());
        ctx.Tools.Register(new GrepTool());
        ctx.Tools.Register(new GetEnvironmentInfoTool());
        ctx.Tools.Register(new RemoveFileTool());
        ctx.Tools.Register(new MoveFileTool());
        ctx.Tools.Register(new RenameFileTool());
        ctx.Tools.Register(new MakeDirectoryTool());
        ctx.Tools.Register(new RemoveDirectoryTool());
        ctx.Tools.Register(new EditFileInplaceTool());
    }

    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        // devtools is a kernel module; deactivation is not supported (Phase 3).
    }
}