import { describe, expect, it } from "vitest";
import type { Edge } from "@xyflow/react";
import { autoLayoutNodes } from "@/features/canvas/layout";
import type { CanvasNode } from "@/features/canvas/store";

function node(
  id: string,
  type: string,
  x: number,
  y: number,
  width = 200,
  height = 120,
): CanvasNode {
  return {
    id,
    type,
    position: { x, y },
    width,
    height,
    data: {},
  } as CanvasNode;
}

function edge(source: string, target: string): Edge {
  return { id: `${source}-${target}`, source, target };
}

describe("canvas auto layout", () => {
  it("places connected nodes from upstream to downstream without overlap", () => {
    const nodes = [
      node("output", "image", 900, 100, 320, 320),
      node("prompt", "text", 400, 500),
      node("generator", "imageGen", 100, 200, 300, 340),
      node("alternate", "text", 600, 50),
    ];

    const arranged = autoLayoutNodes(nodes, [
      edge("prompt", "generator"),
      edge("generator", "output"),
    ]);
    const byId = new Map(arranged.map((item) => [item.id, item]));

    expect(byId.get("prompt")!.position.x).toBeLessThan(byId.get("generator")!.position.x);
    expect(byId.get("generator")!.position.x).toBeLessThan(byId.get("output")!.position.x);

    for (let first = 0; first < arranged.length; first += 1) {
      for (let second = first + 1; second < arranged.length; second += 1) {
        const a = arranged[first];
        const b = arranged[second];
        const aWidth = a.width ?? 0;
        const aHeight = a.height ?? 0;
        const bWidth = b.width ?? 0;
        const bHeight = b.height ?? 0;
        const separated =
          a.position.x + aWidth <= b.position.x ||
          b.position.x + bWidth <= a.position.x ||
          a.position.y + aHeight <= b.position.y ||
          b.position.y + bHeight <= a.position.y;
        expect(separated).toBe(true);
      }
    }

    expect(arranged[0].data).toBe(nodes[0].data);
  });

  it("uses a compact grid when the canvas has no connections", () => {
    const nodes = [
      node("a", "text", 900, 600),
      node("b", "image", 20, 20, 320, 320),
      node("c", "video", 300, 300, 360, 240),
      node("d", "text", 500, 100),
    ];

    const arranged = autoLayoutNodes(nodes, []);
    const positions = arranged.map((item) => item.position);

    expect(new Set(positions.map((position) => position.x)).size).toBeGreaterThan(1);
    expect(positions.every((position) => Number.isFinite(position.x) && Number.isFinite(position.y))).toBe(
      true,
    );
  });

  it("does not get stuck on an imported cycle", () => {
    const arranged = autoLayoutNodes(
      [node("a", "text", 0, 0), node("b", "text", 10, 10), node("c", "text", 20, 20)],
      [edge("a", "b"), edge("b", "a"), edge("b", "c")],
    );

    expect(arranged).toHaveLength(3);
    expect(arranged.every((item) => Number.isFinite(item.position.x))).toBe(true);
  });
});
