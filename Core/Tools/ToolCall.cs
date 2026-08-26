using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Tools
{
    internal record class ToolCall(
        string Id,
        string Name,
        string ArgumentsJson);
}
