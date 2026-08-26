using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Tools
{
    internal record class ToolResult(
        string ToolCallId, 
        string Output, 
        bool IsError = false);
}
