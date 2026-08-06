using System.Text.Json;
using System.Text.Json.Nodes;
using Sol.Application.Abstractions.Ai;

namespace Sol.Infrastructure.Ai.Protocols;

internal static class AgentProtocolJson
{
    public static JsonNode ParseNode(string json, string fallback)
    {
        try
        {
            return JsonNode.Parse(json) ?? JsonNode.Parse(fallback)!;
        }
        catch (JsonException)
        {
            return JsonNode.Parse(fallback)!;
        }
    }

    public static JsonArray AnthropicMessages(IReadOnlyList<AgentModelMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            var content = new JsonArray();
            foreach (var block in message.Content)
            {
                switch (block.Kind)
                {
                    case AgentContentKind.Text:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = block.Text ?? string.Empty,
                        });
                        break;
                    case AgentContentKind.Image:
                        if (block.ImageBytes is { Length: > 0 }
                            && !string.IsNullOrWhiteSpace(block.MediaType))
                        {
                            content.Add((JsonNode)new JsonObject
                            {
                                ["type"] = "image",
                                ["source"] = new JsonObject
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = block.MediaType,
                                    ["data"] = Convert.ToBase64String(block.ImageBytes),
                                },
                            });
                        }
                        break;
                    case AgentContentKind.ToolUse:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = block.ToolUseId,
                            ["name"] = block.ToolName,
                            ["input"] = ParseNode(block.Json ?? "{}", "{}"),
                        });
                        break;
                    case AgentContentKind.ToolResult:
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = block.ToolUseId,
                            ["content"] = block.Text ?? block.Json ?? string.Empty,
                            ["is_error"] = block.IsError,
                        });
                        break;
                    // Thinking is emitted for UI progress but is not requested in phase one. A
                    // future thinking-enabled client must preserve Anthropic's signature before
                    // replaying it; silently replaying reconstructed text would be rejected.
                    case AgentContentKind.Thinking:
                        break;
                }
            }

            result.Add((JsonNode)new JsonObject
            {
                ["role"] = message.Role == AgentMessageRole.Assistant ? "assistant" : "user",
                ["content"] = content,
            });
        }

        return result;
    }

    public static JsonArray AnthropicTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = ParseNode(tool.InputSchemaJson, "{\"type\":\"object\"}"),
            });
        }
        return result;
    }

    public static JsonArray OpenAiMessages(
        string systemPrompt,
        IReadOnlyList<AgentModelMessage> messages)
    {
        var result = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            result.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        }

        foreach (var message in messages)
        {
            if (message.Role == AgentMessageRole.User)
            {
                foreach (var block in message.Content.Where(block => block.Kind == AgentContentKind.ToolResult))
                {
                    result.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = block.ToolUseId,
                        ["content"] = block.Text ?? block.Json ?? string.Empty,
                    });
                }

                var text = string.Concat(message.Content
                    .Where(block => block.Kind == AgentContentKind.Text)
                    .Select(block => block.Text));
                var images = message.Content
                    .Where(block => block.Kind == AgentContentKind.Image)
                    .Select(ImageDataUrl)
                    .Where(url => url is not null)
                    .Select(url => url!)
                    .ToList();
                if (text.Length > 0 && images.Count == 0)
                {
                    result.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = text });
                }
                else if (text.Length > 0 || images.Count > 0)
                {
                    var content = new JsonArray();
                    if (text.Length > 0)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = text,
                        });
                    }
                    foreach (var image in images)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = image },
                        });
                    }
                    result.Add((JsonNode)new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = content,
                    });
                }
                continue;
            }

            var assistant = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = string.Concat(message.Content
                    .Where(block => block.Kind == AgentContentKind.Text)
                    .Select(block => block.Text)),
            };
            var calls = new JsonArray();
            foreach (var block in message.Content.Where(block => block.Kind == AgentContentKind.ToolUse))
            {
                calls.Add((JsonNode)new JsonObject
                {
                    ["id"] = block.ToolUseId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = block.ToolName,
                        ["arguments"] = block.Json ?? "{}",
                    },
                });
            }
            if (calls.Count > 0) assistant["tool_calls"] = calls;
            result.Add((JsonNode)assistant);
        }

        return result;
    }

    public static JsonArray OpenAiTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = ParseNode(tool.InputSchemaJson, "{\"type\":\"object\"}"),
                },
            });
        }
        return result;
    }

    /// <summary>
    /// Converts the durable Agent history to Responses input items. Responses keeps function calls
    /// and their results as separate top-level items rather than Chat Completions messages.
    /// </summary>
    public static JsonArray ResponsesInput(IReadOnlyList<AgentModelMessage> messages)
    {
        var result = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == AgentMessageRole.User)
            {
                foreach (var block in message.Content.Where(
                    block => block.Kind == AgentContentKind.ToolResult))
                {
                    result.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = block.ToolUseId,
                        ["output"] = block.Text ?? block.Json ?? string.Empty,
                    });
                }

                var text = string.Concat(message.Content
                    .Where(block => block.Kind == AgentContentKind.Text)
                    .Select(block => block.Text));
                var images = message.Content
                    .Where(block => block.Kind == AgentContentKind.Image)
                    .Select(ImageDataUrl)
                    .Where(url => url is not null)
                    .Select(url => url!)
                    .ToList();
                if (text.Length > 0 || images.Count > 0)
                {
                    var content = new JsonArray();
                    if (text.Length > 0)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = text,
                        });
                    }
                    foreach (var image in images)
                    {
                        content.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = image,
                        });
                    }
                    result.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "message",
                        ["role"] = "user",
                        ["content"] = content,
                    });
                }

                continue;
            }

            var assistantText = string.Concat(message.Content
                .Where(block => block.Kind == AgentContentKind.Text)
                .Select(block => block.Text));
            if (assistantText.Length > 0)
            {
                result.Add((JsonNode)new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "output_text", ["text"] = assistantText }),
                });
            }

            foreach (var block in message.Content.Where(
                block => block.Kind == AgentContentKind.ToolUse))
            {
                var functionCall = new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = block.ToolUseId,
                    ["name"] = block.ToolName,
                    ["arguments"] = block.Json ?? "{}",
                };
                if (block.ProviderItemId is { Length: > 0 } itemId)
                {
                    functionCall["id"] = itemId;
                }
                result.Add((JsonNode)functionCall);
            }
        }

        return result;
    }

    private static string? ImageDataUrl(AgentContentBlock block)
    {
        return block.ImageBytes is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(block.MediaType)
            ? $"data:{block.MediaType};base64,{Convert.ToBase64String(block.ImageBytes)}"
            : null;
    }

    public static JsonArray ResponsesTools(IReadOnlyList<AgentToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            result.Add((JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ParseNode(tool.InputSchemaJson, "{\"type\":\"object\"}"),
            });
        }
        return result;
    }

    public static string Error(string body, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? $"HTTP {statusCode}";
                if (error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? $"HTTP {statusCode}";
                }
            }
        }
        catch (JsonException)
        {
            // A proxy may send HTML. Return a bounded body below.
        }

        var text = body.Trim();
        if (text.Length == 0) return $"Upstream request failed ({statusCode}).";
        return text.Length <= 400 ? text : text[..400] + "…";
    }
}
