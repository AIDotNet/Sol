import { describe, expect, it } from "vitest";
import { CANVAS_TEMPLATES, instantiateCanvasTemplate } from "@/features/canvas/templates";

describe("instantiateCanvasTemplate", () => {
  it("creates a connected text-to-image starter graph", () => {
    const template = CANVAS_TEMPLATES.find((candidate) => candidate.id === "text-to-image")!;
    let sequence = 0;
    const graph = instantiateCanvasTemplate(template, () => `node-${sequence++}`);

    expect(graph.nodes.map((node) => node.type)).toEqual(["text", "imageGen"]);
    expect(graph.edges).toHaveLength(1);
    expect(graph.edges[0]).toMatchObject({
      source: graph.nodes[0].id,
      target: graph.nodes[1].id,
      type: "default",
    });
  });
});