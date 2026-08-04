import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";
import type { ImageNodeData, NodeKind, TextNodeData } from "@/features/canvas/types";

/**
 * Collects a generation node's inputs by walking edges backwards.
 *
 * There is no dataflow engine: a run pulls what it needs at the moment it starts. Text nodes
 * contribute their text, image nodes contribute their asset, and generation nodes are
 * transparent — traversal continues through them so chaining two generations forwards the
 * original prompt rather than stopping at the intermediate config.
 *
 * Ported from OpenCowork's `collectUpstreamText` / `collectUpstreamImages`, with cycle
 * protection made explicit: the canvas permits cycles (nothing stops a user wiring output back
 * to input), so an unguarded DFS would hang the tab.
 */

export interface UpstreamInputs {
  /** Upstream text, in traversal order, joined by blank lines. */
  prompt: string;
  /** Reference images, nearest first. */
  images: Array<{ url: string; mediaType?: string }>;
}

const GENERATION_KINDS = new Set<NodeKind>(["imageGen", "videoGen"]);

/** Adjacency from target to sources, built once per traversal. */
function buildIncoming(edges: CanvasEdge[]): Map<string, string[]> {
  const incoming = new Map<string, string[]>();

  for (const edge of edges) {
    const sources = incoming.get(edge.target);
    if (sources) {
      sources.push(edge.source);
    } else {
      incoming.set(edge.target, [edge.source]);
    }
  }

  return incoming;
}

export function collectUpstream(
  nodeId: string,
  nodes: CanvasNode[],
  edges: CanvasEdge[],
): UpstreamInputs {
  const byId = new Map(nodes.map((node) => [node.id, node]));
  const incoming = buildIncoming(edges);

  const texts: string[] = [];
  const images: Array<{ url: string; mediaType?: string }> = [];
  const visited = new Set<string>([nodeId]);

  // Iterative rather than recursive: a long chain would otherwise risk a stack overflow, and
  // the explicit stack makes the visited-set guard easy to see.
  const stack = [...(incoming.get(nodeId) ?? [])];

  while (stack.length > 0) {
    const currentId = stack.shift()!;

    // Guards against cycles, and against a diamond re-reading the same node twice.
    if (visited.has(currentId)) continue;
    visited.add(currentId);

    const node = byId.get(currentId);
    if (!node) continue;

    const kind = (node.type ?? "text") as NodeKind;

    if (kind === "text") {
      const text = (node.data as TextNodeData).text?.trim();
      if (text) texts.push(text);
    } else if (kind === "image" || kind === "video") {
      const data = node.data as ImageNodeData;
      if (data.assetUrl) {
        images.push({ url: data.assetUrl, mediaType: data.mediaType });
      }
      // An image with no text of its own contributes the prompt that produced it, so feeding a
      // result back in as a reference keeps its description.
      if (data.prompt?.trim() && !data.assetUrl) {
        texts.push(data.prompt.trim());
      }
    }

    // Generation nodes carry no content themselves but are not walls: traversal continues
    // through them so a chain of generations still sees the original prompt.
    if (kind === "text" || GENERATION_KINDS.has(kind)) {
      stack.push(...(incoming.get(currentId) ?? []));
    }
  }

  return { prompt: texts.join("\n\n"), images };
}

/**
 * Reports whether connecting source→target would close a cycle.
 *
 * @xyflow/react does not prevent cycles, and while `collectUpstream` tolerates them, a graph
 * that loops back on itself is almost always a mis-drag. Rejecting it at connect time is
 * clearer than silently producing a truncated prompt.
 */
export function wouldCreateCycle(
  source: string,
  target: string,
  edges: CanvasEdge[],
): boolean {
  if (source === target) return true;

  // The new edge closes a cycle exactly when `source` is already reachable downstream of
  // `target`.
  const outgoing = new Map<string, string[]>();
  for (const edge of edges) {
    const targets = outgoing.get(edge.source);
    if (targets) {
      targets.push(edge.target);
    } else {
      outgoing.set(edge.source, [edge.target]);
    }
  }

  const visited = new Set<string>();
  const stack = [target];

  while (stack.length > 0) {
    const current = stack.pop()!;
    if (current === source) return true;
    if (visited.has(current)) continue;
    visited.add(current);
    stack.push(...(outgoing.get(current) ?? []));
  }

  return false;
}
