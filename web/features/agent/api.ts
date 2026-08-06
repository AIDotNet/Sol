import type {
  AgentEventEnvelope,
  AgentImageAttachment,
  AgentMessage,
  AgentToolCatalogEntry,
  AgentRun,
  AgentSession,
  AgentStoredEvent,
} from "@/features/agent/types";

const BASE = "/api/v1/agent";

export class AgentApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly details: string[] = [],
  ) {
    super(message);
    this.name = "AgentApiError";
  }

  get isNotFound(): boolean {
    return this.status === 404;
  }

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
    throw new AgentApiError("Failed to reach the API", 0);
  }

  if (response.status === 204) return undefined as T;

  if (!response.ok) {
    let message = `Request failed with status ${response.status}`;
    let details: string[] = [];

    try {
      const body = (await response.json()) as { error?: string; details?: string[] };
      if (Array.isArray(body.details)) details = body.details;
      message = details[0] ?? body.error ?? message;
    } catch {
      // A proxy failure can return HTML. Keep the status-derived message in that case.
    }

    throw new AgentApiError(message, response.status, details);
  }

  return (await response.json()) as T;
}

export async function findSession(canvasId: string): Promise<AgentSession | null> {
  try {
    return await request(`/sessions?canvasId=${encodeURIComponent(canvasId)}`);
  } catch (error) {
    if (error instanceof AgentApiError && error.isNotFound) return null;
    throw error;
  }
}

export function createSession(canvasId: string, title?: string): Promise<AgentSession> {
  return request("/sessions", {
    method: "POST",
    body: JSON.stringify({ canvasId, title: title ?? null }),
  });
}

export async function listMessages(sessionId: string): Promise<AgentMessage[]> {
  const response = await request<{ messages: AgentMessage[] }>(
    `/sessions/${encodeURIComponent(sessionId)}/messages`,
  );
  return response.messages;
}

export function clearMessages(sessionId: string): Promise<void> {
  return request(`/sessions/${encodeURIComponent(sessionId)}/messages`, { method: "DELETE" });
}

export function createRun(
  sessionId: string,
  input: {
    providerId: string;
    modelKey: string;
    prompt: string;
    images?: AgentImageAttachment[];
    executorConnectionId?: string | null;
  },
): Promise<AgentRun> {
  return request(`/sessions/${encodeURIComponent(sessionId)}/runs`, {
    method: "POST",
    body: JSON.stringify({
      ...input,
      images: input.images?.map((image) => image.url),
    }),
  });
}

export async function findActiveRun(canvasId: string): Promise<AgentRun | null> {
  try {
    return await request(`/runs/active?canvasId=${encodeURIComponent(canvasId)}`);
  } catch (error) {
    if (error instanceof AgentApiError && error.isNotFound) return null;
    throw error;
  }
}

export function getRun(runId: string): Promise<AgentRun> {
  return request(`/runs/${encodeURIComponent(runId)}`);
}

export async function listEvents(runId: string, afterSequence = 0): Promise<AgentStoredEvent[]> {
  const response = await request<{ events: AgentStoredEvent[] }>(
    `/runs/${encodeURIComponent(runId)}/events?afterSeq=${afterSequence}`,
  );
  return response.events;
}

export function cancelRun(runId: string): Promise<void> {
  return request(`/runs/${encodeURIComponent(runId)}`, { method: "DELETE" });
}

export async function listTools(): Promise<AgentToolCatalogEntry[]> {
  const response = await request<{ tools: AgentToolCatalogEntry[] }>("/tools");
  return response.tools;
}

export function envelopeFromStored(event: AgentStoredEvent): AgentEventEnvelope {
  let text: string | null = null;
  let error: string | null = null;

  try {
    const payload = JSON.parse(event.payloadJson) as { text?: unknown; error?: unknown };
    text = typeof payload.text === "string" ? payload.text : null;
    error = typeof payload.error === "string" ? payload.error : null;
  } catch {
    // The raw payload remains available in `json`; malformed optional fields are ignored.
  }

  return {
    runId: event.runId,
    sequence: event.sequence,
    type: event.type,
    text,
    json: event.payloadJson,
    error,
    sentAt: event.createdAt,
  };
}
