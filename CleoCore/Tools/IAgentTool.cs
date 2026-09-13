using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Tools
{
    internal interface IAgentTool
    {
        string Name { get; }
        string Description { get; }
        string ParametersJson { get; }

        Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default);

        // Human-readable summary of what THIS call will do, e.g. "Reading src/main.cs".
        // Consumed by the UI layer (CLI, dashboard...) so activity reads naturally.
        string Describe(ToolCall call);
    }
}
