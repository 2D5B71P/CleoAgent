using System.Runtime.CompilerServices;
using System.Text;

namespace CleoAgent.Core.Model.Providers
{
    internal sealed record SseEvent(
        string? Event,
        string Data);

    internal static class SseReader
    {
        public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation]CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(stream);

            string? eventName = null;
            var data = new StringBuilder();

            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);

                if (line is null)
                    break;

                // Blank line terminates an SSE event.
                if (line.Length == 0)
                {
                    if (data.Length == 0)
                        continue;

                    yield return new SseEvent(eventName, data.ToString());

                    eventName = null;
                    data.Clear();
                    continue;
                }

                // SSE comments / keep-alives.
                if (line[0] == ':')
                    continue;

                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventName = line[6..].TrimStart();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                        data.Append('\n');

                    data.Append(line[5..].TrimStart());
                }
            }

            if (data.Length > 0)
            {
                yield return new SseEvent(eventName, data.ToString());
            }
        }
    }
}