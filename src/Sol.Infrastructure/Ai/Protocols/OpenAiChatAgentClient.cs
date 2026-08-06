using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

internal sealed class OpenAiChatAgentClient(IHttpClientFactory httpClientFactory) : IAgentModelClient
{
    public ProviderType Protocol => ProviderType.OpenAiChat;

    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = AgentProtocolJson.OpenAiMessages(request.SystemPrompt, request.Messages),
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = AgentProtocolJson.OpenAiTools(request.Tools);
            body["parallel_tool_calls"] = false;
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{request.Provider.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);

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
        var tools = new Dictionary<int, PendingTool>();
        string? stopReason = null;
        int? inputTokens = null;
        int? outputTokens = null;

        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data == "[DONE]")
            {
                foreach (var tool in tools.OrderBy(pair => pair.Key).Select(pair => pair.Value))
                {
                    yield return CompleteTool(tool);
                }

                yield return new AgentModelEvent(
                    AgentModelEventKind.Completed,
                    StopReason: stopReason ?? (tools.Count > 0 ? "tool_calls" : "end_turn"),
                    InputTokens: inputTokens,
                    OutputTokens: outputTokens);
                yield break;
            }

            if (frame.Data.Length == 0) continue;
            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;

            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = Int(usage, "prompt_tokens") ?? inputTokens;
                outputTokens = Int(usage, "completion_tokens") ?? outputTokens;
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            stopReason = String(choice, "finish_reason") ?? stopReason;
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (String(delta, "content") is { } text && text.Length > 0)
            {
                yield return new AgentModelEvent(AgentModelEventKind.TextDelta, Text: text);
            }

            if (String(delta, "reasoning_content") is { } thinking && thinking.Length > 0)
            {
                yield return new AgentModelEvent(AgentModelEventKind.ThinkingDelta, Text: thinking);
            }

            if (!delta.TryGetProperty("tool_calls", out var calls)
                || calls.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var call in calls.EnumerateArray())
            {
                if (!call.TryGetProperty("index", out var indexElement)
                    || !indexElement.TryGetInt32(out var index)) continue;

                if (!tools.TryGetValue(index, out var pending))
                {
                    pending = new PendingTool(new StringBuilder(), new StringBuilder(), new StringBuilder());
                    tools[index] = pending;
                }

                if (String(call, "id") is { } id) pending.Id.Append(id);
                if (!call.TryGetProperty("function", out var function)) continue;
                if (String(function, "name") is { } name) pending.Name.Append(name);
                if (String(function, "arguments") is { } arguments) pending.Json.Append(arguments);
            }
        }

        yield return new AgentModelEvent(
            AgentModelEventKind.Failed,
            Error: "The OpenAI-compatible stream ended before [DONE].");
    }

    private static AgentModelEvent CompleteTool(PendingTool tool)
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

        return new AgentModelEvent(
            AgentModelEventKind.ToolUse,
            ToolUseId: tool.Id.ToString(),
            ToolName: tool.Name.ToString(),
            Json: json);
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number)
            ? number
            : null;

    private sealed record PendingTool(StringBuilder Id, StringBuilder Name, StringBuilder Json);
}
