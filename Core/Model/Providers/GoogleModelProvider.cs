/*
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CleoAgent.Core.Tools;
using Google.GenAI;
using Google.GenAI.Types;

namespace CleoAgent.Core.Model.Providers
{
    internal sealed class GoogleModelProvider : IModelProvider
    {
        private readonly Client _client;
        private readonly string _model;

        private readonly Dictionary<string, Session> _sessions = new();

        public GoogleModelProvider(string apiKey, string model)
        {
            _client = new Client(apiKey: apiKey);
            _model = model;
        }

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Session session = GetOrCreateSession(request.ContinuationToken);

            var contents = new List<Content>(session.History);

            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                contents.Add(new Content
                {
                    Role  = "user",
                    Parts =
                    [
                        new Part
                        {
                            Text = request.Prompt
                        }
                    ]
                });
            }

            if (request.ToolResult is not null)
            {
                if (!session.ToolCalls.TryGetValue(request.ToolResult.ToolCallId, out string? functionName))
                {
                    yield return new ModelError($"Unknown Google tool call id '{request.ToolResult.ToolCallId}'.");
                    yield break;
                }

                contents.Add(new Content
                {
                    Role  = "user",
                    Parts =
                    [
                        new Part
                        {
                            FunctionResponse = new FunctionResponse
                            {
                                Id   = request.ToolResult.ToolCallId,
                                Name = functionName,
                                Response = new Dictionary<string, object>
                                {
                                    ["output"] = request.ToolResult.Output
                                }
                            }
                        }
                    ]
                });
            }

            var config = new GenerateContentConfig();

            if (request.Tools.Count > 0)
            {
                var declarations = new List<FunctionDeclaration>();

                foreach (ToolDefinition tool in request.Tools)
                {
                    declarations.Add(new FunctionDeclaration
                    {
                        Name        = tool.Name,
                        Description = tool.Description,
                        ParametersJsonSchema = JsonNode.Parse(tool.ParametersJson)
                    });
                }

                config.Tools =
                [
                    new Google.GenAI.Types.Tool
                    {
                        FunctionDeclarations = declarations
                    }
                ];
            }

            var modelParts = new List<Part>();

            await foreach (GenerateContentResponse chunk in _client.Models.GenerateContentStreamAsync(
                model: _model,
                contents: contents,
                config: config,
                cancellationToken: cancellationToken))
            {
                if (chunk.Candidates is null)
                {
                    continue;
                }

                foreach (var candidate in chunk.Candidates)
                {
                    if (candidate.Content?.Parts is null)
                    {
                        continue;
                    }

                    foreach (Part part in candidate.Content.Parts)
                    {
                        modelParts.Add(part);

                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            yield return new ModelTextDelta(part.Text);
                        }

                        if (part.FunctionCall is not null)
                        {
                            FunctionCall functionCall = part.FunctionCall;

                            string functionName = functionCall.Name ?? 
                                throw new InvalidOperationException("Google returned a function call without a name.");

                            string callId = functionCall.Id
                                ?? Guid.NewGuid().ToString("N");

                            string argumentsJson = JsonSerializer.Serialize(functionCall.Args
                                    ?? new Dictionary<string, object>());

                            session.ToolCalls[callId] = functionName;

                            yield return new ModelToolCall(new Tools.ToolCall(callId, functionName, argumentsJson));
                        }
                    }
                }
            }

            // Only commit the request to history after the request
            // successfully completed.
            session.History.Clear();
            session.History.AddRange(contents);

            if (modelParts.Count > 0)
            {
                session.History.Add(new Content
                {
                    Role = "model",
                    Parts = modelParts
                });
            }

            yield return new ModelCompleted(session.Id);
        }

        private Session GetOrCreateSession(string? continuationToken)
        {
            if (continuationToken is not null && _sessions.TryGetValue(continuationToken, out Session? existing))
            {
                return existing;
            }

            var session = new Session(Guid.NewGuid().ToString("N"));

            _sessions[session.Id] = session;

            return session;
        }

        private sealed class Session(string id)
        {
            public string Id { get; } = id;
            public List<Content> History { get; } = [];
            public Dictionary<string, string> ToolCalls { get; } = [];
        }
    }
}
*/