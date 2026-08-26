using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CleoAgent.Core.Model.Embedding;

// A deterministic, dependency-free embedding used as the offline fallback so
// the memory pipeline can run end-to-end without an embedding API. It is NOT
// semantically meaningful — it only guarantees stable vectors for exact/similar
// text. Swap for a real provider (OpenAI, local model) in production.
internal sealed class HashEmbeddingProvider : IEmbeddingProvider
{
    private const int Dimensions = 128;

    public Task<IReadOnlyList<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        var vector = new float[Dimensions];

        int n = 0;

        foreach (char c in text)
        {
            int index = (int)((uint)(c * 31 + n++) % Dimensions);
            vector[index] += 1f;
        }

        // Fold in character n-grams a little so ordering matters somewhat.
        for (int i = 1; i < bytes.Length; i++)
        {
            int index = (int)((uint)((bytes[i - 1] << 8) | bytes[i]) % Dimensions);
            vector[index] += 0.5f;
        }

        Normalize(vector);

        return Task.FromResult<IReadOnlyList<float>>(vector);
    }

    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (float v in vector)
        {
            sum += v * v;
        }

        if (sum <= 0)
        {
            return;
        }

        double norm = System.Math.Sqrt(sum);

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }
}
