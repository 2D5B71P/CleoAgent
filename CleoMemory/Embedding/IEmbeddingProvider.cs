using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Model.Embedding;

// Produces the vector representation of text, used both when storing memories
// and when querying for relevant ones. Kept behind an interface so the
// implementation (OpenAI embeddings, local model, hash fallback) is swappable.
internal interface IEmbeddingProvider
{
    // Returns a fixed-size float vector for the given text.
    Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
