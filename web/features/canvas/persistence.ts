import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";
import type { ImageNodeData, NodeExecution } from "@/features/canvas/types";

/**
 * Canvas persistence.
 *
 * Two rules carried over from OpenCowork, both load-bearing:
 *
 * 1. Strip URLs that cannot outlive the document before saving. A `data:` URL would put
 *    megabytes of base64 into every save and every undo snapshot. A `blob:` URL is worse: it is
 *    small, so it saves cleanly, but it is dead the moment the document unloads — restoring one
 *    leaves a node rendering a broken image with no way back, because the upload affordance only
 *    appears when there is no image at all. Both are dropped so the node returns to an
 *    uploadable empty state.
 *
 * 2. A node persisted mid-run comes back `interrupted`, not `running`. The tab that owned the
 *    request is gone, so nothing will ever resolve it — leaving it `running` shows a spinner
 *    that never stops. Video is the exception: its work continues server-side under a job id,
 *    so those keep `running` and `resumeVideoJobs` re-attaches a poller on load.
 */

export const CANVAS_SCHEMA_VERSION = 2;

const STORAGE_PREFIX = "sol.canvas.";

export interface CanvasSnapshot {
  schemaVersion: number;
  nodes: CanvasNode[];
  edges: CanvasEdge[];
  savedAt: number;
}

/**
 * URLs that are only valid inside the document that created them.
 *
 * `blob:` is the one that matters in practice — an uploaded image starts life as an object URL,
 * and matching only `data:` meant those were written to storage and faithfully restored as dead
 * links. Dropping them here also repairs canvases already saved in that broken state.
 */
function isEphemeralUrl(url: string | undefined): boolean {
  return typeof url === "string" && (url.startsWith("data:") || url.startsWith("blob:"));
}

/** Drops ephemeral payloads and normalizes in-flight execution state. */
export function stripForStorage(nodes: CanvasNode[]): CanvasNode[] {
  return nodes.map((node) => {
    const data = { ...node.data } as ImageNodeData & { execution?: NodeExecution };

    if (isEphemeralUrl(data.assetUrl)) {
      delete data.assetUrl;
    }
    if (isEphemeralUrl((data as { posterUrl?: string }).posterUrl)) {
      delete (data as { posterUrl?: string }).posterUrl;
    }

    const execution = data.execution;
    if (execution && (execution.status === "running" || execution.status === "queued")) {
      const hasServerJob = Boolean((data as { jobId?: string }).jobId);

      data.execution = hasServerJob
        ? execution
        : { ...execution, status: "interrupted", finishedAt: Date.now() };
    }

    // Selection is view state; restoring it would highlight nodes the user never touched.
    return { ...node, data: data as CanvasNode["data"], selected: false };
  });
}

export function toSnapshot(nodes: CanvasNode[], edges: CanvasEdge[]): CanvasSnapshot {
  return {
    schemaVersion: CANVAS_SCHEMA_VERSION,
    nodes: stripForStorage(nodes),
    edges,
    savedAt: Date.now(),
  };
}

export function saveLocal(canvasId: string, nodes: CanvasNode[], edges: CanvasEdge[]): void {
  try {
    window.localStorage.setItem(
      `${STORAGE_PREFIX}${canvasId}`,
      JSON.stringify(toSnapshot(nodes, edges)),
    );
  } catch {
    // Quota exceeded, or storage disabled in a private window. A canvas that cannot be cached
    // locally still works; losing the save is not worth breaking the session over.
  }
}

export function loadLocal(canvasId: string): CanvasSnapshot | null {
  try {
    const raw = window.localStorage.getItem(`${STORAGE_PREFIX}${canvasId}`);
    if (!raw) return null;

    const parsed = JSON.parse(raw) as Partial<CanvasSnapshot>;

    // A snapshot from an older schema is discarded rather than guessed at: an empty canvas is a
    // better outcome than one whose nodes silently lost their meaning.
    if (parsed.schemaVersion !== CANVAS_SCHEMA_VERSION) return null;
    if (!Array.isArray(parsed.nodes) || !Array.isArray(parsed.edges)) return null;

    return {
      schemaVersion: CANVAS_SCHEMA_VERSION,
      nodes: stripForStorage(parsed.nodes),
      edges: parsed.edges,
      savedAt: parsed.savedAt ?? 0,
    };
  } catch {
    return null;
  }
}

export function clearLocal(canvasId: string): void {
  try {
    window.localStorage.removeItem(`${STORAGE_PREFIX}${canvasId}`);
  } catch {
    // Nothing to do.
  }
}

/** Serializes for file export. Pretty-printed so an exported canvas is diffable. */
export function exportCanvas(nodes: CanvasNode[], edges: CanvasEdge[]): string {
  return JSON.stringify(toSnapshot(nodes, edges), null, 2);
}

export type ImportResult =
  | { ok: true; snapshot: CanvasSnapshot }
  | { ok: false; detail: string };

/**
 * Parses an exported canvas.
 *
 * Validated structurally rather than trusted: an import can come from a file someone was sent,
 * and a malformed node would otherwise crash the renderer on the next frame.
 */
export function importCanvas(json: string): ImportResult {
  let parsed: unknown;

  try {
    parsed = JSON.parse(json);
  } catch {
    return { ok: false, detail: "invalid JSON" };
  }

  if (typeof parsed !== "object" || parsed === null) {
    return { ok: false, detail: "expected an object" };
  }

  const candidate = parsed as Partial<CanvasSnapshot>;

  if (candidate.schemaVersion !== CANVAS_SCHEMA_VERSION) {
    return { ok: false, detail: `unsupported schema version ${candidate.schemaVersion}` };
  }

  if (!Array.isArray(candidate.nodes) || !Array.isArray(candidate.edges)) {
    return { ok: false, detail: "missing nodes or edges" };
  }

  const ids = new Set<string>();
  for (const node of candidate.nodes) {
    if (typeof node?.id !== "string" || typeof node?.type !== "string") {
      return { ok: false, detail: "a node is missing its id or type" };
    }
    if (typeof node.position?.x !== "number" || typeof node.position?.y !== "number") {
      return { ok: false, detail: `node ${node.id} has no valid position` };
    }
    if (ids.has(node.id)) {
      return { ok: false, detail: `duplicate node id ${node.id}` };
    }
    ids.add(node.id);
  }

  // An edge pointing at a missing node would render as a line into empty space.
  for (const edge of candidate.edges) {
    if (!ids.has(edge?.source) || !ids.has(edge?.target)) {
      return { ok: false, detail: "an edge references a missing node" };
    }
  }

  return {
    ok: true,
    snapshot: {
      schemaVersion: CANVAS_SCHEMA_VERSION,
      nodes: stripForStorage(candidate.nodes),
      edges: candidate.edges,
      savedAt: candidate.savedAt ?? Date.now(),
    },
  };
}
