using System;

namespace CleoAgent.Core.Memory;

// The lifetime tier of a memory. VESTIGIAL since 2026-09-12: the old
// summarizer/reflection pipeline split memories into short- and long-term.
// The agent-driven memory tools store everything in one pool; this enum lives
// on for interface compatibility and persistence of historical documents.
internal enum MemoryTier
{
    ShortTerm,
    LongTerm
}
