using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CleoAgent.Core.Context;

namespace CleoAgent.Core.Model.Providers;

internal class OpenAIModelProvider : IModelProvider
{
    public virtual string ResponsesEndpoint => "https://api.openai.com/v1/responses";

    protected readonly HttpClient _httpClient;
    protected readonly string _apiKey;
    protected readonly string _model;
    protected readonly ContextEngine? _context;

    public OpenAIModelProvider(HttpClient httpClient, string apiKey, string model, ContextEngine? context = null)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _model = model;
        _context = context;
    }

    // Renders the context preamble (system/workspace/memory...) for a fresh user
    // turn. Returns null for tool-result continuations — those reuse the context
    // already established (OpenAI via previous_response_id, OpenRouter via its
    // stored session context). MemorySource keys off the user prompt, so we only
    // re-render when there is a prompt.
    protected virtual async Task<string?> RenderContextAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_context is null || string.IsNullOrWhiteSpace(request.Prompt))
        {
            return null;
        }

        var ctxRequest = new ContextRequest(
            AgentId: request.ResolvedAgentId,
            UserPrompt: request.Prompt);

        string preamble = await _context.RenderAsync(ctxRequest, cancellationToken: cancellationToken);

        return string.IsNullOrWhiteSpace(preamble) ? null : preamble;
    }

    public virtual async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // OpenAI's Responses API is STATEFUL: we only send the new items for this
        // turn and resume the full conversation server-side via previous_response_id.
        var input = new List<object>();

        string? context = await RenderContextAsync(request, cancellationToken);

        if (context is not null)
        {
            input.Add(new OpenAIUserMessage
            {
                Role    = "system",
                Content = context
            });
        }

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            input.Add(new OpenAIUserMessage
            {
                Role    = "user",
                Content = request.Prompt
            });
        }

        if (request.ToolResult is not null)
        {
            input.Add(new OpenAIFunctionCallOutput
            {
                Type    = "function_call_output",
                CallId  = request.ToolResult.ToolCallId,
                Output  = request.ToolResult.Output
            });
        }

        await foreach (ModelEvent evt in StreamResponseAsync(input, request.ContinuationToken, request.Tools, cancellationToken))
        {
            yield return evt;
        }
    }

    // Sends one OpenAI-compatible Responses request over the OpenAI transport and
    // yields the parsed ModelEvents. Subclasses may override the memory strategy:
    // OpenAI passes a previous_response_id here (stateful), while OpenRouter's
    // stateless endpoint is handled by a subclass that sends full history instead
    // and passes both previousResponseId and tools through to this shared plumbing.
    protected async IAsyncEnumerable<ModelEvent> StreamResponseAsync(
        IReadOnlyList<object> input,
        string? previousResponseId,
        IReadOnlyList<Tools.ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var body = new OpenAIResponseRequest
        {
            Model              = _model,
            Input              = input,
            PreviousResponseId = previousResponseId,
            Stream             = true,
            Store              = false,
            Tools              = tools is { Count: > 0 } ? tools.Select(CreateTool).ToArray() : null,
            ParallelToolCalls  = false
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint);

        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        message.Content = JsonContent.Create(body);

        SendResult sendResult = await SendAsync(message, cancellationToken);

        if (sendResult.Error is not null)
        {
            yield return new ModelError(sendResult.Error);
            yield break;
        }

        using HttpResponseMessage response = sendResult.Response!;

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            yield return new ModelError(
                $"OpenAI returned " +
                $"{(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: " +
                errorBody);

            yield break;
        }

        StreamResult streamResult = await ReadStreamAsync(response, cancellationToken);

        if (streamResult.Error is not null)
        {
            yield return new ModelError(streamResult.Error);
            yield break;
        }

        await using Stream stream = streamResult.Stream!;

        await foreach (SseEvent evt in SseReader.ReadAsync(stream, cancellationToken))
        {
            ModelEvent? modelEvent = ParseEvent(evt);

            if (modelEvent is not null)
                yield return modelEvent;
        }
    }

    private async Task<SendResult> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        try
        {
            HttpResponseMessage response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return new SendResult(response, null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SendResult(null, ex.Message);
        }
    }

    private static async Task<StreamResult> ReadStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            Stream stream =  await response.Content.ReadAsStreamAsync(cancellationToken);

            return new StreamResult(stream, null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StreamResult(null, ex.Message);
        }
    }

    private static ModelEvent? ParseEvent(SseEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.Data))
            return null;

        if (evt.Data == "[DONE]")
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(evt.Data);

            JsonElement root = document.RootElement;

            string? type = root.TryGetProperty(
                "type", out JsonElement typeProperty ) ? typeProperty.GetString() : evt.Event;

            switch (type)
            {
                case "response.output_text.delta":
                    {
                        if (!root.TryGetProperty("delta", out JsonElement delta))
                        {
                            return null;
                        }

                        return new ModelTextDelta(delta.GetString() ?? string.Empty);
                    }

                case "response.completed":
                    {
                        if (!root.TryGetProperty("response", out JsonElement response))
                        {
                            return null;
                        }

                        if (!response.TryGetProperty("id", out JsonElement id))
                        {
                            return null;
                        }

                        return new ModelCompleted(id.GetString());
                    }
                case "response.output_item.done":
                    {
                        if (!root.TryGetProperty("item", out JsonElement item))
                        {
                            return null;
                        }

                        if (!item.TryGetProperty("type", out JsonElement itemType) || itemType.GetString() != "function_call")
                        {
                            return null;
                        }

                        string? id = item.TryGetProperty("call_id", out JsonElement callId) ? callId.GetString() : null;

                        string? name =
                            item.TryGetProperty("name",out JsonElement functionName) ? functionName.GetString() : null;

                        string arguments =
                            item.TryGetProperty("arguments", out JsonElement functionArguments) ? functionArguments.GetString() ?? "{}" : "{}";

                        if (string.IsNullOrWhiteSpace(id))
                        {
                            return new ModelError("OpenAI function call has an invalid call_id.");
                        }
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return new ModelError("OpenAI function call has an invalid name.");
                        }

                        return new ModelToolCall(new Tools.ToolCall(id, name, arguments));
                    }
                case "response.failed":
                    {
                        return new ModelError(ReadError(root) ?? "OpenAI response failed.");
                    }

                case "error":
                    {
                        return new ModelError(ReadError(root) ?? "OpenAI streaming error.");
                    }

                default:
                    return null;
            }
        }
        catch (JsonException ex)
        {
            return new ModelError($"Invalid OpenAI stream event: {ex.Message}");
        }
    }

    private static string? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error))
        {
            return null;
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString();
        }

        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message))
        {
            return message.GetString();
        }

        return error.ToString();
    }

    private static OpenAIFunctionTool CreateTool(Tools.ToolDefinition tool)
    {
        using JsonDocument document = JsonDocument.Parse(tool.ParametersJson);

        return new OpenAIFunctionTool
        {
            Type        = "function",
            Name        = tool.Name,
            Description = tool.Description,
            Parameters  = document.RootElement.Clone(),
            Strict      = false
        };
    }

    private sealed class OpenAIResponseRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required IReadOnlyList<object> Input { get; init; }

        [JsonPropertyName("previous_response_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreviousResponseId { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<OpenAIFunctionTool>? Tools { get; init; }

        [JsonPropertyName("parallel_tool_calls")]
        public bool ParallelToolCalls { get; init; }
        [JsonPropertyName("store")]
        public bool Store { get; init; }
    }

    protected sealed class OpenAIUserMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required string Content { get; init; }
    }

    private sealed class OpenAIFunctionTool
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("parameters")]
        public required JsonElement Parameters { get; init; }

        [JsonPropertyName("strict")]
        public bool Strict { get; init; }
    }

    // The assistant's own function_call item, so a later function_call_output can
    // pair with it when a subclass replays a full conversation history.
    protected sealed class OpenAIFunctionCallItem
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("call_id")]
        public required string CallId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("arguments")]
        public required string Arguments { get; init; }
    }

    protected sealed class OpenAIFunctionCallOutput
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("call_id")]
        public required string CallId { get; init; }

        [JsonPropertyName("output")]
        public required string Output { get; init; }
    }

    private sealed record SendResult(
        HttpResponseMessage? Response,
        string? Error);

    private sealed record StreamResult(
        Stream? Stream,
        string? Error);
}