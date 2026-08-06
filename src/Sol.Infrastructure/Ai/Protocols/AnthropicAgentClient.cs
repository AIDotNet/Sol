using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

internal sealed class AnthropicAgentClient(IHttpClientFactory httpClientFactory) : IAgentModelClient
{
    public ProviderType Protocol => ProviderType.Anthropic;

    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["system"] = request.SystemPrompt,
            ["messages"] = AgentProtocolJson.AnthropicMessages(request.Messages),
        };
        if (request.Tools.Count > 0) body["tools"] = AgentProtocolJson.AnthropicTools(request.Tools);

        // The provider may be declared as OpenAI-compatible while this model overrides the
        // protocol to Anthropic. In that case the stored base URL commonly ends in /v1, but
        // Anthropic owns that segment and adds it to the messages route itself.
        var baseUrl = AiProvider.NormalizeBaseUrl(request.Provider.BaseUrl, ProviderType.Anthropic);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", request.ApiKey);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var client = httpClientFactory.CreateClient("upstream");
        using var response = await client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            yield return new AgentModelEvent(
                AgentModelEventKind.Failed,
                Error: AgentProtocolJson.Error(errorBody, (int)response.StatusCode));
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var toolBlocks = new Dictionary<int, PendingTool>();
        string? stopReason = null;
        int? inputTokens = null;
        int? outputTokens = null;

        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data.Length == 0) continue;

            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var type = String(root, "type") ?? frame.EventName;

            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var started)
                        && started.TryGetProperty("usage", out var usage))
                    {
                        inputTokens = Int(usage, "input_tokens");
                    }
                    break;

                case "content_block_start":
                    if (!root.TryGetProperty("index", out var startIndex)
                        || !root.TryGetProperty("content_block", out var content)) break;
                    if (String(content, "type") == "tool_use")
                    {
                        toolBlocks[startIndex.GetInt32()] = new PendingTool(
                            String(content, "id") ?? string.Empty,
                            String(content, "name") ?? string.Empty,
                            new StringBuilder());
                    }
                    break;

                case "content_block_delta":
                    if (!root.TryGetProperty("delta", out var delta)) break;
                    var deltaType = String(delta, "type");
                    if (deltaType == "text_delta" && String(delta, "text") is { } text)
                    {
                        yield return new AgentModelEvent(AgentModelEventKind.TextDelta, Text: text);
                    }
                    else if (deltaType == "thinking_delta" && String(delta, "thinking") is { } thinking)
                    {
                        yield return new AgentModelEvent(AgentModelEventKind.ThinkingDelta, Text: thinking);
                    }
                    else if (deltaType == "input_json_delta"
                             && root.TryGetProperty("index", out var deltaIndex)
                             && toolBlocks.TryGetValue(deltaIndex.GetInt32(), out var pending)
                             && String(delta, "partial_json") is { } partial)
                    {
                        pending.Json.Append(partial);
                    }
                    break;

                case "content_block_stop":
                    if (root.TryGetProperty("index", out var stopIndex)
                        && toolBlocks.Remove(stopIndex.GetInt32(), out var tool))
                    {
                        var json = tool.Json.Length == 0 ? "{}" : tool.Json.ToString();
                        try
                        {
                            using var _ = JsonDocument.Parse(json);
                        }
                        catch (JsonException)
                        {
                            json = "{}";
                        }

                        yield return new AgentModelEvent(
                            AgentModelEventKind.ToolUse,
                            ToolUseId: tool.Id,
                            ToolName: tool.Name,
                            Json: json);
                    }
                    break;

                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta))
                    {
                        stopReason = String(messageDelta, "stop_reason") ?? stopReason;
                    }
                    if (root.TryGetProperty("usage", out var deltaUsage))
                    {
                        outputTokens = Int(deltaUsage, "output_tokens") ?? outputTokens;
                    }
                    break;

                case "error":
                    var error = root.TryGetProperty("error", out var errorObject)
                        ? String(errorObject, "message")
                        : null;
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Failed,
                        Error: error ?? "The Anthropic stream reported an error.");
                    yield break;

                case "message_stop":
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Completed,
                        StopReason: stopReason ?? "end_turn",
                        InputTokens: inputTokens,
                        OutputTokens: outputTokens);
                    yield break;
            }
        }

        yield return new AgentModelEvent(
            AgentModelEventKind.Failed,
            Error: "The Anthropic stream ended before message_stop.");
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number)
            ? number
            : null;

    private sealed record PendingTool(string Id, string Name, StringBuilder Json);
}
