import type { AiModel, AiProvider, ModelCategory, ProviderType } from "@/features/ai/types";

/**
 * Browser-side client for the AI configuration API.
 *
 * These run in the browser, not on the server, so they hit the Next.js rewrite (`/api/*` →
 * the .NET API) rather than `lib/api/client.ts`, which is `server-only`. `credentials: "include"`
 * is required on every call: the device cookie is what identifies the owner of the configuration,
 * and without it the API returns 401.
 */

const BASE = "/api/v1";

export class AiApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly details: string[] = [],
  ) {
    super(message);
    this.name = "AiApiError";
  }

  /** The device cookie is missing or unrecognised; the caller should re-run the handshake. */
  get isUnauthorized(): boolean {
    return this.status === 401;
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
    throw new AiApiError("Failed to reach the API", 0);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  if (!response.ok) {
    // Error bodies are `{ error, details[] }`, but a proxy or crash can return HTML instead.
    let details: string[] = [];
    let error = `Request failed with status ${response.status}`;

    try {
      const body = (await response.json()) as { error?: string; details?: string[] };
      if (body.error) error = body.error;
      if (Array.isArray(body.details)) details = body.details;
    } catch {
      // Keep the status-derived message.
    }

    throw new AiApiError(error, response.status, details);
  }

  return (await response.json()) as T;
}

// --- providers ---

export interface CreateProviderInput {
  builtinId?: string | null;
  name: string;
  description?: string | null;
  icon?: string | null;
  type: ProviderType;
  baseUrl: string;
  apiKey?: string;
  enabled?: boolean;
  presetVersion?: number;
  models?: CreateModelInput[];
}

export interface CreateModelInput {
  modelKey: string;
  name?: string;
  type?: ProviderType | null;
  category?: ModelCategory;
  icon?: string | null;
  contextLength?: number | null;
  maxOutputTokens?: number | null;
  supportsVision?: boolean;
  supportsFunctionCall?: boolean;
  supportsThinking?: boolean;
  enabled?: boolean;
}

/**
 * A partial provider update.
 *
 * `apiKey` is three-state, matching the server: omit it to keep the stored key (the common case
 * when editing a base URL), pass `""` to clear it, pass a value to replace it. Sending `null`
 * would be indistinguishable from omitting it after JSON round-tripping, so it is not allowed.
 */
export interface UpdateProviderInput {
  name?: string;
  description?: string | null;
  icon?: string | null;
  type?: ProviderType;
  baseUrl?: string;
  apiKey?: string;
  enabled?: boolean;
}

export function listProviders(): Promise<{ providers: AiProvider[] }> {
  return request("/ai/providers");
}

export function createProvider(input: CreateProviderInput): Promise<AiProvider> {
  return request("/ai/providers", { method: "POST", body: JSON.stringify(input) });
}

export function updateProvider(id: string, input: UpdateProviderInput): Promise<AiProvider> {
  return request(`/ai/providers/${id}`, { method: "PATCH", body: JSON.stringify(input) });
}

export function deleteProvider(id: string): Promise<void> {
  return request(`/ai/providers/${id}`, { method: "DELETE" });
}

export interface ProviderCheckResult {
  ok: boolean;
  statusCode: number | null;
  error: string | null;
  modelCount: number | null;
}

export function checkProvider(id: string): Promise<ProviderCheckResult> {
  return request(`/ai/providers/${id}/check`, { method: "POST" });
}

export interface DiscoveredModel {
  modelKey: string;
  name: string;
  category: ModelCategory;
  icon: string | null;
}

/** Queries the provider's own catalog. Does not persist anything. */
export function fetchUpstreamModels(id: string): Promise<{ models: DiscoveredModel[] }> {
  return request(`/ai/providers/${id}/upstream-models`);
}

/** Adds models, skipping any the provider already has. Safe to re-run. */
export function importModels(
  id: string,
  models: CreateModelInput[],
): Promise<{ added: number }> {
  return request(`/ai/providers/${id}/models/import`, {
    method: "POST",
    body: JSON.stringify({ models }),
  });
}

// --- models ---

export function addModel(providerId: string, input: CreateModelInput): Promise<AiModel> {
  return request(`/ai/providers/${providerId}/models`, {
    method: "POST",
    body: JSON.stringify(input),
  });
}

export interface UpdateModelInput {
  name?: string;
  /** `""` clears the override so the model follows its provider again. */
  type?: ProviderType | "";
  category?: ModelCategory;
  icon?: string | null;
  contextLength?: number | null;
  maxOutputTokens?: number | null;
  supportsVision?: boolean;
  supportsFunctionCall?: boolean;
  supportsThinking?: boolean;
  enabled?: boolean;
}

export function updateModel(modelId: string, input: UpdateModelInput): Promise<AiModel> {
  return request(`/ai/models/${modelId}`, { method: "PATCH", body: JSON.stringify(input) });
}

export function deleteModel(modelId: string): Promise<void> {
  return request(`/ai/models/${modelId}`, { method: "DELETE" });
}

// --- MCP ---

export type McpTransport = "stdio" | "sse" | "streamable-http";

export interface McpEnvEntry {
  key: string;
  value: string;
}

export interface McpServer {
  id: string;
  name: string;
  description: string | null;
  enabled: boolean;
  transport: McpTransport;
  command: string | null;
  args: string[];
  env: McpEnvEntry[];
  cwd: string | null;
  url: string | null;
  headers: McpEnvEntry[];
  autoFallback: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateMcpServerInput {
  name: string;
  description?: string | null;
  transport: McpTransport;
  command?: string | null;
  args?: string[];
  env?: McpEnvEntry[];
  cwd?: string | null;
  url?: string | null;
  headers?: McpEnvEntry[];
  autoFallback?: boolean;
  enabled?: boolean;
}

export function listMcpServers(): Promise<{ servers: McpServer[] }> {
  return request("/mcp/servers");
}

export function createMcpServer(input: CreateMcpServerInput): Promise<McpServer> {
  return request("/mcp/servers", { method: "POST", body: JSON.stringify(input) });
}

export function updateMcpServer(
  id: string,
  input: Partial<CreateMcpServerInput>,
): Promise<McpServer> {
  return request(`/mcp/servers/${id}`, { method: "PATCH", body: JSON.stringify(input) });
}

export function deleteMcpServer(id: string): Promise<void> {
  return request(`/mcp/servers/${id}`, { method: "DELETE" });
}

export function importMcpServers(
  servers: CreateMcpServerInput[],
): Promise<{ added: number }> {
  return request("/mcp/servers/import", {
    method: "POST",
    body: JSON.stringify({ servers }),
  });
}

// --- URL quick config ---

export interface ImportConfigPayload {
  providers: Array<{
    builtinId: string | null;
    name: string;
    type: ProviderType;
    apiKey: string | null;
    baseUrl: string | null;
    models: CreateModelInput[];
  }>;
}

export function importConfig(
  payload: ImportConfigPayload,
): Promise<{ created: number; updated: number }> {
  return request("/ai/config/import", { method: "POST", body: JSON.stringify(payload) });
}

// --- canvas assets ---

export interface UploadedAsset {
  url: string;
  mediaType: string;
  assetId: string;
}

/**
 * Uploads an image and returns the URL the canvas should store.
 *
 * An object URL is not a substitute for this. It dies with the document, so a node holding one
 * breaks on reload — and the generation endpoint only resolves `/api/v1/canvas/assets/` URLs,
 * so an un-uploaded image is silently ignored when used as a reference.
 */
export async function uploadAsset(file: File, signal?: AbortSignal): Promise<UploadedAsset> {
  const form = new FormData();
  form.append("file", file);

  // No Content-Type header: the browser must set it so the multipart boundary is included.
  const response = await fetch(`${BASE}/canvas/assets`, {
    method: "POST",
    credentials: "include",
    body: form,
    signal,
  });

  if (!response.ok) {
    let detail = `Upload failed with status ${response.status}`;
    try {
      const body = (await response.json()) as { error?: string; details?: string[] };
      detail = body.details?.[0] ?? body.error ?? detail;
    } catch {
      // Keep the status-derived message.
    }
    throw new AiApiError(detail, response.status);
  }

  return (await response.json()) as UploadedAsset;
}
