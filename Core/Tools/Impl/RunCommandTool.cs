using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CleoAgent.Core.Tools.Impl
{
    internal sealed class RunCommandTool : IAgentTool
    {
        public string Name => "run_command";
        public string Description =>
            "Run a single executable with arguments. IMPORTANT: pass the executable name " +
            "ALONE in \"command\" (e.g. \"cmd\", \"powershell\", \"curl\", \"python\") and each " +
            "argument separately in \"args\". Do NOT put the whole command line as one string " +
            "in \"command\" (e.g. do NOT pass \"cmd /c dir\" as a single value) — the executable " +
            "path is run directly and will not be parsed. To run a Windows shell builtin or a " +
            "multi-token line, use command=\"cmd\" with args=[\"/c\", \"<full line>\"].";
        public string ParametersJson =>
        """
        {
          "type": "object",
          "properties": {
            "command": {
              "type": "string",
              "description": "EXECUTABLE ONLY, no arguments: e.g. \"cmd\", \"powershell\", \"curl\", \"python\". Do not include the rest of the command line here."
            },
            "args": {
              "type": "array",
              "items": {
                "type": "string"
              },
              "description": "Arguments passed directly to the executable, one per array element. For a shell line like 'cmd /c dir /s /b <path>', pass 'cmd' as command and '/c dir /s /b <path>' as a single args element."
            },
            "workdir": {
              "type": "string",
              "description": "Working directory for the command."
            }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                var root = document.RootElement;
                if (!root.TryGetProperty("command", out var commandElement))
                {
                    return Error(call, "Missing required argument: command");
                }

                var command = commandElement.GetString();
                if (string.IsNullOrWhiteSpace(command))
                {
                    return Error(call, "Command cannot be empty.");
                }

                string workingDirectory = ".";
                if (root.TryGetProperty("workdir", out var workdirElement))
                {
                    var workdir = workdirElement.GetString();

                    if (!string.IsNullOrWhiteSpace(workdir))
                    {
                        workingDirectory = workdir;
                    }
                }

                if (!Directory.Exists(workingDirectory))
                {
                    return Error(call, $"Working directory does not exist: {workingDirectory}");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName            = command,
                    WorkingDirectory    = workingDirectory,
                    UseShellExecute     = false,
                    CreateNoWindow      = true,

                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                if (root.TryGetProperty("args", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var argumentElement in argumentsElement.EnumerateArray())
                    {
                        var argument = argumentElement.GetString();

                        if (argument is not null) {
                            startInfo.ArgumentList.Add(argument);
                        }
                    }
                }

                using var process = new Process
                {
                    StartInfo = startInfo
                };

                if (!process.Start())
                {
                    return Error(call, $"Failed to start command: {command}");
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

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

                var output = BuildOutput(process.ExitCode, stdout, stderr);

                return new ToolResult(call.Id, output, process.ExitCode != 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Error(call, ex.Message);
            }
        }

        private static ToolResult Error(ToolCall call, string message)
        {
            return new ToolResult(call.Id, message, true);
        }

        // Commands can be very long; cap what we echo back to the UI so the
        // activity line stays readable.
        private const int MaxDescribeLength = 100;

        public string Describe(ToolCall call)
        {
            try
            {
                using var document = JsonDocument.Parse(call.ArgumentsJson);

                var root = document.RootElement;

                string command = root.TryGetProperty("command", out var commandElement)
                    ? commandElement.GetString() ?? string.Empty
                    : string.Empty;

                if (command.Length == 0)
                {
                    return "Running a command";
                }

                var args = new StringBuilder();

                if (root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var argElement in argsElement.EnumerateArray())
                    {
                        args.Append(' ');
                        args.Append(argElement.GetString() ?? string.Empty);
                    }
                }

                string text = $"Running {command}{args}";

                if (text.Length > MaxDescribeLength)
                {
                    // Truncate safely at a UTF-16 char boundary; descriptions are
                    // display-only, so a precise codepoint boundary is unnecessary.
                    text = text.Substring(0, MaxDescribeLength) + "…";
                }

                return text;
            }
            catch
            {
                return "Running a command";
            }
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

        private static string BuildOutput(int exitCode, string stdout, string stderr)
        {
            var output = new StringBuilder();

            output.AppendLine($"Exit code: {exitCode}");

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                output.AppendLine();
                output.AppendLine("stdout:");
                output.AppendLine(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                output.AppendLine();
                output.AppendLine("stderr:");
                output.AppendLine(stderr.TrimEnd());
            }

            return output.ToString();
        }
    }
}
