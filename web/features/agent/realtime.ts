"use client";

import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import type {
  AgentApprovalEnvelope,
  AgentEventEnvelope,
  AgentToolCallEnvelope,
} from "@/features/agent/types";
import { handshake } from "@/lib/device";

const AGENT_EVENT_METHOD = "AgentEvent";
const AGENT_TOOL_CALL_METHOD = "AgentToolCall";
const AGENT_APPROVAL_METHOD = "AgentApprovalRequest";
const RECONNECT_DELAYS_MS = [0, 2_000, 10_000, 30_000];

type AgentEventListener = (event: AgentEventEnvelope) => void;
type AgentToolCallListener = (call: AgentToolCallEnvelope) => void;
type AgentApprovalListener = (approval: AgentApprovalEnvelope) => void;
type ReconnectListener = () => void;

let connection: HubConnection | null = null;
let connectPromise: Promise<HubConnection> | null = null;
let subscribers = 0;
let restartTimer: ReturnType<typeof setTimeout> | null = null;
const eventListeners = new Set<AgentEventListener>();
const toolCallListeners = new Set<AgentToolCallListener>();
const approvalListeners = new Set<AgentApprovalListener>();
const reconnectListeners = new Set<ReconnectListener>();

function getConnection(): HubConnection {
  if (connection) return connection;

  connection = new HubConnectionBuilder()
    .withUrl("/hubs/sol", { withCredentials: true })
    .withAutomaticReconnect(RECONNECT_DELAYS_MS)
    .configureLogging(
      process.env.NODE_ENV === "development" ? LogLevel.Warning : LogLevel.Error,
    )
    .build();

  connection.on(AGENT_EVENT_METHOD, (event: AgentEventEnvelope) => {
    eventListeners.forEach((listener) => listener(event));
  });
  connection.on(AGENT_TOOL_CALL_METHOD, (call: AgentToolCallEnvelope) => {
    toolCallListeners.forEach((listener) => listener(call));
  });
  connection.on(AGENT_APPROVAL_METHOD, (approval: AgentApprovalEnvelope) => {
    approvalListeners.forEach((listener) => listener(approval));
  });
  connection.onreconnected(() => {
    reconnectListeners.forEach((listener) => listener());
  });

  connection.onclose(() => {
    if (subscribers === 0 || restartTimer) return;
    restartTimer = setTimeout(() => {
      restartTimer = null;
      if (subscribers > 0) void ensureAgentRealtime().catch(() => undefined);
    }, 5_000);
  });

  return connection;
}

async function ensureAgentRealtime(): Promise<HubConnection> {
  const current = getConnection();
  if (current.state === HubConnectionState.Connected) return current;
  if (connectPromise) return connectPromise;

  connectPromise = (async () => {
    // The hub validates the HttpOnly device cookie during connect. Running the idempotent
    // handshake first also restores that cookie from the browser mirror after it was cleared.
    await handshake();
    if (current.state === HubConnectionState.Disconnected) await current.start();
    return current;
  })();

  try {
    return await connectPromise;
  } finally {
    connectPromise = null;
  }
}

async function stopWhenIdle(): Promise<void> {
  // React StrictMode releases and immediately reacquires effects in development. Yield once so
  // that pair does not tear down a healthy connection between the two mounts.
  await Promise.resolve();
  if (subscribers > 0) return;

  if (restartTimer) {
    clearTimeout(restartTimer);
    restartTimer = null;
  }

  try {
    if (connectPromise) await connectPromise;
    if (connection?.state !== HubConnectionState.Disconnected) await connection?.stop();
  } catch {
    // A failed start is already reflected by the panel's HTTP/replay path.
  }
}

/**
 * Shares one SignalR connection across every mounted consumer.
 *
 * The returned cleanup is ref-counted, so a panel remount or a future observer does not stop a
 * connection another consumer still needs.
 */
export function subscribeAgentEvents(listener: AgentEventListener): () => void {
  return subscribe(eventListeners, listener);
}

export function subscribeAgentToolCalls(listener: AgentToolCallListener): () => void {
  return subscribe(toolCallListeners, listener);
}

export function subscribeAgentApprovals(listener: AgentApprovalListener): () => void {
  return subscribe(approvalListeners, listener);
}

export function subscribeAgentReconnect(listener: ReconnectListener): () => void {
  return subscribe(reconnectListeners, listener);
}

/** Returns the current executor id, reconnecting first when needed. */
export async function getAgentConnectionId(): Promise<string | null> {
  try {
    return (await ensureAgentRealtime()).connectionId;
  } catch {
    return null;
  }
}

export async function attachAgentRun(runId: string, canvasId: string): Promise<string | null> {
  try {
    return await (await ensureAgentRealtime()).invoke<string>("AttachAgentRun", runId, canvasId);
  } catch {
    return null;
  }
}

export async function detachAgentRun(runId: string): Promise<void> {
  const current = connection;
  if (current?.state !== HubConnectionState.Connected) return;
  await current.invoke("DetachAgentRun", runId).catch(() => undefined);
}

export async function submitAgentToolResult(
  runId: string,
  toolUseId: string,
  resultJson: string,
  isError: boolean,
): Promise<void> {
  const current = await ensureAgentRealtime();
  await current.invoke("SubmitToolResult", runId, toolUseId, resultJson, isError);
}

export async function submitAgentApproval(
  runId: string,
  approvalId: string,
  approved: boolean,
): Promise<void> {
  const current = await ensureAgentRealtime();
  await current.invoke("SubmitApproval", runId, approvalId, approved);
}

function subscribe<T>(listeners: Set<T>, listener: T): () => void {
  listeners.add(listener);
  subscribers += 1;
  void ensureAgentRealtime().catch(() => undefined);

  let released = false;
  return () => {
    if (released) return;
    released = true;
    listeners.delete(listener);
    subscribers = Math.max(0, subscribers - 1);
    if (subscribers === 0) void stopWhenIdle();
  };
}
