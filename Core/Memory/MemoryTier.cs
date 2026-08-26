using System;

namespace CleoAgent.Core.Memory;

// The lifetime tier of a memory. Short-term memories are page-level
// question/answer extractions produced by the summarizer; long-term memories
// are the distilled, durable facts produced by the reflection pass.
internal enum MemoryTier
{
    ShortTerm,
    LongTerm
}
