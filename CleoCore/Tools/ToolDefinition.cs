using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Tools
{
    internal sealed record ToolDefinition(
        string Name,
        string Description,
        string ParametersJson);
}
