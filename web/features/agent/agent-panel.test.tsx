import { render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { Translate } from "@/components/providers/i18n-provider";
import type { AgentContentBlock } from "@/features/agent/types";

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => (key: string) => key,
}));

const { AssistantCard } = await import("@/features/agent/agent-panel");

const t: Translate = (key) => key;

describe("AssistantCard block order", () => {
  it("keeps text, tools, thinking, and later tools in their stored order", () => {
    const blocks: AgentContentBlock[] = [
      { kind: "text", text: "text" },
      { kind: "tool_use", toolUseId: "call-1", toolName: "create_node", json: "{}" },
      { kind: "thinking", text: "think" },
      { kind: "tool_use", toolUseId: "call-2", toolName: "update_node", json: "{}" },
    ];
    const toolCalls = new Map([
      ["call-1", {
        id: "call-1",
        name: "create_node",
        input: "{}",
        result: "{\"ok\":true}",
        isError: false,
      }],
      ["call-2", {
        id: "call-2",
        name: "update_node",
        input: "{}",
        result: "{\"ok\":true}",
        isError: false,
      }],
    ]);

    const { container } = render(
      <AssistantCard
        index={0}
        blocks={blocks}
        liveText=""
        liveThinking=""
        live={false}
        toolCalls={toolCalls}
        t={t}
      />,
    );

    expect(
      [...container.querySelectorAll<HTMLElement>("[data-agent-block]")]
        .map((block) => block.dataset.agentBlock),
    ).toEqual(["text", "tool", "thinking", "tool"]);
  });

  it("renders a live tool before a durable assistant message arrives", () => {
    const blocks: AgentContentBlock[] = [
      { kind: "thinking", text: "planning" },
      { kind: "tool_use", toolUseId: "live-1", toolName: "read_canvas", json: "{}" },
    ];
    const toolCalls = new Map([
      ["live-1", {
        id: "live-1",
        name: "read_canvas",
        input: "{}",
        result: null,
        isError: false,
      }],
    ]);

    const { container } = render(
      <AssistantCard
        index={0}
        blocks={[]}
        liveBlocks={blocks}
        live
        toolCalls={toolCalls}
        t={t}
      />,
    );

    expect(container.querySelector('[data-agent-block="tool"]')).not.toBeNull();
    expect(container.textContent).toContain("agent.toolRunning");
  });
});
