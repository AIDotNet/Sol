using System.Runtime.CompilerServices;
using System.Text;

namespace Sol.Infrastructure.Ai.Protocols;

internal sealed record SseFrame(string? EventName, string Data);

/// <summary>Reads Server-Sent Events without buffering the response body.</summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseFrame> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        string? eventName = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    // Each data line contributes one newline. The final separator is not part of
                    // the payload, so trim exactly one rather than arbitrary whitespace.
                    data.Length -= 1;
                    yield return new SseFrame(eventName, data.ToString());
                }

                eventName = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':') continue; // heartbeat/comment

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? string.Empty : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    break;
            }
        }

        // A conforming server writes a blank line, but accepting EOF avoids dropping the last
        // JSON object when a relay closes immediately after [DONE].
        if (data.Length > 0)
        {
            data.Length -= 1;
            yield return new SseFrame(eventName, data.ToString());
        }
    }
}
