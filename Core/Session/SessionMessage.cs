using System;
using System.Text.Json.Serialization;

namespace CleoAgent.Core.Session;

// A single conversation entry in a session, persisted as one JSONL line.
// Mirrors the OpenAI Responses message shapes the provider replays, but as a
// neutral DTO so the store stays provider-agnostic.
public enum SessionMessageType
{
    User,          // role "user"   (also used as the LLM-summarized "so far" block via System)
    Assistant,     // role "assistant"
    System,        // role "system" (compacted summary block)
    FunctionCall,  // assistant's function_call item
    FunctionOutput // function_call_output item pairing with FunctionCall
}

public sealed record SessionMessage(
    SessionMessageType Type,
    string? Content = null,     // role message text (user/assistant/system)
    string? CallId = null,      // function_call / function_call_output
    string? Name = null,        // function_call
    string? Arguments = null,   // function_call
    string? Output = null)      // function_call_output
{
    // Computed convenience for size accounting. NOT persisted (System.Text.Json
    // would otherwise serialize the get-only property and recurse into Serialize).
    [JsonIgnore]
    public long SerializedLength => SessionStore.Serialize(this).Length;
}