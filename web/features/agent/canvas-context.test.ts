import { describe, expect, it } from "vitest";
import { buildCanvasContextText } from "@/features/agent/canvas-context";
import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";

function node(overrides: Partial<CanvasNode> & { id: string }): CanvasNode {
  return {
    type: "text",
    position: { x: 0, y: 0 },
    data: {},
    ...overrides,
  } as CanvasNode;
}

describe("buildCanvasContextText", () => {
  it("returns null for an empty canvas", () => {
    expect(buildCanvasContextText([], [])).toBeNull();
  });

  it("summarizes the selection and the node overview", () => {
    const text = buildCanvasContextText(
      [
        node({ id: "a", type: "text", data: { text: "a cat astronaut\nin space" }, selected: true }),
        node({ id: "b", type: "imageGen", data: { modelId: "flux-pro", count: 4, aspect: "1:1" } }),
      ],
      [{ id: "e1", source: "a", target: "b" } as CanvasEdge],
    );

    expect(text).not.toBeNull();
    expect(text).toContain("selected: *a (text, \"a cat astronaut in space\")");
    expect(text).toContain("canvas: 2 node(s), 1 edge(s)");
    expect(text).toContain("- *a (text,");
    expect(text).toContain("- b (imageGen, model=flux-pro count=4 aspect=1:1)");
  });

  it("marks image nodes without assets and caps long text", () => {
    const longText = "x".repeat(300);
    const text = buildCanvasContextText(
      [
        node({ id: "img", type: "image", data: { prompt: longText } }),
        node({ id: "empty", type: "image", data: { assetUrl: "/api/v1/canvas/assets/x" } }),
      ],
      [],
    );

    expect(text).toContain('"xxxxx');
    expect(text).toContain("…\"");
    expect(text?.length ?? 0).toBeLessThan(300);
    expect(text).toContain("empty (image, [asset attached])");
  });

  it("hard-caps at the server limit with an ellipsis", () => {
    const longId = "n".repeat(100);
    const nodes = Array.from({ length: 200 }, (_, index) =>
      node({ id: `${longId}-${index}`, data: { text: "y".repeat(80) } }),
    );
    const text = buildCanvasContextText(nodes, []);
    expect(text?.length).toBeLessThanOrEqual(24_000);
    expect(text?.endsWith("…")).toBe(true);
  });
});
