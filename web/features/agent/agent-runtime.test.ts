import { beforeEach, describe, expect, it, vi } from "vitest";
import type { AgentToolCallEnvelope } from "@/features/agent/types";

const mocks = vi.hoisted(() => ({
  executeCanvasTool: vi.fn(),
  submitAgentToolResult: vi.fn().mockResolvedValue(undefined),
}));

vi.mock("@/features/agent/tools", () => ({
  AGENT_CANVAS_TOOL_NAMES: [],
  executeCanvasTool: mocks.executeCanvasTool,
}));

vi.mock("@/features/agent/realtime", () => ({
  attachAgentRun: vi.fn(),
  detachAgentRun: vi.fn(),
  submitAgentApproval: vi.fn(),
  submitAgentToolResult: mocks.submitAgentToolResult,
  subscribeAgentApprovals: vi.fn(() => () => undefined),
  subscribeAgentEvents: vi.fn(() => () => undefined),
  subscribeAgentReconnect: vi.fn(() => () => undefined),
  subscribeAgentToolCalls: vi.fn(() => () => undefined),
}));

import { handleAgentToolCall } from "@/features/agent/agent-runtime";

function call(): AgentToolCallEnvelope {
  return {
    runId: "run-1",
    canvasId: "canvas-1",
    toolUseId: "tool-1",
    toolName: "run_node",
    inputJson: JSON.stringify({ nodeId: "video-1" }),
    timeoutMilliseconds: 30_000,
  };
}

beforeEach(() => {
  window.localStorage.clear();
  mocks.executeCanvasTool.mockReset();
  mocks.submitAgentToolResult.mockReset().mockResolvedValue(undefined);
});

describe("Agent canvas tool runtime", () => {
  it("ignores a duplicate tool call while the original execution is pending", async () => {
    let resolveExecution: ((value: { json: string; isError: boolean }) => void) | undefined;
    const execution = new Promise<{ json: string; isError: boolean }>((resolve) => {
      resolveExecution = resolve;
    });
    mocks.executeCanvasTool.mockReturnValueOnce(execution);

    const first = handleAgentToolCall(call(), "canvas-1");
    const duplicate = handleAgentToolCall(call(), "canvas-1");

    expect(mocks.executeCanvasTool).toHaveBeenCalledTimes(1);
    resolveExecution?.({ json: '{"status":"ok"}', isError: false });
    await Promise.all([first, duplicate]);

    expect(mocks.submitAgentToolResult).toHaveBeenCalledTimes(1);
  });
});
