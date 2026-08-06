import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";

/**
 * Canvas document API.
 *
 * The browser keeps a localStorage copy for instant load and offline editing; this is the
 * durable one. Before it existed, clearing site data destroyed every graph the user had made.
 */

const BASE = "/api/v1/canvas";

export interface CanvasSummary {
  id: string;
  name: string;
  nodeCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface CanvasDocument {
  id: string;
  name: string;
  graph: { nodes: CanvasNode[]; edges: CanvasEdge[] } | null;
  createdAt: string;
  updatedAt: string;
}

export class CanvasApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "CanvasApiError";
  }

  get isUnauthorized(): boolean {
    return this.status === 401;
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${BASE}${path}`, {
      ...init,
      credentials: "include",
      headers: {
        Accept: "application/json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });
  } catch {
    throw new CanvasApiError("Failed to reach the API", 0);
  }

  if (response.status === 204) return undefined as T;

  if (!response.ok) {
    let detail = `Request failed with status ${response.status}`;
    try {
      const body = (await response.json()) as { error?: string; details?: string[] };
      detail = body.details?.[0] ?? body.error ?? detail;
    } catch {
      // Keep the status-derived message.
    }
    throw new CanvasApiError(detail, response.status);
  }

  return (await response.json()) as T;
}

export function listCanvases(): Promise<{ canvases: CanvasSummary[] }> {
  return request("");
}

/**
 * Reduces a full document to a list entry.
 *
 * Lets a caller that just created or renamed a canvas splice the result into the list it
 * already holds, rather than refetching to learn something it was handed.
 */
export function summarize(document: CanvasDocument): CanvasSummary {
  return {
    id: document.id,
    name: document.name,
    nodeCount: document.graph?.nodes?.length ?? 0,
    createdAt: document.createdAt,
    updatedAt: document.updatedAt,
  };
}

export function getCanvas(id: string): Promise<CanvasDocument> {
  return request(`/${id}`);
}

export function createCanvas(
  name?: string,
  graph?: { nodes: CanvasNode[]; edges: CanvasEdge[] },
): Promise<CanvasDocument> {
  return request("", { method: "POST", body: JSON.stringify({ name, graph }) });
}

/**
 * Writes a canvas, creating it at this id when it does not exist yet.
 *
 * The create-on-PUT behaviour is what lets a canvas that only ever lived in localStorage be
 * adopted by the server under the id it already has, instead of being duplicated.
 */
export function saveCanvas(
  id: string,
  graph: { nodes: CanvasNode[]; edges: CanvasEdge[] },
  name?: string,
): Promise<CanvasDocument> {
  return request(`/${id}`, { method: "PUT", body: JSON.stringify({ name, graph }) });
}

/** Renames without touching the graph — the server leaves it alone when `graph` is omitted. */
export function renameCanvas(id: string, name: string): Promise<CanvasDocument> {
  return request(`/${id}`, { method: "PUT", body: JSON.stringify({ name }) });
}

export function deleteCanvas(id: string): Promise<void> {
  return request(`/${id}`, { method: "DELETE" });
}

// --- asset library ---

export interface CanvasAssetSummary {
  id: string;
  url: string;
  kind: "image" | "video";
  mediaType: string;
  byteSize: number;
  /** The prompt that produced it, when it was generated rather than uploaded. */
  prompt: string | null;
  createdAt: string;
  /** The folder this asset belongs to, or null when it is in the ungrouped bucket. */
  groupId: string | null;
}

export interface CanvasAssetGroupSummary {
  id: string;
  name: string;
  assetCount: number;
  createdAt: string;
  updatedAt: string;
}

/**
 * Lists stored media, newest first.
 *
 * Capped server-side — this backs a picker, not an archive browser.
 */
export function listAssets(
  kind?: "image" | "video",
): Promise<{ assets: CanvasAssetSummary[] }> {
  return request(`/assets${kind ? `?kind=${kind}` : ""}`);
}

/** Lists user-created folders in the asset library. */
export function listAssetGroups(): Promise<{ groups: CanvasAssetGroupSummary[] }> {
  return request("/assets/groups");
}

/** Creates a folder without assigning any assets. */
export function createAssetGroup(name: string): Promise<CanvasAssetGroupSummary> {
  return request("/assets/groups", { method: "POST", body: JSON.stringify({ name }) });
}

/** Renames a folder while keeping its asset assignments. */
export function renameAssetGroup(
  id: string,
  name: string,
): Promise<CanvasAssetGroupSummary> {
  return request(`/assets/groups/${id}`, {
    method: "PATCH",
    body: JSON.stringify({ name }),
  });
}

/** Deletes a folder; the server leaves its assets available as ungrouped. */
export function deleteAssetGroup(id: string): Promise<void> {
  return request(`/assets/groups/${id}`, { method: "DELETE" });
}

/** Moves a selection into a folder. Pass null to remove the current assignment. */
export function assignAssetsToGroup(
  assetIds: string[],
  groupId: string | null,
): Promise<{ updated: number }> {
  return request("/assets/group", {
    method: "PATCH",
    body: JSON.stringify({ assetIds, groupId }),
  });
}

/**
 * Deletes an asset and the file behind it.
 *
 * A canvas still referencing it will show a broken image. That is the accepted trade: refusing
 * to delete anything that might be in use would make the library unmanageable.
 */
export function deleteAsset(id: string): Promise<void> {
  return request(`/assets/${id}`, { method: "DELETE" });
}
