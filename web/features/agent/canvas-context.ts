import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";

/**
 * Builds the compact canvas snapshot the panel attaches to every run.
 *
 * The server holds no canvas state, so the browser — the same page that will execute the run's
 * tools — describes what the user is looking at: the current selection in detail, then a terse
 * one-line-per-node overview. Without it the Agent's opening move on every "把选中的…" request
 * is a full `read_canvas` probe, and on large graphs that probe can overflow the tool-result cap
 * before it ever finds the selection.
 *
 * Returned text is model-facing and bounded; `null` means there is nothing worth attaching.
 * The server treats it as opaque bounded data (24 KB cap) — keep the format terse.
 */
export function buildCanvasContextText(
  nodes: CanvasNode[],
  edges: CanvasEdge[],
  maxNodes = 200,
): string | null {
  if (nodes.length === 0) return null;

  const lines: string[] = [];
  const selected = nodes.filter((node) => node.selected);
  if (selected.length > 0) {
    lines.push(
      `selected: ${selected.map((node) => describeNode(node, true)).join("; ")}`,
    );
  }
  lines.push(`canvas: ${nodes.length} node(s), ${edges.length} edge(s)`);

  for (const node of nodes.slice(0, maxNodes)) {
    lines.push(`- ${describeNode(node, node.selected === true)}`);
  }
  if (nodes.length > maxNodes) {
    lines.push(`- … ${nodes.length - maxNodes} more node(s) omitted`);
  }

  // Hard parity with the server-side CreateAgentRunRequest cap.
  const text = lines.join("\n");
  return text.length > 24_000 ? `${text.slice(0, 24_000 - 1)}…` : text;
}

function describeNode(node: CanvasNode, isSelected: boolean): string {
  const data = node.data as Record<string, unknown>;
  const marker = isSelected ? "*" : "";
  const kind = typeof node.type === "string" ? node.type : "text";
  const detail = (() => {
    switch (kind) {
      case "text":
        return quote(data.text);
      case "image":
      case "video":
        return quote(data.prompt) ?? (data.assetUrl ? "[asset attached]" : undefined);
      case "imageGen":
        return [
          data.modelId ? `model=${data.modelId}` : undefined,
          data.count ? `count=${data.count}` : undefined,
          data.aspect ? `aspect=${data.aspect}` : undefined,
        ]
          .filter(Boolean)
          .join(" ") || undefined;
      case "videoGen":
        return [
          data.modelId ? `model=${data.modelId}` : undefined,
          data.duration ? `duration=${data.duration}s` : undefined,
          data.resolution ? `resolution=${data.resolution}` : undefined,
        ]
          .filter(Boolean)
          .join(" ") || undefined;
      default:
        return undefined;
    }
  })();

  return `${marker}${node.id} (${kind}${detail ? `, ${detail}` : ""})`;
}

function quote(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const compact = value.trim().replace(/\s+/g, " ");
  if (!compact) return undefined;
  return `"${compact.length > 80 ? `${compact.slice(0, 79)}…` : compact}"`;
}
