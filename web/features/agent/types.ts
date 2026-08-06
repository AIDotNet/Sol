export type AgentRunStatus =
  | "queued"
  | "running"
  | "awaiting_canvas"
  | "awaiting_approval"
  | "succeeded"
  | "failed"
  | "cancelled"
  | "interrupted";

export interface AgentSession {
  id: string;
  canvasId: string;
  title: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface AgentRun {
  id: string;
  sessionId: string;
  canvasId: string;
  providerId: string | null;
  modelKey: string;
  status: AgentRunStatus;
  iteration: number;
  lastSequence: number;
  error: string | null;
  createdAt: string;
  updatedAt: string;
}

export type AgentContentKind = "text" | "image" | "thinking" | "tool_use" | "tool_result";

export interface AgentContentBlock {
  kind: AgentContentKind;
  text?: string | null;
  imageUrl?: string | null;
  mediaType?: string | null;
  toolUseId?: string | null;
  toolName?: string | null;
  json?: string | null;
  isError?: boolean;
}

export interface AgentImageAttachment {
  id: string;
  url: string;
  mediaType: string;
  name: string;
}

export interface AgentMessage {
  id: string;
  runId: string | null;
  ordinal: number;
  role: "user" | "assistant" | string;
  contentJson: string;
  createdAt: string;
}

/** A tool call that is still being rendered from the live Agent event stream. */
export interface AgentLiveToolCall {
  id: string;
  name: string;
  input: string;
  result: string | null;
  isError: boolean;
}

export interface AgentStoredEvent {
  runId: string;
  sequence: number;
  type: string;
  payloadJson: string;
  createdAt: string;
}

export interface AgentToolCatalogEntry {
  name: string;
  site: "canvas" | "server";
  approval: "never" | "always" | "destructive_action";
  description: string;
  inputSchemaJson: string;
  timeoutMilliseconds: number;
}

export interface AgentApprovalEnvelope {
  runId: string;
  approvalId: string;
  toolName: string;
  summary: string;
  inputJson: string;
}

export interface AgentToolCallEnvelope {
  runId: string;
  canvasId: string;
  toolUseId: string;
  toolName: string;
  inputJson: string;
  timeoutMilliseconds: number;
}

/** Sequence zero is an ephemeral stream delta; positive sequences can be replayed. */
export interface AgentEventEnvelope {
  runId: string;
  sequence: number;
  type: string;
  text: string | null;
  json: string | null;
  error: string | null;
  sentAt: string;
}

export function parseAgentContent(contentJson: string): AgentContentBlock[] {
  try {
    const value: unknown = JSON.parse(contentJson);
    if (!Array.isArray(value)) return [];

    return value.filter((block): block is AgentContentBlock => {
      if (typeof block !== "object" || block === null) return false;
      const kind = (block as { kind?: unknown }).kind;
      return (
        kind === "text" ||
        kind === "image" ||
        kind === "thinking" ||
        kind === "tool_use" ||
        kind === "tool_result"
      );
    });
  } catch {
    return [];
  }
}

export function isAgentRunActive(run: AgentRun | null): boolean {
  return (
    run?.status === "queued" ||
    run?.status === "running" ||
    run?.status === "awaiting_canvas" ||
    run?.status === "awaiting_approval"
  );
}
