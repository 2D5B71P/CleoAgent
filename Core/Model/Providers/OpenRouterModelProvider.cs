using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Config;
using CleoAgent.Core.Context;
using CleoAgent.Core.Session;
using CleoAgent.Core.Tools;

namespace CleoAgent.Core.Model.Providers;

internal class OpenRouterModelProvider : OpenAIModelProvider
{
    public override string ResponsesEndpoint => "https://openrouter.ai/api/v1/responses";

    private readonly SessionStore _store;
    private readonly CompactionConfig _compaction;
    private readonly Dictionary<string, SessionRecord> _cache = new();

    public OpenRouterModelProvider(
        HttpClient httpClient,
        string apiKey,
        string model,
        ContextEngine? context,
        string? agentId = null,
        CompactionConfig? compaction = null)
        : base(httpClient, apiKey, model, context)
    {
        _store = new SessionStore(agentId ?? "default");
        _compaction = compaction ?? CompactionConfig.Default;
    }

    // OpenRouter's Responses endpoint is STATELESS: it rejects previous_response_id
    // (and store:true) with a 400, so the conversation cannot be reconstructed
    // server-side. We keep history CLIENT-SIDE and replay it as the complete
    // `input` array every turn. Sessions are persisted to disk (append-only JSONL)
    // so they survive restarts with stable ids, and bounded by compaction so the
    // per-turn payload (and resident RAM) stays flat no matter how long the
    // session runs.
    public override async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Ephemeral calls (memory summarizer/reflection, compaction summarizer)
        // are one-shot and must not touch the session store.
        if (request.Ephemeral)
        {
            await foreach (ModelEvent evt in StreamEphemeralAsync(request, cancellationToken))
            {
                yield return evt;
            }
            yield break;
        }

        SessionRecord session = GetOrCreateSession(request.ContinuationToken);

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            session.SystemMessage = await RenderContextAsync(request, cancellationToken);
        }

        var input = new List<object>();
        BuildContext(session, input);

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            var user = new OpenAIUserMessage { Role = "user", Content = request.Prompt };
            _store.Append(session, new SessionMessage(SessionMessageType.User, Content: request.Prompt));
            input.Add(user);
        }

        if (request.ToolResult is not null)
        {
            var output = new OpenAIFunctionCallOutput
            {
                Type    = "function_call_output",
                CallId  = request.ToolResult.ToolCallId,
                Output  = request.ToolResult.Output
            };
            _store.Append(session, new SessionMessage(
                SessionMessageType.FunctionOutput,
                CallId: request.ToolResult.ToolCallId,
                Output: request.ToolResult.Output));
            input.Add(output);
        }

        var turnText = new StringBuilder();

        await foreach (ModelEvent evt in StreamResponseAsync(input, null, request.Tools, cancellationToken))
        {
            switch (evt)
            {
                case ModelTextDelta text:
                    turnText.Append(text.Text);
                    yield return text;
                    break;

                case ModelToolCall toolCall:
                    FlushTurnText(turnText, session);

                    _store.Append(session, new SessionMessage(
                        SessionMessageType.FunctionCall,
                        CallId: toolCall.Call.Id,
                        Name: toolCall.Call.Name,
                        Arguments: toolCall.Call.ArgumentsJson));

                    yield return toolCall;
                    break;

                case ModelCompleted:
                    FlushTurnText(turnText, session);
                    await CompactIfNeededAsync(session, cancellationToken);
                    yield return new ModelCompleted(session.Id);
                    break;

                case ModelError error:
                    FlushTurnText(turnText, session);
                    yield return error;
                    yield break;

                default:
                    break;
            }
        }
    }

    // One-shot call used by internal sub-systems (memory, compaction). No session
    // creation, no persistence, no history accumulation.
    private async IAsyncEnumerable<ModelEvent> StreamEphemeralAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = new List<object>();

        string? context = await RenderContextAsync(request, cancellationToken);

        if (context is not null)
        {
            input.Add(new OpenAIUserMessage { Role = "system", Content = context });
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            input.Add(new OpenAIUserMessage { Role = "user", Content = request.Prompt });
        }

        await foreach (ModelEvent evt in StreamResponseAsync(input, null, request.Tools, cancellationToken))
        {
            yield return evt;
        }
    }

    private SessionRecord GetOrCreateSession(string? continuationToken)
    {
        // continuationToken maps to a session id for resume; a fresh id for a
        // brand-new conversation.
        string sessionId = continuationToken ?? Guid.NewGuid().ToString("N");

        // Live sessions are cached in-memory (bounded by compaction, and loaded
        // from disk lazily on first touch after a restart) so the rendered context
        // and tool-call chains survive across turns within this process.
        if (_cache.TryGetValue(sessionId, out SessionRecord? existing))
        {
            return existing;
        }

        SessionRecord record = _store.Load(sessionId);
        _cache[sessionId] = record;
        return record;
    }

    // Prepends the session's cached context preamble (system/workspace/memory)
    // to the input payload, followed by the accumulated conversation history.
    private static void BuildContext(SessionRecord session, List<object> input)
    {
        if (session.SystemMessage is not null)
        {
            input.Add(new OpenAIUserMessage { Role = "system", Content = session.SystemMessage });
        }

        foreach (SessionMessage message in session.History)
        {
            switch (message.Type)
            {
                case SessionMessageType.System:
                case SessionMessageType.User:
                case SessionMessageType.Assistant:
                    input.Add(new OpenAIUserMessage { Role = RoleFor(message.Type), Content = message.Content ?? "" });
                    break;

                case SessionMessageType.FunctionCall:
                    input.Add(new OpenAIFunctionCallItem
                    {
                        Type      = "function_call",
                        CallId    = message.CallId ?? "",
                        Name      = message.Name ?? "",
                        Arguments = message.Arguments ?? "{}"
                    });
                    break;

                case SessionMessageType.FunctionOutput:
                    input.Add(new OpenAIFunctionCallOutput
                    {
                        Type    = "function_call_output",
                        CallId  = message.CallId ?? "",
                        Output  = message.Output ?? ""
                    });
                    break;
            }
        }
    }

    private static string RoleFor(SessionMessageType type) => type switch
    {
        SessionMessageType.System    => "system",
        SessionMessageType.Assistant => "assistant",
        _                            => "user"
    };

    // Appends the current turn's assistant text as a message item so later turns
    // retain prior answers. Also persisted to disk.
    private void FlushTurnText(StringBuilder turnText, SessionRecord session)
    {
        if (turnText.Length is 0)
        {
            return;
        }

        _store.Append(session, new SessionMessage(SessionMessageType.Assistant, Content: turnText.ToString()));

        turnText.Clear();
    }

    // Compaction: when the session grows past the configured ceiling, fold the
    // oldest turns into a compact LLM summary and keep only the most recent
    // (KeepRecentTurns) turns verbatim. Bounds both the API payload and RAM.
    private async Task CompactIfNeededAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        long bytes = _store.HistoryBytes(session);

        // Upper guardrail: never let the serialized history exceed the hard byte
        // ceiling, even if the token estimate hasn't tripped yet.
        bool overHard = bytes > _compaction.HardByteCeiling;

        if (!overHard && EstimateTokens(bytes) < _compaction.SoftTokenCeiling)
        {
            return;
        }

        // A "turn" starts at each User message (one is appended per user prompt;
        // tool results and assistant replies follow it). Split history into turns.
        var userIdx = new List<int>();
        for (int k = 0; k < session.History.Count; k++)
        {
            if (session.History[k].Type == SessionMessageType.User)
            {
                userIdx.Add(k);
            }
        }

        // Mirror of the size guard above: nothing to compact below the ceiling.
        if (userIdx.Count <= _compaction.KeepRecentTurns && !overHard)
        {
            return;
        }

        int keepBlocks = _compaction.KeepRecentTurns;

        // Hard guardrail fallback: if we're already over the byte ceiling, shrink
        // the kept window (halve, floor 1) so the rebuilt history actually lands
        // under it even when the conversation has few turns.
        if (overHard && keepBlocks > 1)
        {
            keepBlocks = Math.Max(1, keepBlocks / 2);
        }

        // Keep the trailing `keepBlocks` user turns verbatim; everything before
        // the first kept turn (including any prior compaction summary) is folded
        // into a fresh summary block.
        int split = userIdx.Count > keepBlocks
            ? userIdx[userIdx.Count - keepBlocks]
            : (overHard ? userIdx[0] : 0);

        if (split <= 0)
        {
            // Nothing older than the first user turn (or empty older segment):
            // a re-summarization would only add payload.
            return;
        }

        var older  = session.History.GetRange(0, split);
        var recent = session.History.GetRange(split, session.History.Count - split);

        string? summary = await SummarizeOlderAsync(older, cancellationToken);

        if (string.IsNullOrWhiteSpace(summary))
        {
            // Summarization failed (transient). Leave history intact rather than
            // dropping turns.
            return;
        }

        // Rebuild: summary block + recent turns.
        var rebuilt = new List<SessionMessage>
        {
            new(SessionMessageType.System, Content: summary)
        };
        rebuilt.AddRange(recent);

        _store.Rewrite(session, rebuilt);

        Console.Error.WriteLine($"[session] compacted {session.Id}: {older.Count} older msg(s) -> summary ({summary.Length} chars)");
    }

    private static int EstimateTokens(long bytes) => (int)(bytes / 4);

    // Summarizes the older portion of a session into a single compact context
    // block using the LLM (paths mirrors the memory summarizer, but ephemeral so
    // it does not create/persist its own session).
    private Task<string?> SummarizeOlderAsync(IReadOnlyList<SessionMessage> older, CancellationToken cancellationToken)
    {
        var transcript = new StringBuilder();

        foreach (SessionMessage message in older)
        {
            switch (message.Type)
            {
                case SessionMessageType.User:
                    transcript.Append("USER: ").AppendLine(message.Content);
                    break;
                case SessionMessageType.Assistant:
                    transcript.Append("ASSISTANT: ").AppendLine(message.Content);
                    break;
                case SessionMessageType.System:
                    transcript.Append("[previous summary] ").AppendLine(message.Content);
                    break;
                case SessionMessageType.FunctionCall:
                    transcript.Append("[tool: ").Append(message.Name).Append("] ").AppendLine(message.Arguments);
                    break;
                case SessionMessageType.FunctionOutput:
                    transcript.Append("[tool result: ").AppendLine(message.Output);
                    break;
            }
        }

        string prompt =
            "You are compressing an earlier part of an agent conversation into a single " +
            "concise summary block. Preserve key facts, decisions, user preferences, " +
            "prior tool outcomes, and ongoing tasks. Output ONLY the summary text, no " +
            "preamble.\n\n--- older conversation ---\n" +
            transcript +
            "\n--- end ---";

        var request = ModelRequest.ForPrompt(prompt, Array.Empty<ToolDefinition>(), agentId: null, ephemeral: true);

        return CompleteAsync(request, cancellationToken);
    }

    // Completes an ephemeral request, returning the model's text reply.
    private async Task<string?> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();

        await foreach (ModelEvent evt in StreamEphemeralAsync(request, cancellationToken))
        {
            switch (evt)
            {
                case ModelTextDelta delta:
                    text.Append(delta.Text);
                    break;
                case ModelError error:
                    return null;
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    public void Reset()
    {
        // Sessions are persisted; nothing to clear for a warm restart. (Kept for
        // symmetry with the loop.)
    }
}