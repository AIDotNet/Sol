import type { Edge, Node } from "@xyflow/react";
import { NODE_DEFAULT_SIZE, type NodeKind } from "@/features/canvas/types";

/** Space between columns and rows in the automatic layout. */
const COLUMN_GAP = 96;
const ROW_GAP = 36;

type Position = { x: number; y: number };

function dimensions(node: Node): { width: number; height: number } {
  const kind = node.type as NodeKind;
  const fallback = NODE_DEFAULT_SIZE[kind] ?? NODE_DEFAULT_SIZE.text;

  return {
    width: node.width && node.width > 0 ? node.width : fallback.width,
    height: node.height && node.height > 0 ? node.height : fallback.height,
  };
}

/**
 * Sorts by the current visual order. Keeping that order makes repeated layout runs predictable
 * and avoids shuffling sibling nodes every time a user presses the button.
 */
function visualOrder<T extends Node>(a: T, b: T, originalOrder: Map<string, number>): number {
  return (
    a.position.y - b.position.y ||
    a.position.x - b.position.x ||
    (originalOrder.get(a.id) ?? 0) - (originalOrder.get(b.id) ?? 0)
  );
}

function bounds<T extends Node>(nodes: T[]): { x: number; y: number } {
  return {
    x: Math.min(...nodes.map((node) => node.position.x)),
    y: Math.min(...nodes.map((node) => node.position.y)),
  };
}

function layoutAsGrid<T extends Node>(nodes: T[]): Map<string, Position> {
  const originalOrder = new Map(nodes.map((node, index) => [node.id, index]));
  const ordered = [...nodes].sort((a, b) => visualOrder(a, b, originalOrder));
  const columns = Math.max(1, Math.ceil(Math.sqrt(ordered.length)));
  const rows = Math.ceil(ordered.length / columns);
  const columnWidths = Array.from({ length: columns }, () => 0);
  const rowHeights = Array.from({ length: rows }, () => 0);

  ordered.forEach((node, index) => {
    const column = index % columns;
    const row = Math.floor(index / columns);
    const size = dimensions(node);
    columnWidths[column] = Math.max(columnWidths[column], size.width);
    rowHeights[row] = Math.max(rowHeights[row], size.height);
  });

  const origin = bounds(nodes);
  const columnX: number[] = [];
  let x = origin.x;
  for (const width of columnWidths) {
    columnX.push(x);
    x += width + COLUMN_GAP;
  }

  const rowY: number[] = [];
  let y = origin.y;
  for (const height of rowHeights) {
    rowY.push(y);
    y += height + ROW_GAP;
  }

  return new Map(
    ordered.map((node, index) => {
      const column = index % columns;
      const row = Math.floor(index / columns);
      return [node.id, { x: columnX[column], y: rowY[row] }];
    }),
  );
}

/**
 * Computes a left-to-right layout from the graph's edges.
 *
 * A node's layer is one column to the right of its furthest upstream parent. The canvas rejects
 * new cycles, but imported documents can be older or hand-edited, so nodes left in a cycle are
 * placed in a final column instead of making layout hang or overlap the rest of the graph.
 * Disconnected graphs use a compact grid, which is more useful than putting every independent
 * node in one very tall column.
 */
export function autoLayoutNodes<T extends Node>(nodes: T[], edges: Edge[]): T[] {
  if (nodes.length < 2) return nodes;

  const nodeById = new Map(nodes.map((node) => [node.id, node]));
  const originalOrder = new Map(nodes.map((node, index) => [node.id, index]));
  const outgoing = new Map<string, string[]>(nodes.map((node) => [node.id, []]));
  const indegree = new Map(nodes.map((node) => [node.id, 0]));
  let validEdgeCount = 0;

  // Deduplicate edges for the topological pass. React Flow normally prevents duplicates, but
  // imports and older snapshots should not make a node wait for the same parent twice.
  const seenEdges = new Set<string>();
  for (const edge of edges) {
    if (
      edge.source === edge.target ||
      !nodeById.has(edge.source) ||
      !nodeById.has(edge.target)
    ) {
      continue;
    }

    const edgeKey = `${edge.source}\u0000${edge.target}`;
    if (seenEdges.has(edgeKey)) continue;
    seenEdges.add(edgeKey);
    validEdgeCount += 1;
    outgoing.get(edge.source)?.push(edge.target);
    indegree.set(edge.target, (indegree.get(edge.target) ?? 0) + 1);
  }

  if (validEdgeCount === 0) {
    const positions = layoutAsGrid(nodes);
    return nodes.map((node) => ({ ...node, position: positions.get(node.id)! }));
  }

  const compareIds = (a: string, b: string) => {
    const first = nodeById.get(a)!;
    const second = nodeById.get(b)!;
    return visualOrder(first, second, originalOrder);
  };

  const layers = new Map<string, number>();
  const queue = nodes
    .filter((node) => indegree.get(node.id) === 0)
    .sort((a, b) => visualOrder(a, b, originalOrder))
    .map((node) => {
      layers.set(node.id, 0);
      return node.id;
    });

  while (queue.length > 0) {
    const currentId = queue.shift()!;
    const currentLayer = layers.get(currentId) ?? 0;
    const children = [...(outgoing.get(currentId) ?? [])].sort(compareIds);

    for (const childId of children) {
      layers.set(childId, Math.max(layers.get(childId) ?? 0, currentLayer + 1));
      const remaining = (indegree.get(childId) ?? 0) - 1;
      indegree.set(childId, remaining);
      if (remaining === 0) {
        queue.push(childId);
        queue.sort(compareIds);
      }
    }
  }

  // A cycle has no zero-indegree node. Put its members after the acyclic portion as one safe,
  // non-overlapping layer. The normal UI never reaches this branch, but it keeps auto-layout safe
  // for imported graphs too.
  const highestLayer = Math.max(-1, ...layers.values());
  const cycleLayer = highestLayer + 1;
  for (const node of nodes) {
    if (!layers.has(node.id)) layers.set(node.id, cycleLayer);
  }

  const layerNodes = new Map<number, T[]>();
  for (const node of nodes) {
    const layer = layers.get(node.id) ?? 0;
    const group = layerNodes.get(layer) ?? [];
    group.push(node);
    layerNodes.set(layer, group);
  }

  const orderedLayers = [...layerNodes.keys()].sort((a, b) => a - b);
  const columnWidths = orderedLayers.map((layer) =>
    Math.max(...layerNodes.get(layer)!.map((node) => dimensions(node).width)),
  );
  const columnHeights = orderedLayers.map((layer) =>
    layerNodes.get(layer)!.reduce(
      (total, node) => total + dimensions(node).height,
      0,
    ) +
      Math.max(0, layerNodes.get(layer)!.length - 1) * ROW_GAP,
  );
  const maxColumnHeight = Math.max(...columnHeights);
  const origin = bounds(nodes);
  const positions = new Map<string, Position>();
  let columnX = origin.x;

  orderedLayers.forEach((layer, columnIndex) => {
    const group = layerNodes.get(layer)!.sort((a, b) =>
      visualOrder(a, b, originalOrder),
    );
    let nodeY = origin.y + (maxColumnHeight - columnHeights[columnIndex]) / 2;

    for (const node of group) {
      positions.set(node.id, { x: columnX, y: nodeY });
      nodeY += dimensions(node).height + ROW_GAP;
    }

    columnX += columnWidths[columnIndex] + COLUMN_GAP;
  });

  return nodes.map((node) => ({ ...node, position: positions.get(node.id)! }));
}
