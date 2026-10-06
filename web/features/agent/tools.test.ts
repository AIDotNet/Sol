import { beforeEach, describe, expect, it, vi } from "vitest";
import { executeCanvasTool } from "@/features/agent/tools";
import { useCanvasStore } from "@/features/canvas/store";
import type { AgentToolCallEnvelope } from "@/features/agent/types";

// Generation resolves the model key through the AI store; the mock keeps the tests offline.
vi.mock("@/features/ai/store", () => ({
  useAiStore: {
    getState: () => ({
      providers: [
        { id: "p1", enabled: true, models: [{ id: "m1", enabled: true, modelKey: "mk-1" }] },
      ],
    }),
  },
}));

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

  it("connects several node pairs in one batch, reporting per-connection errors", async () => {
    const state = useCanvasStore.getState();
    const prompt = state.addNode("text", { x: 0, y: 0 }, { text: "prompt" });
    const reference = state.addNode("image", { x: 0, y: 300 });
    const target = state.addNode("imageGen", { x: 400, y: 0 });
    const missing = "node-that-is-gone";

    const executed = await executeCanvasTool(
      call("connect_nodes", {
        connections: [
          { sourceId: prompt, targetId: target },
          { sourceId: reference, targetId: target },
          { sourceId: missing, targetId: target },
          { sourceId: prompt, targetId: target },
        ],
      }),
    );

    expect(executed.isError).toBe(false);
    const payload = result(executed.json);
    expect(payload.connections).toEqual([
      expect.objectContaining({ sourceId: prompt, targetId: target }),
      expect.objectContaining({ sourceId: reference, targetId: target }),
    ]);
    expect((payload.connections as Array<{ edgeId: string }>).map((entry) => entry.edgeId))
      .not.toContain(null);
    expect(payload.errors).toEqual([
      { sourceId: missing, targetId: target, error: "node_not_found" },
      { sourceId: prompt, targetId: target, error: "edge_already_exists" },
    ]);
    expect(useCanvasStore.getState().edges).toHaveLength(2);
    // One undo step rolls the whole batch back together (3 nodes created before it).
    expect(useCanvasStore.getState().past).toHaveLength(4);
    useCanvasStore.getState().undo();
    expect(useCanvasStore.getState().edges).toHaveLength(0);
  });

  it("fails the whole batch connect only when every connection is invalid", async () => {
    const state = useCanvasStore.getState();
    const only = state.addNode("text", { x: 0, y: 0 });

    const executed = await executeCanvasTool(
      call("connect_nodes", {
        connections: [{ sourceId: only, targetId: only }],
      }),
    );
    expect(executed.isError).toBe(true);
    expect(result(executed.json).error).toBe("self_connection");
    expect(useCanvasStore.getState().edges).toHaveLength(0);
  });

  it("runs independent generation nodes concurrently and groups outputs per node", async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({ assets: [{ url: "/api/v1/canvas/assets/out", mediaType: "image/png" }] }),
    } as Response);
    vi.stubGlobal("fetch", fetchMock);

    const state = useCanvasStore.getState();
    const prompt = state.addNode("text", { x: 0, y: 0 }, { text: "a lighthouse at dusk" });
    const genA = state.addNode("imageGen", { x: 400, y: 0 }, { providerId: "p1", modelId: "m1" });
    const genB = state.addNode("imageGen", { x: 400, y: 400 }, { providerId: "p1", modelId: "m1" });
    await executeCanvasTool(call("connect_nodes", {
      connections: [
        { sourceId: prompt, targetId: genA },
        { sourceId: prompt, targetId: genB },
      ],
    }));

    const executed = await executeCanvasTool(call("run_nodes", { nodeIds: [genA, genB] }));

    expect(executed.isError).toBe(false);
    const payload = result(executed.json);
    const runs = payload.results as Array<{
      nodeId: string;
      ok: boolean;
      outputs: Array<{ assetUrl: string | null }>;
    }>;
    expect(runs.map((entry) => entry.nodeId).sort()).toEqual([genA, genB].sort());
    for (const entry of runs) {
      expect(entry.ok).toBe(true);
      expect(entry.outputs).toHaveLength(1);
      expect(entry.outputs[0].assetUrl).toBe("/api/v1/canvas/assets/out");
    }
    // Two independent generations, two upstream requests.
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("rejects a batched generation node with no provider or no upstream input", async () => {
    const state = useCanvasStore.getState();
    const unconfigured = state.addNode("imageGen", { x: 400, y: 0 });
    // Has a model but nothing wired into it: runImageNode would fail silently, so the tool must
    // surface the missing input instead.
    const disconnected = state.addNode("imageGen", { x: 400, y: 400 }, {
      providerId: "p1",
      modelId: "m1",
    });

    const missingModel = await executeCanvasTool(
      call("run_nodes", { nodeIds: [unconfigured] }),
    );
    expect(missingModel.isError).toBe(true);
    expect(result(missingModel.json).error).toBe(`generation_node_not_configured:${unconfigured}`);

    const missingInput = await executeCanvasTool(
      call("run_nodes", { nodeIds: [disconnected] }),
    );
    expect(missingInput.isError).toBe(true);
    expect(result(missingInput.json).error).toBe(`generation_node_missing_input:${disconnected}`);
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
