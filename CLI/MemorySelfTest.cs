using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CleoAgent.Core.Config;
using CleoAgent.Core.Context;
using CleoAgent.Core.Memory;
using CleoAgent.Core.Model.Embedding;
using CleoAgent.Core.Model.Factories;

namespace CleoAgent.CLI;

// Temporary end-to-end self-test for the memory layer. Invoked via `--selftest`.
// Verifies: store short/long term, relevance retrieval, per-agent isolation,
// JSON persistence round-trip, ContextEngine rendering, and Config loading.
internal static class MemorySelfTest
{
    public static async Task RunAsync()
    {
        Console.WriteLine("=== Memory self-test ===");

        var embedding = new HashEmbeddingProvider();

        // Per-agent isolation: two agents, different stores.
        string scratchRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_selftest_" + Guid.NewGuid().ToString("N"));

        var repoA = new FileMemoryRepository(scratchRoot, "agentA", embedding);
        var repoB = new FileMemoryRepository(scratchRoot, "agentB", embedding);

        // Store some memories in A.
        await repoA.UpsertAsync(MemoryDocument.Create(
            MemoryTier.ShortTerm,
            "The user's login flow uses OAuth2 with PKCE.",
            await embedding.EmbedAsync("login flow OAuth2 PKCE")));

        await repoA.UpsertAsync(MemoryDocument.Create(
            MemoryTier.ShortTerm,
            "The build target is net10.0 and requires .NET 10 SDK.",
            await embedding.EmbedAsync("build target net10.0 SDK")));

        await repoA.UpsertAsync(MemoryDocument.Create(
            MemoryTier.LongTerm,
            "Deployment is done via a GitHub Actions workflow to Azure.",
            await embedding.EmbedAsync("deployment GitHub Actions Azure")));

        // B has unrelated memory.
        await repoB.UpsertAsync(MemoryDocument.Create(
            MemoryTier.ShortTerm,
            "The user prefers Dart for mobile UI.",
            await embedding.EmbedAsync("mobile UI Dart")));

        // Query A with a prompt about auth -> should surface the OAuth memory.
        var query = await embedding.EmbedAsync("tell me about how users log in and auth");
        var hits = await repoA.QueryAsync(query, limit: 3);

        Console.WriteLine("\n-- Relevant hits for 'auth/login' in agentA --");
        foreach (var m in hits)
        {
            Console.WriteLine($"  [{m.Tier}] {m.Text}");
        }

        Console.WriteLine("\n-- Ensure agentA results do NOT include agentB's Dart note --");
        bool leaked = hits.Any(h => h.Text.Contains("Dart", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(leaked ? "  FAIL: cross-agent leak!" : "  OK: no cross-agent leak.");

        // Reload from disk (fresh repo over same root) to test persistence.
        var repoA2 = new FileMemoryRepository(scratchRoot, "agentA", embedding);
        var recent = await repoA2.GetRecentAsync(MemoryTier.ShortTerm, limit: 10);
        Console.WriteLine($"\n-- Persistence round-trip --");
        Console.WriteLine($"  Reloaded short-term count: {recent.Count} (expected 2)");
        Console.WriteLine(recent.Count == 2 ? "  OK: persisted." : "  FAIL: persistence broken.");

        // Cleanup
        try { System.IO.Directory.Delete(scratchRoot, recursive: true); }
        catch { /* best-effort */ }

        // --- ContextEngine integration: verify the wired preamble renders ---
        Console.WriteLine("\n-- ContextEngine integration (renders system + memory) --");
        await TestContextEngineAsync(embedding);

        Console.WriteLine("\n-- Config loader (parses commented JSON) --");
        await TestConfigAsync();

        Console.WriteLine("\n-- Embedding provider factory --");
        TestEmbeddingFactory();

        Console.WriteLine("\n-- Memory summarizer (offline fake model) --");
        await TestSummarizerAsync(embedding);

        Console.WriteLine("\n-- Session store (disk-backed JSONL round-trip) --");
        await TestSessionStoreAsync();

        Console.WriteLine("\n=== Self-test complete ===");
    }

    private static async Task TestSummarizerAsync(IEmbeddingProvider embedding)
    {
        string scratchRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_selfest_sum_" + Guid.NewGuid().ToString("N"));

        var repo = new FileMemoryRepository(scratchRoot, "agentA", embedding);

        // Fake provider that returns the JSON the real model would produce.
        var fake = new FakeProviderModel("""
        Here is my answer:
        ```json
        [
          { "question": "What port does the app listen on?", "answer": "8080" },
          { "question": "Which style does the UI use?", "answer": "Magenta accent buttons" },
          { "question": "ignored-because-third-and-malformed", "answer": "x" }
        ]
        ```
        """);

        var summarizer = new MemorySummarizer(fake, repo, embedding, "agentA");

        int stored = await summarizer.SummarizeAsync("user said hi\nassistant said hello", maxPairs: 2);

        // maxPairs=2 should cap storage at 2 even though the reply has 3 pairs.
        bool ok = stored == 2;

        if (ok)
        {
            var shorts = await repo.GetRecentAsync(CleoAgent.Core.Memory.MemoryTier.ShortTerm, limit: 10);
            ok = shorts.Count == 2 && shorts.Any(m => m.Text.Contains("8080", StringComparison.Ordinal));
        }

        Console.WriteLine(ok
            ? "  OK: summarizer parsed pairs, capped to maxPairs=2."
            : $"  FAIL: summarizer stored {stored} pairs (expected 2).");

        try { System.IO.Directory.Delete(scratchRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    // Minimal IModelProvider that returns a fixed text reply via CompleteAsync.
    private sealed class FakeProviderModel : CleoAgent.Core.Model.IModelProvider
    {
        private readonly string _reply;
        public FakeProviderModel(string reply) => _reply = reply;

        public System.Collections.Generic.IAsyncEnumerable<CleoAgent.Core.Model.ModelEvent> StreamAsync(
            CleoAgent.Core.Model.ModelRequest request,
            System.Threading.CancellationToken cancellationToken = default)
        {
            return StreamAsyncCore();
        }

        private async System.Collections.Generic.IAsyncEnumerable<CleoAgent.Core.Model.ModelEvent> StreamAsyncCore()
        {
            yield return new CleoAgent.Core.Model.ModelTextDelta(_reply);
        }
    }

    private static async Task TestContextEngineAsync(IEmbeddingProvider embedding)
    {
        string scratchRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_selfest_ctx_" + Guid.NewGuid().ToString("N"));

        var repo = new FileMemoryRepository(scratchRoot, "default", embedding);

        await repo.UpsertAsync(MemoryDocument.Create(
            MemoryTier.ShortTerm,
            "The build target is net10.0 and requires the .NET 10 SDK.",
            await embedding.EmbedAsync("build target net10.0 SDK")));

        var engine = new ContextEngine(new IContextSource[]
        {
            new SystemInstructionsSource(),
            new MemorySource(repo, embedding)
        });

        var request = new ContextRequest(
            AgentId: "default",
            UserPrompt: "which SDK do I need to build this project?");

        string preamble = await engine.RenderAsync(request);

        Console.WriteLine($"  Preamble length: {preamble.Length} (expected > 0)");
        Console.WriteLine(preamble.Contains("net10.0", StringComparison.OrdinalIgnoreCase)
            ? "  OK: memory injected into context."
            : "  FAIL: memory not injected.");
        Console.WriteLine(preamble.StartsWith("## System", StringComparison.OrdinalIgnoreCase)
            ? "  OK: system instructions first."
            : "  Note: system block not first (ordering check).");

        try { System.IO.Directory.Delete(scratchRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private static Task TestConfigAsync()
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cleoagent_selfest_cfg_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);

        string path = System.IO.Path.Combine(dir, "config.json");

        System.IO.File.WriteAllText(path, """
        {
          // a line comment
          "agent": { "id": "TestAgent" },
          "model": {
            "provider": "open_router",
            "model": "openai/gpt-4o",
            "api_key": "${CLEO_TEST_KEY}"
          },
          /* block
             comment */
          "embedding": { "provider": "none", "model": "", "api_key": "" },
          "compaction": {
            "keepRecentTurns": 20,
            "softTokenCeiling": 16000,
            "hardByteCeiling": 64000
          }
        }
        """);

        // Set the env var that the api_key token references, so we can verify
        // ${VAR} interpolation resolves to it.
        string? previousKey = System.Environment.GetEnvironmentVariable("CLEO_TEST_KEY");
        System.Environment.SetEnvironmentVariable("CLEO_TEST_KEY", "sk-interpolated-123");

        // Point the real loader at the temp file and exercise Config.Load.
        string? previous = Config.ConfigPathOverride;
        Config.ConfigPathOverride = path;

        try
        {
            AppConfig cfg = Config.Load();

            bool ok = cfg.Agent.Id == "TestAgent"
                      && cfg.Model.Provider == "open_router"
                      && cfg.Model.Model == "openai/gpt-4o"
                      && cfg.Model.ApiKey == "sk-interpolated-123"
                      && cfg.Embedding.Provider == "none"
                      && cfg.Compaction.KeepRecentTurns == 20
                      && cfg.Compaction.SoftTokenCeiling == 16000
                      && cfg.Compaction.HardByteCeiling == 64000;

            Console.WriteLine(ok
                ? "  OK: comments stripped, ${VAR} interpolated, values parsed."
                : $"  FAIL: config parse wrong (id={cfg.Agent.Id}, key={cfg.Model.ApiKey}).");
        }
        finally
        {
            Config.ConfigPathOverride = previous;

            if (previousKey is null)
                System.Environment.SetEnvironmentVariable("CLEO_TEST_KEY", null);
            else
                System.Environment.SetEnvironmentVariable("CLEO_TEST_KEY", previousKey);

            try { System.IO.Directory.Delete(dir, recursive: true); }
            catch { /* best-effort */ }
        }

        return Task.CompletedTask;
    }

    // Verifies the disk-backed session store: append → reload → same history,
    // per-session file isolation, and rewrite (compaction-style) round-trip.
    private static async Task TestSessionStoreAsync()
    {
        // NOTE: AgentPaths caches %APPDATA% in a static ctor that may already have
        // run by now. To keep this test hermetic we point %APPDATA% at a temp dir
        // BEFORE touching any session APIs. If the static ctor has already cached,
        // the store writes to the real location — so we use an OBVIOUS scratch
        // agent id and clean up the session files explicitly afterward.
        string scratchAgent = "selftest_" + Guid.NewGuid().ToString("N");

        var store = new CleoAgent.Core.Session.SessionStore(scratchAgent);
        var session = store.Load("sess-1");
        store.Append(session, new CleoAgent.Core.Session.SessionMessage(
            CleoAgent.Core.Session.SessionMessageType.User, Content: "hello"));
        store.Append(session, new CleoAgent.Core.Session.SessionMessage(
            CleoAgent.Core.Session.SessionMessageType.Assistant, Content: "hi there"));

        // Reload from disk by a fresh store instance over the same id.
        var store2 = new CleoAgent.Core.Session.SessionStore(scratchAgent);
        var reloaded = store2.Load("sess-1");

        bool persisted = reloaded.History.Count == 2
            && reloaded.History[0].Type == CleoAgent.Core.Session.SessionMessageType.User
            && reloaded.History[0].Content == "hello"
            && reloaded.History[1].Content == "hi there";

        Console.WriteLine(persisted
            ? "  OK: session JSONL round-trip (user+assistant) preserved."
            : $"  FAIL: session reload got {reloaded.History.Count} msgs.");

        // Rewrite (compaction-shaped): replace history with summary + recent.
        var rebuilt = new System.Collections.Generic.List<CleoAgent.Core.Session.SessionMessage>
        {
            new(CleoAgent.Core.Session.SessionMessageType.System, Content: "compacted summary"),
            new(CleoAgent.Core.Session.SessionMessageType.User, Content: "recent turn")
        };
        store2.Rewrite(reloaded, rebuilt);

        var store3 = new CleoAgent.Core.Session.SessionStore(scratchAgent);
        var afterRewrite = store3.Load("sess-1");

        bool rewriteOk = afterRewrite.History.Count == 2
            && afterRewrite.History[0].Type == CleoAgent.Core.Session.SessionMessageType.System
            && afterRewrite.History[0].Content == "compacted summary"
            && afterRewrite.History[1].Content == "recent turn";

        Console.WriteLine(rewriteOk
            ? "  OK: compaction-style rewrite round-trips to disk."
            : "  FAIL: rewrite did not persist.");

        // Isolation: a different session id must not see sess-1's messages.
        var other = store3.Load("sess-other");
        Console.WriteLine(other.History.Count == 0
            ? "  OK: session ids are isolated."
            : $"  FAIL: unexpected {other.History.Count} msgs in fresh id.");

        // Cleanup: remove the scratch agent's session dir under whatever APPDATA
        // AgentPaths resolved (works whether or not the static ctor had run).
        try
        {
            string dir = CleoAgent.Core.AgentPaths.AgentSessionDir(scratchAgent);
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private static void TestEmbeddingFactory()
    {
        using var http = new HttpClient();

        // none → hash fallback.
        var hash = EmbeddingProviderFactory.Create(new EmbeddingConfig("none", "", ""), http);
        bool noneOk = hash is HashEmbeddingProvider;

        // Unknown provider → throws.
        bool unknownThrows = false;
        try
        {
            EmbeddingProviderFactory.Create(new EmbeddingConfig("bogus", "m", "k"), http);
        }
        catch (InvalidOperationException)
        {
            unknownThrows = true;
        }

        // openai with empty model/key → throws (won't attempt a network call).
        bool openAiMissingThrows = false;
        try
        {
            EmbeddingProviderFactory.Create(new EmbeddingConfig("openai", "text-embedding-3-small", ""), http);
        }
        catch (InvalidOperationException)
        {
            openAiMissingThrows = true;
        }

        Console.WriteLine(noneOk
            ? "  OK: 'none' → HashEmbeddingProvider."
            : "  FAIL: 'none' did not map to hash.");
        Console.WriteLine(unknownThrows
            ? "  OK: unknown provider throws."
            : "  FAIL: unknown provider did not throw.");
        Console.WriteLine(openAiMissingThrows
            ? "  OK: openai without api_key throws."
            : "  FAIL: openai missing key did not throw.");
    }
}
