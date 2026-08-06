import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";

// Provider lookup is the only thing execution needs from the AI store; the canvas store stays real
// because these tests are about what lands on the graph.
vi.mock("@/features/ai/store", () => ({
  useAiStore: {
    getState: () => ({
      providers: [{ id: "p1", models: [{ id: "m1", modelKey: "some-image-model" }] }],
    }),
  },
}));

const { useCanvasStore } = await import("@/features/canvas/store");
const { cancelRun, retryRun, runImageNode, runVideoNode } = await import("@/features/canvas/execution");

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response;
}

function node(id: string, type: string, data: Record<string, unknown>): CanvasNode {
  return { id, type, position: { x: 0, y: 0 }, data } as CanvasNode;
}

function edge(source: string, target: string): CanvasEdge {
  return { id: `e${source}-${target}`, source, target };
}

/** Runs the image node against the store's current graph, as the node's own button does. */
async function run(nodeId: string) {
  const { nodes, edges } = useCanvasStore.getState();
  await runImageNode(nodeId, nodes, edges);
}

function imageNodes() {
  return useCanvasStore.getState().nodes.filter((candidate) => candidate.type === "image");
}

function executionOf(nodeId: string) {
  const found = useCanvasStore.getState().nodes.find((candidate) => candidate.id === nodeId);
  return (found?.data as { execution?: { status: string; error?: string; runId: string } })
    ?.execution;
}

/** A fetch that never settles on its own, but rejects on abort the way a real one does. */
function abortableFetch() {
  return vi.fn().mockImplementation(
    (_input: string, init?: { signal?: AbortSignal }) =>
      new Promise<Response>((_resolve, reject) => {
        init?.signal?.addEventListener("abort", () =>
          reject(new DOMException("Aborted", "AbortError")),
        );
      }),
  );
}

function assets(...urls: string[]) {
  return { assets: urls.map((url) => ({ url, mediaType: "image/png" })) };
}

beforeEach(() => {
  useCanvasStore.getState().reset();
  vi.restoreAllMocks();
});

describe("cancelRun", () => {
  it("stops the upstream job using the id held by the node it was pressed on", async () => {
    // The job id lives on the output node, which is also where the Stop button is — a video that
    // is only stopped locally keeps rendering upstream, and keeps billing.
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({}));
    vi.stubGlobal("fetch", fetchMock);

    useCanvasStore.getState().load({
      nodes: [
        node("g", "videoGen", {}),
        node("v", "video", { jobId: "job-42", execution: { runId: "r1", status: "running" } }),
      ],
      edges: [edge("g", "v")],
    });

    cancelRun("v");

    expect(fetchMock).toHaveBeenCalledWith(
      "/api/v1/ai/videos/job-42",
      expect.objectContaining({ method: "DELETE" }),
    );
  });

  it("takes the node out of its running state", () => {
    // The poller that would have resolved it just detached, so left alone it spins forever. The
    // node itself stays: a job was accepted upstream, and that is worth showing.
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({})));

    useCanvasStore.getState().load({
      nodes: [
        node("g", "videoGen", {}),
        node("v", "video", { jobId: "job-42", execution: { runId: "r1", status: "running" } }),
      ],
      edges: [edge("g", "v")],
    });

    cancelRun("v");

    expect(executionOf("v")?.status).toBe("cancelled");
    expect(executionOf("g")).toBeUndefined();
  });

  it("stops every node of the run, not just the one that was clicked", () => {
    // One request produces all of a run's images, so stopping one of them stops all of them.
    vi.stubGlobal("fetch", vi.fn());

    useCanvasStore.getState().load({
      nodes: [
        node("g", "imageGen", {}),
        node("a", "image", { assetUrl: "/a", execution: { runId: "r1", status: "running" } }),
        node("b", "image", { assetUrl: "/b", execution: { runId: "r1", status: "running" } }),
        node("c", "image", { assetUrl: "/c", execution: { runId: "r2", status: "running" } }),
      ],
      edges: [edge("g", "a"), edge("g", "b"), edge("g", "c")],
    });

    cancelRun("a");

    expect(executionOf("a")?.status).toBe("cancelled");
    expect(executionOf("b")?.status).toBe("cancelled");
    // A concurrent run from the same config node is untouched.
    expect(executionOf("c")?.status).toBe("running");
  });
});

describe("a run in progress", () => {
  beforeEach(() => {
    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "a cat" }),
        node("g", "imageGen", { providerId: "p1", modelId: "m1", count: 2 }),
      ],
      edges: [edge("t", "g")],
    });
  });

  it("puts its output nodes on the canvas before the request resolves", async () => {
    // Generation takes tens of seconds. Spawning only on success left the canvas unchanged for
    // that whole stretch, so the click read as having done nothing at all.
    let release: (value: Response) => void = () => {};
    const pending = new Promise<Response>((resolve) => {
      release = resolve;
    });
    vi.stubGlobal("fetch", vi.fn().mockReturnValue(pending));

    const finished = run("g");

    expect(imageNodes()).toHaveLength(2);
    expect(imageNodes().map((candidate) => executionOf(candidate.id)?.status)).toEqual([
      "running",
      "running",
    ]);

    release(jsonResponse(assets("/api/v1/canvas/assets/1", "/api/v1/canvas/assets/2")));
    await finished;

    // Filled in place, not replaced: the result lands in the box the user was watching.
    expect(imageNodes()).toHaveLength(2);
    expect(imageNodes().map((candidate) => (candidate.data as { assetUrl?: string }).assetUrl)).toEqual([
      "/api/v1/canvas/assets/1",
      "/api/v1/canvas/assets/2",
    ]);
    expect(imageNodes().every((candidate) => !executionOf(candidate.id))).toBe(true);
  });

  it("leaves the run state off the config node", async () => {
    // The config node owning the run is what used to make a second Generate impossible: the new
    // run took over the status field the first one was checked against.
    vi.stubGlobal("fetch", vi.fn().mockReturnValue(new Promise<Response>(() => {})));

    void run("g");

    expect(executionOf("g")).toBeUndefined();
  });

  it("reports a failed request on the node that was waiting for the image", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ error: "nope" }, 500)));

    await run("g");

    // One request, one report: the first box keeps the error and the spares go, rather than
    // lining up two identical red boxes. The config node stays clean — with two runs going it
    // could not say which of them failed anyway.
    expect(imageNodes()).toHaveLength(1);
    expect(executionOf(imageNodes()[0].id)?.status).toBe("failed");
    expect(executionOf(imageNodes()[0].id)?.error).toBe("nope");
    expect(executionOf("g")).toBeUndefined();
  });

  it("discards the spares when fewer images come back than were asked for", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(assets("/api/v1/canvas/assets/1"))));

    await run("g");

    expect(imageNodes()).toHaveLength(1);
    expect((imageNodes()[0].data as { assetUrl?: string }).assetUrl).toBe(
      "/api/v1/canvas/assets/1",
    );
  });

  it("takes its placeholders with it when stopped", async () => {
    vi.stubGlobal("fetch", abortableFetch());

    const finished = run("g");
    expect(imageNodes()).toHaveLength(2);

    cancelRun(imageNodes()[0].id);
    await finished;

    // Stopping a run that produced nothing should leave the canvas as it was, rather than
    // littering it with empty boxes wearing a "stopped" banner.
    expect(imageNodes()).toHaveLength(0);
  });

  it("reports nothing connected upstream on the config node", async () => {
    useCanvasStore.getState().load({
      nodes: [node("g", "imageGen", { providerId: "p1", modelId: "m1" })],
      edges: [],
    });

    await run("g");

    // No node was created, so the config node is the only place this can be said.
    expect(imageNodes()).toHaveLength(0);
    expect(executionOf("g")?.status).toBe("failed");
  });

  it("clears a previous failure when it starts", async () => {
    // Only reachable through a pre-flight failure now, which is the one kind that has no node of
    // its own to sit on.
    useCanvasStore.getState().load({
      nodes: [node("g", "imageGen", { providerId: "p1", modelId: "m1" })],
      edges: [],
    });
    await run("g");
    expect(executionOf("g")?.status).toBe("failed");

    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "a cat" }),
        node("g", "imageGen", { providerId: "p1", modelId: "m1", execution: executionOf("g") }),
      ],
      edges: [edge("t", "g")],
    });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(assets("/one"))));
    await run("g");

    // A fresh attempt should not start out wearing an old error.
    expect(executionOf("g")).toBeUndefined();
  });
});

describe("the image request body", () => {
  /** Pulls the posted JSON back out of the fetch stub. */
  function bodyOf(fetchMock: ReturnType<typeof vi.fn>) {
    const call = fetchMock.mock.calls.find(([url]) => url === "/api/v1/ai/images");
    return JSON.parse((call?.[1] as { body: string }).body);
  }

  function graph(config: Record<string, unknown>) {
    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "a cat" }),
        node("g", "imageGen", { providerId: "p1", modelId: "m1", ...config }),
      ],
      edges: [edge("t", "g")],
    });
  }

  it("sends a marked inpaint mask separately from reference images", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(assets("/one")));
    vi.stubGlobal("fetch", fetchMock);
    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "repair the painted area" }),
        node("source", "image", { assetUrl: "/source.png" }),
        node("mask", "image", { assetUrl: "/mask.png", role: "inpaint-mask" }),
        node("g", "imageGen", { providerId: "p1", modelId: "m1" }),
      ],
      edges: [edge("t", "g"), edge("source", "g"), edge("mask", "g")],
    });

    await run("g");

    expect(bodyOf(fetchMock)).toMatchObject({
      maskUrl: "/mask.png",
      images: ["/source.png"],
    });
    expect(bodyOf(fetchMock).images).not.toContain("/mask.png");
  });

  it("asks for a URL and a PNG when the node has been left alone", async () => {
    // These mirror the server's own fallbacks. A node that has never had the chips touched must
    // still send them, because the chips show those values as selected.
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(assets("/one")));
    vi.stubGlobal("fetch", fetchMock);
    graph({});

    await run("g");

    expect(bodyOf(fetchMock)).toMatchObject({ outputFormat: "png", responseFormat: "url" });
  });

  it("sends the chosen format and response format", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(assets("/one")));
    vi.stubGlobal("fetch", fetchMock);
    graph({ outputFormat: "jpeg", responseFormat: "base64", quality: "high" });

    await run("g");

    expect(bodyOf(fetchMock)).toMatchObject({
      outputFormat: "jpeg",
      responseFormat: "base64",
      quality: "high",
    });
  });
});

describe("the video duration request", () => {
  function graph(duration?: number, seedanceInputMode?: string) {
    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "a cat" }),
        node("g", "videoGen", {
          providerId: "p1",
          modelId: "m1",
          duration,
          seedanceInputMode,
        }),
      ],
      edges: [edge("t", "g")],
    });
  }

  function videoNodes() {
    return useCanvasStore.getState().nodes.filter((candidate) => candidate.type === "video");
  }

  function postedBody(fetchMock: ReturnType<typeof vi.fn>) {
    const call = fetchMock.mock.calls.find(([url]) => url === "/api/v1/ai/videos");
    return JSON.parse((call?.[1] as { body: string }).body);
  }

  async function startAndCancel(fetchMock: ReturnType<typeof vi.fn>) {
    const { nodes, edges } = useCanvasStore.getState();
    const running = runVideoNode("g", nodes, edges);

    const [output] = videoNodes();
    cancelRun(output.id);
    await running;

    return postedBody(fetchMock);
  }

  it("sends the five-second default when duration is absent", async () => {
    const fetchMock = abortableFetch();
    vi.stubGlobal("fetch", fetchMock);
    graph();

    expect((await startAndCancel(fetchMock)).duration).toBe(5);
  });

  it("sends an arbitrary whole number in range", async () => {
    const fetchMock = abortableFetch();
    vi.stubGlobal("fetch", fetchMock);
    graph(37);

    expect((await startAndCancel(fetchMock)).duration).toBe(37);
  });

  it("defaults to reference media and sends the selected first/last mode", async () => {
    const fetchMock = abortableFetch();
    vi.stubGlobal("fetch", fetchMock);
    graph(5, "first-last");

    expect((await startAndCancel(fetchMock)).inputMode).toBe("first-last");
  });

  it.each([0, 61, 3.5])("rejects invalid stored duration %s before calling the API", async (duration) => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    graph(duration);

    const { nodes, edges } = useCanvasStore.getState();
    await runVideoNode("g", nodes, edges);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(videoNodes()).toHaveLength(0);
    expect(executionOf("g")?.status).toBe("failed");
    expect(executionOf("g")?.error).toContain("1 to 60");
  });
});

describe("retrying from a failed node", () => {
  beforeEach(() => {
    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "a cat" }),
        node("g", "imageGen", { providerId: "p1", modelId: "m1" }),
      ],
      edges: [edge("t", "g")],
    });
  });

  it("replaces the failed node with a fresh run of the node that fed it", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({ error: "nope" }, 500)));
    await run("g");

    const failed = imageNodes()[0].id;
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(assets("/second-time"))));

    const { nodes, edges } = useCanvasStore.getState();
    retryRun(failed, nodes, edges);

    // retryRun kicks the new run off without awaiting it, the way the button does.
    await vi.waitFor(() =>
      expect((imageNodes()[0]?.data as { assetUrl?: string }).assetUrl).toBe("/second-time"),
    );

    // The red box is gone rather than accumulating next to its replacement, and the retry went
    // through the config node, so it picks up any setting changed since.
    expect(imageNodes()).toHaveLength(1);
    expect(imageNodes().map((candidate) => candidate.id)).not.toContain(failed);
  });

  it("does nothing for a node no generation node feeds", () => {
    // An uploaded image has no run behind it, so there is nothing to retry.
    useCanvasStore.getState().load({
      nodes: [node("i", "image", { assetUrl: "/uploaded" })],
      edges: [],
    });

    const { nodes, edges } = useCanvasStore.getState();
    retryRun("i", nodes, edges);

    expect(imageNodes()).toHaveLength(1);
  });
});

describe("generating again while a run is still going", () => {
  beforeEach(() => {
    useCanvasStore.getState().load({
      nodes: [
        node("t", "text", { text: "a cat" }),
        node("g", "imageGen", { providerId: "p1", modelId: "m1" }),
      ],
      edges: [edge("t", "g")],
    });
  });

  it("keeps both runs, and both results", async () => {
    // The point of the whole per-run model: a second press adds to the canvas rather than
    // replacing what the first press is still working on.
    const releases: Array<(value: Response) => void> = [];
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation(
        () => new Promise<Response>((resolve) => releases.push(resolve)),
      ),
    );

    const first = run("g");
    const second = run("g");

    expect(imageNodes()).toHaveLength(2);

    // Resolved out of order, because a slow first run must not be discarded by a fast second.
    releases[1](jsonResponse(assets("/second")));
    releases[0](jsonResponse(assets("/first")));
    await Promise.all([first, second]);

    expect(imageNodes().map((candidate) => (candidate.data as { assetUrl?: string }).assetUrl)).toEqual([
      "/first",
      "/second",
    ]);
  });

  it("places the second run below the first rather than on top of it", async () => {
    // `spawnOutput` positions relative to the config node, so without an offset the two runs
    // land on identical coordinates and one hides the other.
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(assets("/one"))));

    await run("g");
    await run("g");

    const [first, second] = imageNodes();
    expect(second.position.y).toBeGreaterThan(first.position.y);
    expect(second.position.x).toBe(first.position.x);
  });

  it("stops only the run whose node was clicked", async () => {
    vi.stubGlobal("fetch", abortableFetch());

    const first = run("g");
    // Deliberately not awaited: this run is still going when the test ends, which is the point.
    void run("g");
    const [a, b] = imageNodes().map((candidate) => candidate.id);

    cancelRun(a);
    await first;

    expect(imageNodes().map((candidate) => candidate.id)).toEqual([b]);
    expect(executionOf(b)?.status).toBe("running");
  });
});
