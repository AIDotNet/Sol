import type { TranslationKey } from "@/components/providers/i18n-provider";
import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";
import { NODE_DEFAULT_SIZE, type CanvasNodeData, type NodeKind } from "@/features/canvas/types";

/** Identifiers for the built-in, connected workflow examples. */
export type CanvasTemplateId = "text-to-image" | "reference-to-image" | "image-to-video";

export interface CanvasTemplateNodeSpec {
  /** Stable only inside the template; the store replaces it with a real canvas node id. */
  key: string;
  kind: NodeKind;
  position: { x: number; y: number };
  data?: Partial<CanvasNodeData>;
}

export interface CanvasTemplateEdgeSpec {
  source: string;
  target: string;
}

export interface CanvasTemplateDefinition {
  id: CanvasTemplateId;
  nameKey: TranslationKey;
  descriptionKey: TranslationKey;
  nodes: readonly CanvasTemplateNodeSpec[];
  edges: readonly CanvasTemplateEdgeSpec[];
}

/** Turns a template definition into a graph that can be stored as a canvas document. */
export function instantiateCanvasTemplate(
  template: CanvasTemplateDefinition,
  createId: () => string,
): { nodes: CanvasNode[]; edges: CanvasEdge[] } {
  const ids = new Map(template.nodes.map((node) => [node.key, createId()]));

  return {
    nodes: template.nodes.map((node) => {
      const size = NODE_DEFAULT_SIZE[node.kind];
      return {
        id: ids.get(node.key)!,
        type: node.kind,
        position: node.position,
        data: (node.data ?? {}) as CanvasNode["data"],
        width: size.width,
        height: size.height,
      };
    }),
    edges: template.edges.map((edge) => ({
      id: createId(),
      source: ids.get(edge.source)!,
      target: ids.get(edge.target)!,
      type: "default",
    })),
  };
}

/**
 * Small, intentionally provider-neutral workflow starters.
 *
 * Provider/model fields stay empty so importing a template never silently selects a model. The
 * graph is still immediately useful: users only need to fill the prompt/upload a reference and
 * choose a configured model on the generation node.
 */
export const CANVAS_TEMPLATES: readonly CanvasTemplateDefinition[] = [
  {
    id: "text-to-image",
    nameKey: "canvas.templates.textToImageName",
    descriptionKey: "canvas.templates.textToImageDescription",
    nodes: [
      { key: "prompt", kind: "text", position: { x: 0, y: 90 }, data: { text: "" } },
      {
        key: "generation",
        kind: "imageGen",
        position: { x: 380, y: 0 },
        data: { aspect: "1:1", count: 1 },
      },
    ],
    edges: [{ source: "prompt", target: "generation" }],
  },
  {
    id: "reference-to-image",
    nameKey: "canvas.templates.referenceToImageName",
    descriptionKey: "canvas.templates.referenceToImageDescription",
    nodes: [
      { key: "prompt", kind: "text", position: { x: 0, y: 0 }, data: { text: "" } },
      { key: "reference", kind: "image", position: { x: 0, y: 230 } },
      {
        key: "generation",
        kind: "imageGen",
        position: { x: 380, y: 110 },
        data: { aspect: "1:1", count: 1 },
      },
    ],
    edges: [
      { source: "prompt", target: "generation" },
      { source: "reference", target: "generation" },
    ],
  },
  {
    id: "image-to-video",
    nameKey: "canvas.templates.imageToVideoName",
    descriptionKey: "canvas.templates.imageToVideoDescription",
    nodes: [
      { key: "image", kind: "image", position: { x: 0, y: 40 } },
      {
        key: "generation",
        kind: "videoGen",
        position: { x: 380, y: 0 },
        data: { duration: 5, resolution: "720p" },
      },
    ],
    edges: [{ source: "image", target: "generation" }],
  },
];
