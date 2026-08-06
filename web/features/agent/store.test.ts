import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentEventEnvelope } from "@/features/agent/types";

vi.mock("@/features/agent/api", () => ({
  listMessages: vi.fn().mockResolvedValue([]),
  listEvents: vi.fn().mockResolvedValue([]),
  findSession: vi.fn(),
  findActiveRun: vi.fn(),
  getRun: vi.fn(),
  createSession: vi.fn(),
  createRun: vi.fn(),
  clearMessages: vi.fn(),
  cancelRun: vi.fn(),
}));

vi.mock("@/features/agent/realtime", () => ({
  getAgentConnectionId: vi.fn().mockResolvedValue(null),
}));

const { useAgentStore } = await import("@/features/agent/store");

const run = {
  id: "run-1",
  sessionId: "session-1",
  canvasId: "canvas-1",
  providerId: "provider-1",
  modelKey: "model-1",
  status: "running" as const,
  iteration: 0,
  lastSequence: 0,
  error: null,
  createdAt: "2026-01-01T00:00:00Z",
  updatedAt: "2026-01-01T00:00:00Z",
};

function event(
  sequence: number,
  type: string,
  json: object = {},
  text: string | null = null,
): AgentEventEnvelope {
  return {
    runId: run.id,
    sequence,
    type,
    text,
    json: JSON.stringify(json),
    error: null,
    sentAt: "2026-01-01T00:00:00Z",
  };
}

beforeEach(() => {
  useAgentStore.setState({
    canvasId: run.canvasId,
    messages: [],
    run,
    streamText: "",
    thinkingText: "",
    liveBlocks: [],
    liveToolCalls: [],
    lastSequence: 0,
    loading: false,
    sending: false,
    clearing: false,
    error: null,
  });
});

describe("live Agent events", () => {
  it("renders a tool before its result and updates it in place", () => {
    const receive = useAgentStore.getState().receiveEvent;

    receive(event(1, "tool.started", {
      toolUseId: "tool-1",
      toolName: "read_canvas",
      input: { nodeId: "node-1" },
    }));

    expect(useAgentStore.getState().liveBlocks).toEqual([
      {
        kind: "tool_use",
        toolUseId: "tool-1",
        toolName: "read_canvas",
        json: '{"nodeId":"node-1"}',
      },
    ]);
    expect(useAgentStore.getState().liveToolCalls[0]).toMatchObject({
      id: "tool-1",
      result: null,
    });

    receive(event(2, "tool.completed", {
      toolUseId: "tool-1",
      toolName: "read_canvas",
      isError: false,
      result: { status: "ok" },
    }));

    expect(useAgentStore.getState().liveBlocks).toHaveLength(1);
    expect(useAgentStore.getState().liveToolCalls[0]).toMatchObject({
      id: "tool-1",
      result: '{"status":"ok"}',
      isError: false,
    });
  });

  it("batches token updates without losing text-thinking-text order", async () => {
    const receive = useAgentStore.getState().receiveEvent;
    receive(event(0, "text.delta", {}, "before"));
    receive(event(0, "thinking.delta", {}, "think"));
    receive(event(0, "text.delta", {}, "after"));

    await new Promise((resolve) => setTimeout(resolve, 25));

    expect(useAgentStore.getState().liveBlocks).toEqual([
      { kind: "text", text: "before" },
      { kind: "thinking", text: "think" },
      { kind: "text", text: "after" },
    ]);
  });
});
