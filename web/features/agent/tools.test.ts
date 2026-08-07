import { beforeEach, describe, expect, it } from "vitest";
import { executeCanvasTool } from "@/features/agent/tools";
import { useCanvasStore } from "@/features/canvas/store";
import type { AgentToolCallEnvelope } from "@/features/agent/types";

function call(toolName: string, input: Record<string, unknown> = {}): AgentToolCallEnvelope {
  return {
    runId: "run-1",
    canvasId: "canvas-1",
    toolUseId: `tool-${toolName}`,
    toolName,
    inputJson: JSON.stringify(input),
    timeoutMilliseconds: 30_000,
  };
}

function result(json: string): Record<string, unknown> {
  return JSON.parse(json) as Record<string, unknown>;
}

beforeEach(() => {
  useCanvasStore.getState().reset();
});

describe("Agent canvas tools", () => {
  it("creates each supported node kind with store defaults", async () => {
    for (const kind of ["text", "image", "video", "imageGen", "videoGen"] as const) {
      const executed = await executeCanvasTool(call("create_node", { kind }));
      expect(executed.isError).toBe(false);
      expect(typeof result(executed.json).nodeId).toBe("string");
    }

    expect(useCanvasStore.getState().nodes.map((node) => node.type)).toEqual([
      "text",
      "image",
      "video",
      "imageGen",
      "videoGen",
    ]);
  });

  it("updates only fields allowed by the node kind and preserves undo", async () => {
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "before" });

    const update = await executeCanvasTool(
      call("update_node", { nodeId: id, data: { text: "after" } }),
    );
    expect(update.isError).toBe(false);
    expect(useCanvasStore.getState().nodes[0].data.text).toBe("after");

    useCanvasStore.getState().undo();
    expect(useCanvasStore.getState().nodes[0].data.text).toBe("before");

    const invalid = await executeCanvasTool(
      call("update_node", { nodeId: id, data: { assetUrl: "/asset.png" } }),
    );
    expect(invalid.isError).toBe(true);
    expect(result(invalid.json).error).toBe("field_not_allowed_for_text:assetUrl");
  });

  it("rejects cycles and duplicate edges before calling the low-level store action", async () => {
    const state = useCanvasStore.getState();
    const first = state.addNode("text", { x: 0, y: 0 }, { text: "one" });
    const second = state.addNode("text", { x: 300, y: 0 }, { text: "two" });

    expect(
      (await executeCanvasTool(call("connect_nodes", { sourceId: first, targetId: second }))).isError,
    ).toBe(false);
    expect(
      (await executeCanvasTool(call("connect_nodes", { sourceId: first, targetId: second }))).isError,
    ).toBe(true);
    expect(
      (await executeCanvasTool(call("connect_nodes", { sourceId: second, targetId: first }))).isError,
    ).toBe(true);
    expect(useCanvasStore.getState().edges).toHaveLength(1);
  });

  it("replaces selection exactly without making it durable history", async () => {
    const state = useCanvasStore.getState();
    const first = state.addNode("text", { x: 0, y: 0 }, { text: "one" });
    const second = state.addNode("text", { x: 300, y: 0 }, { text: "two" });
    const revision = useCanvasStore.getState().revision;
    const history = useCanvasStore.getState().past.length;

    await executeCanvasTool(call("select_nodes", { nodeIds: [second] }));

    const nodes = useCanvasStore.getState().nodes;
    expect(nodes.find((node) => node.id === first)?.selected).toBe(false);
    expect(nodes.find((node) => node.id === second)?.selected).toBe(true);
    expect(useCanvasStore.getState().revision).toBe(revision);
    expect(useCanvasStore.getState().past).toHaveLength(history);
  });

  it("reads a compact live graph and execution status", async () => {
    const id = useCanvasStore.getState().addNode("image", { x: 4, y: 8 }, {
      assetUrl: "/api/v1/canvas/assets/image",
      execution: { runId: "image-run", status: "succeeded" },
    });

    const read = result((await executeCanvasTool(call("read_canvas"))).json);
    expect(read.nodes).toEqual(
      expect.arrayContaining([expect.objectContaining({ id, type: "image" })]),
    );

    const status = result(
      (await executeCanvasTool(call("get_node_status", { nodeIds: [id, "missing"] }))).json,
    );
    expect(status.nodes).toEqual([
      expect.objectContaining({ nodeId: id, exists: true }),
      { nodeId: "missing", exists: false },
    ]);
  });

  it("uses the graph-aware layout for the auto_layout canvas action", async () => {
    const state = useCanvasStore.getState();
    const source = state.addNode("text", { x: 500, y: 100 });
    const target = state.addNode("image", { x: 0, y: 400 });
    await executeCanvasTool(call("connect_nodes", { sourceId: source, targetId: target }));

    const arranged = await executeCanvasTool(
      call("manage_canvas", { action: "auto_layout" }),
    );

    expect(arranged.isError).toBe(false);
    const nodes = useCanvasStore.getState().nodes;
    expect(nodes.find((node) => node.id === source)!.position.x).toBeLessThan(
      nodes.find((node) => node.id === target)!.position.x,
    );
    expect(result(arranged.json)).toMatchObject({ action: "auto_layout", nodeCount: 2 });
  });
});
