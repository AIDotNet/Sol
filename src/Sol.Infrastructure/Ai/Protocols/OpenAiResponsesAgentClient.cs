using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;
using Sol.Domain.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

/// <summary>
/// OpenAI Responses streaming for Agent turns (<c>POST /responses</c>).
/// </summary>
/// <remarks>
/// Responses represents tool calls as top-level output items rather than Chat Completions
/// messages. The client translates those items into the Agent's protocol-neutral events; the
/// turn runner executes the tools and sends the resulting function-call-output items on the next
/// iteration.
/// </remarks>
internal sealed class OpenAiResponsesAgentClient(IHttpClientFactory httpClientFactory)
    : IAgentModelClient
{
    public ProviderType Protocol => ProviderType.OpenAiResponses;

    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(
        AgentModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = request.ModelKey,
            ["input"] = AgentProtocolJson.ResponsesInput(request.Messages),
            ["stream"] = true,
            ["max_output_tokens"] = request.MaxOutputTokens,
        };

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            body["instructions"] = request.SystemPrompt;
        }

        if (request.Tools.Count > 0)
        {
            body["tools"] = AgentProtocolJson.ResponsesTools(request.Tools);
        }

        if (request.SupportsThinking)
        {
            // Reasoning summaries are safe to replay as display-only Agent thinking. The
            // encrypted reasoning item itself is deliberately not persisted or reconstructed.
            body["reasoning"] = new JsonObject { ["summary"] = "auto" };
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{request.Provider.BaseUrl.TrimEnd('/')}/responses")
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
        var functionCalls = new Dictionary<string, PendingFunctionCall>(StringComparer.Ordinal);
        var emittedCalls = new HashSet<string>(StringComparer.Ordinal);
        var sawToolCall = false;
        var sawRefusal = false;

        await foreach (var frame in SseReader.ReadAsync(stream, ct))
        {
            if (frame.Data.Length == 0) continue;
            if (frame.Data == "[DONE]") yield break;

            using var document = JsonDocument.Parse(frame.Data);
            var root = document.RootElement;
            var type = String(root, "type") ?? frame.EventName;

            switch (type)
            {
                case "response.output_text.delta":
                    if (String(root, "delta") is { Length: > 0 } text)
                    {
                        yield return new AgentModelEvent(
                            AgentModelEventKind.TextDelta, Text: text);
                    }
                    break;

                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (String(root, "delta") is { Length: > 0 } thinking)
                    {
                        yield return new AgentModelEvent(
                            AgentModelEventKind.ThinkingDelta, Text: thinking);
                    }
                    break;

                case "response.refusal.delta":
                    sawRefusal = true;
                    if (String(root, "delta") is { Length: > 0 } refusal)
                    {
                        yield return new AgentModelEvent(
                            AgentModelEventKind.TextDelta, Text: refusal);
                    }
                    break;

                case "response.output_item.added":
                    if (root.TryGetProperty("item", out var added))
                    {
                        sawToolCall |= UpsertFunctionCall(
                            functionCalls, added, replaceArguments: false) is not null;
                    }
                    break;

                case "response.function_call_arguments.delta":
                {
                    var key = EventItemKey(root);
                    if (key is null) break;
                    var call = GetFunctionCall(functionCalls, key);
                    call.CallId = String(root, "call_id") ?? call.CallId;
                    call.Arguments.Append(String(root, "delta") ?? string.Empty);
                    sawToolCall = true;
                    break;
                }

                case "response.function_call_arguments.done":
                {
                    var key = EventItemKey(root);
                    if (key is null) break;
                    var call = GetFunctionCall(functionCalls, key);
                    call.CallId = String(root, "call_id") ?? call.CallId;
                    if (String(root, "arguments") is { } arguments)
                    {
                        call.SetArguments(arguments);
                    }
                    sawToolCall = true;
                    break;
                }

                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var doneItem)
                        && UpsertFunctionCall(functionCalls, doneItem, replaceArguments: true)
                            is { } doneCall)
                    {
                        sawToolCall = true;
                        if (BuildToolEvent(doneCall, emittedCalls) is { } tool)
                        {
                            yield return tool;
                        }
                    }
                    break;

                case "response.completed":
                {
                    var completed = ResponsePayload(root);
                    if (completed.TryGetProperty("output", out var output)
                        && output.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in output.EnumerateArray())
                        {
                            if (UpsertFunctionCall(
                                    functionCalls, item, replaceArguments: true)
                                is not { } call)
                            {
                                continue;
                            }

                            sawToolCall = true;
                            if (BuildToolEvent(call, emittedCalls) is { } tool)
                            {
                                yield return tool;
                            }
                        }
                    }

                    // A compatible relay may omit the final output array while still sending the
                    // function-call argument events. Flush those pending calls before completing.
                    foreach (var call in functionCalls.Values)
                    {
                        if (BuildToolEvent(call, emittedCalls) is { } tool)
                        {
                            yield return tool;
                        }
                    }

                    var stopReason = sawRefusal
                        ? "refusal"
                        : sawToolCall ? "tool_calls" : "end_turn";
                    yield return Completed(completed, stopReason);
                    yield break;
                }

                case "response.incomplete":
                {
                    var incomplete = ResponsePayload(root);
                    var reason = incomplete.TryGetProperty("incomplete_details", out var details)
                        ? String(details, "reason")
                        : null;
                    yield return Completed(
                        incomplete,
                        reason == "max_output_tokens" ? "max_tokens" : reason ?? "incomplete");
                    yield break;
                }

                case "response.failed":
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Failed,
                        Error: ResponseError(ResponsePayload(root)));
                    yield break;

                case "error":
                    yield return new AgentModelEvent(
                        AgentModelEventKind.Failed,
                        Error: ResponseError(root));
                    yield break;
            }
        }

        yield return new AgentModelEvent(
            AgentModelEventKind.Failed,
            Error: "The Responses stream ended before response.completed.");
    }

    private static AgentModelEvent Completed(JsonElement response, string stopReason) =>
        new(
            AgentModelEventKind.Completed,
            StopReason: stopReason,
            InputTokens: UsageInt(response, "input_tokens"),
            OutputTokens: UsageInt(response, "output_tokens"));

    private static int? UsageInt(JsonElement response, string name) =>
        response.TryGetProperty("usage", out var usage)
        && usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static JsonElement ResponsePayload(JsonElement root) =>
        root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            ? response
            : root;

    private static string ResponseError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "Responses failed.";
            if (error.ValueKind == JsonValueKind.Object
                && String(error, "message") is { } message)
            {
                return message;
            }
        }

        return String(root, "message") ?? "The Responses stream reported a failure.";
    }

    private static PendingFunctionCall? UpsertFunctionCall(
        Dictionary<string, PendingFunctionCall> calls,
        JsonElement item,
        bool replaceArguments)
    {
        if (String(item, "type") != "function_call") return null;

        var key = String(item, "id") ?? String(item, "call_id");
        if (key is null) return null;

        var call = GetFunctionCall(calls, key);
        call.CallId = String(item, "call_id") ?? call.CallId;
        call.Name = String(item, "name") ?? call.Name;
        if (String(item, "arguments") is { } arguments)
        {
            if (replaceArguments) call.SetArguments(arguments);
            else if (call.Arguments.Length == 0) call.SetArguments(arguments);
        }
        return call;
    }

    private static PendingFunctionCall GetFunctionCall(
        Dictionary<string, PendingFunctionCall> calls,
        string key)
    {
        if (!calls.TryGetValue(key, out var call))
        {
            call = new PendingFunctionCall(key);
            calls[key] = call;
        }
        return call;
    }

    private static AgentModelEvent? BuildToolEvent(
        PendingFunctionCall call,
        HashSet<string> emittedCalls)
    {
        if (!emittedCalls.Add(call.Key)) return null;

        return new AgentModelEvent(
            AgentModelEventKind.ToolUse,
            ToolUseId: call.CallId.Length > 0 ? call.CallId : call.Key,
            ToolName: call.Name,
            Json: call.Arguments.Length > 0 ? call.Arguments.ToString() : "{}",
            ProviderItemId: call.Key);
    }

    private static string? EventItemKey(JsonElement root) =>
        String(root, "item_id")
        ?? String(root, "id")
        ?? (root.TryGetProperty("output_index", out var outputIndex)
            && outputIndex.TryGetInt32(out var index)
                ? $"output-{index}"
                : null);

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed class PendingFunctionCall(string key)
    {
        public string Key { get; } = key;
        public string CallId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();

        public void SetArguments(string value)
        {
            Arguments.Clear();
            Arguments.Append(value);
        }
    }
}
