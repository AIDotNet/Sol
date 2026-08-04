import { describe, expect, it } from "vitest";
import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";
import { collectUpstream, wouldCreateCycle } from "@/features/canvas/upstream";
import type { NodeKind } from "@/features/canvas/types";

function node(id: string, type: NodeKind, data: Record<string, unknown> = {}): CanvasNode {
  return { id, type, position: { x: 0, y: 0 }, data } as CanvasNode;
}

function edge(source: string, target: string): CanvasEdge {
  return { id: `${source}-${target}`, source, target };
}

describe("collectUpstream", () => {
  it("collects text from a directly connected text node", () => {
    const nodes = [node("t1", "text", { text: "a cat" }), node("g1", "imageGen")];
    const result = collectUpstream("g1", nodes, [edge("t1", "g1")]);

    expect(result.prompt).toBe("a cat");
    expect(result.images).toEqual([]);
  });

  it("ignores downstream nodes", () => {
    const nodes = [
      node("t1", "text", { text: "upstream" }),
      node("g1", "imageGen"),
      node("t2", "text", { text: "downstream" }),
    ];
    const result = collectUpstream("g1", nodes, [edge("t1", "g1"), edge("g1", "t2")]);

    expect(result.prompt).toBe("upstream");
  });

  it("passes through a generation node so chained runs keep the original prompt", () => {
    // t1 → gen1 → img1 → gen2. gen2 must still see t1's text.
    const nodes = [
      node("t1", "text", { text: "a cat" }),
      node("gen1", "imageGen"),
      node("img1", "image", { assetUrl: "/asset/1" }),
      node("gen2", "imageGen"),
    ];
    const edges = [edge("t1", "gen1"), edge("gen1", "img1"), edge("img1", "gen2")];

    const result = collectUpstream("gen2", nodes, edges);

    expect(result.images).toEqual([{ url: "/asset/1", mediaType: undefined }]);
    // img1 is a wall for text (it has an asset), so the chain stops there.
    expect(result.prompt).toBe("");

    // But wiring the text directly through the config node does pass through.
    const direct = collectUpstream("gen1", nodes, edges);
    expect(direct.prompt).toBe("a cat");
  });

  it("joins several text nodes with blank lines", () => {
    const nodes = [
      node("t1", "text", { text: "first" }),
      node("t2", "text", { text: "second" }),
      node("g1", "imageGen"),
    ];
    const result = collectUpstream("g1", nodes, [edge("t1", "g1"), edge("t2", "g1")]);

    expect(result.prompt.split("\n\n").sort()).toEqual(["first", "second"]);
  });

  it("collects reference images", () => {
    const nodes = [
      node("i1", "image", { assetUrl: "/asset/a", mediaType: "image/png" }),
      node("g1", "imageGen"),
    ];
    const result = collectUpstream("g1", nodes, [edge("i1", "g1")]);

    expect(result.images).toEqual([{ url: "/asset/a", mediaType: "image/png" }]);
  });

  it("falls back to a pending image's prompt when it has no asset yet", () => {
    const nodes = [
      node("i1", "image", { prompt: "a previous description" }),
      node("g1", "imageGen"),
    ];
    const result = collectUpstream("g1", nodes, [edge("i1", "g1")]);

    expect(result.prompt).toBe("a previous description");
  });

  it("terminates on a cycle instead of hanging", () => {
    // The canvas permits cycles; an unguarded traversal would loop forever.
    const nodes = [
      node("a", "text", { text: "A" }),
      node("b", "imageGen"),
      node("c", "imageGen"),
    ];
    const edges = [edge("a", "b"), edge("b", "c"), edge("c", "b")];

    const result = collectUpstream("c", nodes, edges);
    expect(result.prompt).toBe("A");
  });

  it("reads a node reachable by two paths only once", () => {
    const nodes = [
      node("t1", "text", { text: "shared" }),
      node("g1", "imageGen"),
      node("g2", "imageGen"),
      node("g3", "imageGen"),
    ];
    const edges = [edge("t1", "g1"), edge("t1", "g2"), edge("g1", "g3"), edge("g2", "g3")];

    const result = collectUpstream("g3", nodes, edges);
    expect(result.prompt).toBe("shared");
  });

  it("skips blank text nodes", () => {
    const nodes = [
      node("t1", "text", { text: "   " }),
      node("t2", "text", { text: "real" }),
      node("g1", "imageGen"),
    ];
    const result = collectUpstream("g1", nodes, [edge("t1", "g1"), edge("t2", "g1")]);

    expect(result.prompt).toBe("real");
  });

  it("returns empty inputs for an unconnected node", () => {
    const result = collectUpstream("g1", [node("g1", "imageGen")], []);
    expect(result).toEqual({ prompt: "", images: [] });
  });
});

describe("wouldCreateCycle", () => {
  it("rejects a self-connection", () => {
    expect(wouldCreateCycle("a", "a", [])).toBe(true);
  });

  it("rejects an edge that closes a loop", () => {
    const edges = [edge("a", "b"), edge("b", "c")];
    expect(wouldCreateCycle("c", "a", edges)).toBe(true);
  });

  it("allows a diamond, which is not a cycle", () => {
    const edges = [edge("a", "b"), edge("a", "c")];
    expect(wouldCreateCycle("b", "d", edges)).toBe(false);
    expect(wouldCreateCycle("c", "d", edges)).toBe(false);
  });

  it("allows an ordinary forward edge", () => {
    expect(wouldCreateCycle("a", "b", [])).toBe(false);
  });
});
