using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleoAgent.Core.Config;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model.Embedding;
using CleoAgent.Core.Model.Factories;
using CleoAgent.Core.Modules;
using CleoAgent.Core.Tools;
using CleoAgent.Core.Tools.Impl;

namespace CleoAgent.Core.Modules;

// The memory module: agent-driven memory tools (memory_write/retrieve/forget/
// clear) with real embeddings. Owns the embedding provider selection (its
// config section) and publishes the repository + embedding provider as
// services for future consumers.
internal sealed class MemoryModule : IModule
{
    private readonly HttpClient _http;

    public MemoryModule(HttpClient http)
    {
        _http = http;
    }

    public ModuleManifest Manifest() => ModuleManifest.Create(
        "memory",
        "1.0.0",
        "Agent-driven semantic memory: memory_write, memory_retrieve, " +
        "memory_forget, memory_clear (embeddings via the [embedding] config " +
        "section; provider + repository published as services).",
        configSection: "embedding",
        modelRequestable: true);

    public async Task LoadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        ConfigSection cfg = ctx.Config;

        // Embedding provider selection: same fields as the legacy [embedding]
        // section - provider ("none" = local hash, "openai"/"openrouter" = API),
        // model, api_key. Defaults match EmbeddingConfig.Default.
        var embeddingConfig = new EmbeddingConfig(
            cfg.GetString("provider", EmbeddingConfig.Default.Provider),
            cfg.GetString("model", EmbeddingConfig.Default.Model),
            cfg.GetString("api_key", EmbeddingConfig.Default.ApiKey));

        IEmbeddingProvider embedding = EmbeddingProviderFactory.Create(embeddingConfig, _http);
        IMemoryRepository repository = new FileMemoryRepository(ctx.AgentsRoot, ctx.AgentId, embedding);

        // Publish services by name for future consumers (Phase 3+ modules).
        ctx.Services.Register("memory", repository);
        ctx.Services.Register("embedding", embedding);

        ctx.Tools.Register(new MemoryWriteTool(repository, embedding));
        ctx.Tools.Register(new MemoryRetrieveTool(repository, embedding));
        ctx.Tools.Register(new MemoryForgetTool(repository, embedding));
        ctx.Tools.Register(new MemoryClearTool(repository));
    }

    public async Task UnloadAsync(ModuleContext ctx, CancellationToken cancellationToken = default)
    {
        // No unregisterable state; services are additive.
    }
}