using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CleoAgent.Core.Modules;

namespace CleoAgent.Core.Tools.Impl;

// A script-backed tool from an external module (design s7, trust tier 2):
// executes impl.command OUT-OF-PROCESS with the same Process primitives as
// run_command, and returns the script's stdout as the tool result.
//
// Contract:
//   - working directory = the module directory (relative script paths work);
//   - the tool-call arguments JSON is piped to the child's STDIN;
//   - exit code 0  -> stdout (trimmed) is the tool output;
//   - exit code !=0 -> the call is an error; stderr + exit code are attached.
// The agent's own code never runs inside the host process.
internal sealed class ScriptBackedTool : IAgentTool
{
    private readonly ExternalToolSpec _spec;
    private readonly string _moduleDir;

    public ScriptBackedTool(ExternalToolSpec spec, string moduleDir)
    {
        _spec = spec;
        _moduleDir = moduleDir;
    }

    public string Name => _spec.Name;
    public string Description => _spec.Description;
    public string ParametersJson => _spec.ParametersJson;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        var argv = _spec.Command;
        if (argv.Count == 0)
        {
            return new ToolResult(call.Id, "Script tool has no command (impl.command is empty).", true);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = argv[0],
            WorkingDirectory = _moduleDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        for (int i = 1; i < argv.Count; i++)
        {
            startInfo.ArgumentList.Add(argv[i]);
        }

        try
        {
            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                return new ToolResult(call.Id, $"Failed to start script tool command: {argv[0]}", true);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                // Tool arguments arrive on stdin as a JSON document (the same
                // ArgumentsJson the model produced). Newline-terminated so line-
                // oriented scripts can slurp one line.
                await process.StandardInput.WriteAsync(call.ArgumentsJson + "\n");
                process.StandardInput.Close();
            }
            catch (Exception ex)
            {
                TryKill(process);
                return new ToolResult(call.Id, "Failed to deliver tool arguments to the script: " + ex.Message, true);
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                var output = new StringBuilder();
                output.AppendLine($"Script exited with code {process.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    output.AppendLine();
                    output.AppendLine("stderr:");
                    output.AppendLine(stderr.TrimEnd());
                }
                return new ToolResult(call.Id, output.ToString().TrimEnd(), true);
            }

            return new ToolResult(call.Id, string.IsNullOrWhiteSpace(stdout) ? "(no output)" : stdout.TrimEnd());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolResult(call.Id, ex.Message, true);
        }
    }

    public string Describe(ToolCall call)
    {
        return $"Running external-module tool \"{Name}\"";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}