using CleoAgent.Core.Tools;
using System;
using System.Collections.Generic;
using System.Text;

namespace CleoAgent.Core.Model
{
    internal abstract record ModelEvent;

    internal sealed record ModelTextDelta(
        string Text) : ModelEvent;

    internal sealed record ModelToolCall(
        ToolCall Call) : ModelEvent;

    internal sealed record ModelCompleted(
        string? ContinuationToken = null) : ModelEvent;

    internal sealed record ModelError(
        string Message) : ModelEvent;
}
