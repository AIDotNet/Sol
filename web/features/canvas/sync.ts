"use client";

import * as api from "@/features/canvas/api";
import { loadLocal, saveLocal, toSnapshot } from "@/features/canvas/persistence";
import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";

/**
 * Reconciles the local cache with the server copy.
 *
 * localStorage stays the read path — it paints instantly and keeps working offline. The server
 * is the durable copy. On load both are consulted and the newer one wins; on change the local
 * write is immediate and the server write is debounced by the caller.
 *
 * Last-write-wins by timestamp is enough here: a canvas belongs to one device, so genuine
 * concurrent edits mean the same person in two tabs, and the loser is recoverable from undo.
 */

/** The key the single hardcoded canvas used before multi-canvas existed. */
const LEGACY_LOCAL_ID = "default";

const CURRENT_ID_KEY = "sol.canvas.current";

export interface ResolvedCanvas {
  id: string;
  /** Null when only the local copy was reachable, so the caller supplies its own label. */
  name: string | null;
  nodes: CanvasNode[];
  edges: CanvasEdge[];
  /** True when the local copy was ahead and has been pushed. */
  pushedLocal: boolean;
}

export function rememberCurrentCanvas(id: string): void {
  try {
    window.localStorage.setItem(CURRENT_ID_KEY, id);
  } catch {
    // Private-window storage failure is not worth breaking navigation over.
  }
}

export function lastOpenedCanvas(): string | null {
  try {
    return window.localStorage.getItem(CURRENT_ID_KEY);
  } catch {
    return null;
  }
}

/**
 * Migrates the pre-multi-canvas local document onto the server.
 *
 * Returns the new canvas id, or null when there was nothing to migrate. Runs before the
 * canvas list is consulted so a user who only ever had the implicit "default" canvas keeps
 * their work rather than landing on an empty new one.
 */
export async function migrateLegacyCanvas(name: string): Promise<string | null> {
  const legacy = loadLocal(LEGACY_LOCAL_ID);
  if (!legacy || legacy.nodes.length === 0) {
    // Nothing worth keeping. Clear the key so this does not run again.
    clearLegacy();
    return null;
  }

  const created = await api.createCanvas(name, {
    nodes: legacy.nodes,
    edges: legacy.edges,
  });

  saveLocal(created.id, legacy.nodes, legacy.edges);
  clearLegacy();
  rememberCurrentCanvas(created.id);

  return created.id;
}

function clearLegacy(): void {
  try {
    window.localStorage.removeItem(`sol.canvas.${LEGACY_LOCAL_ID}`);
  } catch {
    // Ignore.
  }
}

/**
 * Coerces a stored graph into usable arrays.
 *
 * A freshly created canvas has no graph yet, and an older client may have written a partial
 * one. Both arrive as an object that is truthy but missing `nodes`/`edges`, which would put
 * `undefined` into the store and throw on the next render.
 */
function readGraph(graph: { nodes?: CanvasNode[]; edges?: CanvasEdge[] } | null | undefined): {
  nodes: CanvasNode[];
  edges: CanvasEdge[];
} {
  return {
    nodes: Array.isArray(graph?.nodes) ? graph.nodes : [],
    edges: Array.isArray(graph?.edges) ? graph.edges : [],
  };
}

/**
 * Loads a canvas, preferring whichever copy is newer.
 *
 * A server failure is not fatal: the local copy is returned so editing continues, and the next
 * successful save re-syncs. That matters because the alternative — refusing to open a canvas
 * the user has locally — turns a transient network problem into data the user cannot reach.
 */
export async function resolveCanvas(id: string): Promise<ResolvedCanvas> {
  const local = loadLocal(id);

  let remote: api.CanvasDocument | null = null;
  try {
    remote = await api.getCanvas(id);
  } catch (error) {
    if (error instanceof api.CanvasApiError && !error.isNotFound) {
      // Network or auth problem — fall back to local rather than losing access.
      if (local) {
        return {
          id,
          name: null,
          nodes: local.nodes,
          edges: local.edges,
          pushedLocal: false,
        };
      }
    }
  }

  const remoteUpdated = remote ? Date.parse(remote.updatedAt) : 0;
  const localSaved = local?.savedAt ?? 0;

  // Local ahead: push it, so a canvas edited offline is not silently replaced by a stale copy.
  if (local && localSaved > remoteUpdated) {
    let pushed = false;
    try {
      await api.saveCanvas(id, { nodes: local.nodes, edges: local.edges });
      pushed = true;
    } catch {
      // Durability waits for the next save; the edit itself is not lost.
    }

    return {
      id,
      name: remote?.name ?? null,
      nodes: local.nodes,
      edges: local.edges,
      pushedLocal: pushed,
    };
  }

  if (remote) {
    const graph = readGraph(remote.graph);
    saveLocal(id, graph.nodes, graph.edges);

    return {
      id,
      name: remote.name,
      nodes: graph.nodes,
      edges: graph.edges,
      pushedLocal: false,
    };
  }

  return {
    id,
    name: null,
    nodes: local?.nodes ?? [],
    edges: local?.edges ?? [],
    pushedLocal: false,
  };
}

export type SaveState = "idle" | "saving" | "saved" | "error";

/**
 * Writes both copies.
 *
 * The local write happens first and unconditionally, so a server failure never costs the user
 * their edit — it only costs durability until the next successful save.
 */
export async function persistCanvas(
  id: string,
  nodes: CanvasNode[],
  edges: CanvasEdge[],
): Promise<SaveState> {
  saveLocal(id, nodes, edges);

  // The same stripping the local copy gets: no blob/data URLs, no phantom `running` runs.
  const snapshot = toSnapshot(nodes, edges);

  try {
    await api.saveCanvas(id, { nodes: snapshot.nodes, edges: snapshot.edges });
    return "saved";
  } catch {
    return "error";
  }
}
