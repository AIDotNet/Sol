"use client";

import { create } from "zustand";
import * as api from "@/features/agent/api";
import { getAgentConnectionId } from "@/features/agent/realtime";
import type {
  AgentContentBlock,
  AgentEventEnvelope,
  AgentImageAttachment,
  AgentLiveToolCall,
  AgentMessage,
  AgentRun,
  AgentSession,
} from "@/features/agent/types";
import { isAgentRunActive } from "@/features/agent/types";

interface SendPromptInput {
  providerId: string;
  modelKey: string;
  prompt: string;
  images?: AgentImageAttachment[];
  /** Compact canvas snapshot (selection, overview) built by the sending page. */
  canvasContext?: string | null;
}

interface AgentState {
  open: boolean;
  canvasId: string | null;
  session: AgentSession | null;
  messages: AgentMessage[];
  run: AgentRun | null;
  streamText: string;
  thinkingText: string;
  /** Ordered blocks that have arrived for the current run but are not durable yet. */
  liveBlocks: AgentContentBlock[];
  /** Tool state is updated from realtime events without refetching the whole conversation. */
  liveToolCalls: AgentLiveToolCall[];
  lastSequence: number;
  loading: boolean;
  sending: boolean;
  clearing: boolean;
  error: string | null;

  setOpen: (open: boolean) => void;
  initialize: (canvasId: string) => Promise<void>;
  discoverActiveRun: (canvasId: string) => Promise<void>;
  sendPrompt: (input: SendPromptInput) => Promise<void>;
  clearMessages: () => Promise<void>;
  cancel: () => Promise<void>;
  receiveEvent: (event: AgentEventEnvelope) => void;
}

const repairingRuns = new Set<string>();
const deferredEvents = new Map<string, AgentEventEnvelope[]>();

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "unknown";
}

function optimisticUserMessage(
  run: AgentRun,
  prompt: string,
  images: AgentImageAttachment[],
  ordinal: number,
): AgentMessage {
  return {
    id: `local-${run.id}`,
    runId: run.id,
    ordinal,
    role: "user",
    contentJson: JSON.stringify([
      { kind: "text", text: prompt },
      ...images.map((image) => ({
        kind: "image",
        imageUrl: image.url,
        mediaType: image.mediaType,
      })),
    ]),
    createdAt: run.createdAt,
  };
}

function appendBlocks(
  existing: AgentContentBlock[],
  incoming: AgentContentBlock[],
): AgentContentBlock[] {
  const result = [...existing];
  for (const block of incoming) {
    const previous = result.at(-1);
    if (
      previous
      && previous.kind === block.kind
      && (block.kind === "text" || block.kind === "thinking")
    ) {
      result[result.length - 1] = {
        ...previous,
        text: (previous.text ?? "") + (block.text ?? ""),
      };
    } else {
      result.push(block);
    }
  }
  return result;
}

function eventPayload(json: string | null): Record<string, unknown> | null {
  if (!json) return null;
  try {
    const value: unknown = JSON.parse(json);
    return typeof value === "object" && value !== null && !Array.isArray(value)
      ? value as Record<string, unknown>
      : null;
  } catch {
    return null;
  }
}

function stringValue(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function jsonValue(value: unknown, fallback = "{}"): string {
  if (typeof value === "string") return value;
  try {
    return JSON.stringify(value) ?? fallback;
  } catch {
    return fallback;
  }
}

export const useAgentStore = create<AgentState>((set, get) => {
  let pendingLiveBlocks: AgentContentBlock[] = [];
  let liveFlushTimer: ReturnType<typeof setTimeout> | null = null;

  function flushPendingLive(): void {
    if (liveFlushTimer !== null) {
      clearTimeout(liveFlushTimer);
      liveFlushTimer = null;
    }
    if (pendingLiveBlocks.length === 0) return;

    const pending = pendingLiveBlocks;
    pendingLiveBlocks = [];
    set((state) => {
      const liveBlocks = appendBlocks(state.liveBlocks, pending);
      return {
        liveBlocks,
        streamText: liveBlocks
          .filter((block) => block.kind === "text")
          .map((block) => block.text ?? "")
          .join(""),
        thinkingText: liveBlocks
          .filter((block) => block.kind === "thinking")
          .map((block) => block.text ?? "")
          .join(""),
      };
    });
  }

  function queueLiveDelta(kind: "text" | "thinking", text: string): void {
    const previous = pendingLiveBlocks.at(-1);
    if (previous?.kind === kind) {
      pendingLiveBlocks[pendingLiveBlocks.length - 1] = {
        ...previous,
        text: (previous.text ?? "") + text,
      };
    } else {
      pendingLiveBlocks.push({ kind, text });
    }

    // SignalR can deliver a token per message. Commit at most once per frame-sized window so a
    // long answer does not cause the entire panel to reconcile for every character.
    if (liveFlushTimer === null) {
      liveFlushTimer = setTimeout(() => {
        liveFlushTimer = null;
        flushPendingLive();
      }, 16);
    }
  }

  async function refreshMessages(sessionId: string, clearStream: boolean): Promise<void> {
    try {
      if (clearStream) flushPendingLive();
      const messages = await api.listMessages(sessionId);
      if (get().session?.id !== sessionId) return;
      set({
        messages,
        ...(clearStream
          ? { liveBlocks: [], liveToolCalls: [], streamText: "", thinkingText: "" }
          : {}),
      });
    } catch (error) {
      set({ error: errorMessage(error) });
    }
  }

  function applyEvent(event: AgentEventEnvelope): void {
    const state = get();
    if (state.run?.id !== event.runId) return;
    if (event.sequence > 0 && event.sequence <= state.lastSequence) return;

    const sequence = event.sequence > 0 ? event.sequence : state.lastSequence;

    switch (event.type) {
      case "text.delta":
        if (event.text) queueLiveDelta("text", event.text);
        return;

      case "thinking.delta":
        if (event.text) queueLiveDelta("thinking", event.text);
        return;

      case "tool.started": {
        flushPendingLive();
        const payload = eventPayload(event.json);
        const id = stringValue(payload?.toolUseId);
        const name = stringValue(payload?.toolName) ?? "tool";
        if (!id) return;
        const input = jsonValue(payload?.input);
        set((current) => {
          const liveToolCalls = current.liveToolCalls.some((tool) => tool.id === id)
            ? current.liveToolCalls
            : [
                ...current.liveToolCalls,
                { id, name, input, result: null, isError: false },
              ];
          const hasBlock = current.liveBlocks.some(
            (block) => block.kind === "tool_use" && block.toolUseId === id,
          );
          return {
            lastSequence: sequence,
            run: { ...current.run!, lastSequence: sequence },
            liveToolCalls,
            liveBlocks: hasBlock
              ? current.liveBlocks
              : [
                  ...current.liveBlocks,
                  { kind: "tool_use", toolUseId: id, toolName: name, json: input },
                ],
          };
        });
        return;
      }

      case "tool.completed": {
        flushPendingLive();
        const payload = eventPayload(event.json);
        const id = stringValue(payload?.toolUseId);
        if (!id) return;
        const name = stringValue(payload?.toolName) ?? "tool";
        const result = jsonValue(payload?.result);
        const isError = payload?.isError === true;
        set((current) => {
          const liveToolCalls = current.liveToolCalls.some((tool) => tool.id === id)
            ? current.liveToolCalls.map((tool) =>
                tool.id === id ? { ...tool, result, isError } : tool,
              )
            : [
                ...current.liveToolCalls,
                { id, name, input: "{}", result, isError },
              ];
          const hasBlock = current.liveBlocks.some(
            (block) => block.kind === "tool_use" && block.toolUseId === id,
          );
          return {
            lastSequence: sequence,
            run: { ...current.run!, lastSequence: sequence },
            liveToolCalls,
            liveBlocks: hasBlock
              ? current.liveBlocks
              : [
                  ...current.liveBlocks,
                  { kind: "tool_use", toolUseId: id, toolName: name, json: "{}" },
                ],
          };
        });
        return;
      }

      case "run.started":
        set({
          lastSequence: sequence,
          run: { ...state.run, status: "running", lastSequence: sequence },
        });
        return;

      case "run.awaiting_canvas":
        set({
          lastSequence: sequence,
          run: { ...state.run, status: "awaiting_canvas", lastSequence: sequence },
        });
        return;

      case "run.succeeded": {
        flushPendingLive();
        set((current) => {
          const hasText = current.liveBlocks.some(
            (block) => block.kind === "text" && Boolean(block.text),
          );
          const liveBlocks = !hasText && event.text
            ? appendBlocks(current.liveBlocks, [{ kind: "text", text: event.text }])
            : current.liveBlocks;
          return {
            run: {
              ...current.run!,
              status: "succeeded" as const,
              lastSequence: sequence,
              error: null,
            },
            lastSequence: sequence,
            liveBlocks,
            streamText: event.text ?? current.streamText,
            error: null,
          };
        });
        void refreshMessages(state.run.sessionId, true);
        return;
      }

      case "run.failed": {
        flushPendingLive();
        const failure = event.error ?? "The Agent run failed.";
        const run = {
          ...state.run,
          status: "failed" as const,
          lastSequence: sequence,
          error: failure,
        };
        set({ run, lastSequence: sequence, error: failure });
        void refreshMessages(run.sessionId, true);
        return;
      }

      default:
        if (event.sequence > 0) {
          set({
            lastSequence: sequence,
            run: { ...state.run, lastSequence: sequence },
          });
        }
    }
  }

  async function repairGap(runId: string): Promise<void> {
    if (repairingRuns.has(runId)) return;
    repairingRuns.add(runId);

    try {
      const after = get().run?.id === runId ? get().lastSequence : 0;
      const events = await api.listEvents(runId, after);
      for (const stored of events.sort((a, b) => a.sequence - b.sequence)) {
        applyEvent(api.envelopeFromStored(stored));
      }
    } catch (error) {
      if (get().run?.id === runId) set({ error: errorMessage(error) });
    } finally {
      repairingRuns.delete(runId);
      const deferred = deferredEvents.get(runId) ?? [];
      deferredEvents.delete(runId);
      for (const event of deferred.sort((a, b) => a.sequence - b.sequence)) {
        get().receiveEvent(event);
      }
    }
  }

  return {
    open: false,
    canvasId: null,
    session: null,
    messages: [],
    run: null,
    streamText: "",
    thinkingText: "",
    liveBlocks: [],
    liveToolCalls: [],
    lastSequence: 0,
    loading: false,
    sending: false,
    clearing: false,
    error: null,

    setOpen: (open) => set({ open }),

    initialize: async (canvasId) => {
      set({
        canvasId,
        session: null,
        messages: [],
        run: null,
        streamText: "",
        thinkingText: "",
        liveBlocks: [],
        liveToolCalls: [],
        lastSequence: 0,
        loading: true,
        sending: false,
        clearing: false,
        error: null,
      });

      try {
        const [session, run] = await Promise.all([
          api.findSession(canvasId),
          api.findActiveRun(canvasId),
        ]);
        const messages = session ? await api.listMessages(session.id) : [];
        if (get().canvasId !== canvasId) return;

        set({
          session,
          messages,
          run,
          loading: false,
          liveBlocks: [],
          liveToolCalls: [],
          lastSequence: 0,
        });

        if (run) {
          const events = await api.listEvents(run.id, 0);
          if (get().canvasId !== canvasId || get().run?.id !== run.id) return;
          for (const event of events.sort((a, b) => a.sequence - b.sequence)) {
            get().receiveEvent(api.envelopeFromStored(event));
          }
        }
      } catch (error) {
        if (get().canvasId === canvasId) {
          set({ loading: false, error: errorMessage(error) });
        }
      }
    },

    discoverActiveRun: async (canvasId) => {
      if (get().canvasId !== canvasId || isAgentRunActive(get().run)) return;
      try {
        const run = await api.findActiveRun(canvasId);
        if (!run || get().canvasId !== canvasId) return;
        let session = get().session;
        if (!session || session.id !== run.sessionId) {
          session = await api.findSession(canvasId);
        }
        const messages = session ? await api.listMessages(session.id) : get().messages;
        if (get().canvasId !== canvasId) return;
        set({
          session,
          messages,
          run,
          lastSequence: 0,
          streamText: "",
          thinkingText: "",
          liveBlocks: [],
          liveToolCalls: [],
        });
        for (const event of (await api.listEvents(run.id, 0)).sort(
          (a, b) => a.sequence - b.sequence,
        )) {
          get().receiveEvent(api.envelopeFromStored(event));
        }
      } catch {
        // Polling is takeover support for another tab, not a user action. Stay silent and retry.
      }
    },

    sendPrompt: async ({ providerId, modelKey, prompt, images = [], canvasContext = null }) => {
      const trimmed = prompt.trim();
      const canvasId = get().canvasId;
      if (
        !canvasId
        || !trimmed
        || get().sending
        || get().clearing
        || isAgentRunActive(get().run)
      ) return;

      set({
        sending: true,
        error: null,
        streamText: "",
        thinkingText: "",
        liveBlocks: [],
        liveToolCalls: [],
        lastSequence: 0,
      });

      try {
        let session = get().session;
        if (!session) {
          session = await api.createSession(canvasId);
          if (get().canvasId !== canvasId) return;
          set({ session });
        }

        const executorConnectionId = await getAgentConnectionId();
        const run = await api.createRun(session.id, {
          providerId,
          modelKey,
          prompt: trimmed,
          images,
          executorConnectionId,
          canvasContext,
        });
        if (get().canvasId !== canvasId) return;

        const ordinal = (get().messages.at(-1)?.ordinal ?? -1) + 1;
        set((state) => ({
          run,
          sending: false,
          lastSequence: 0,
          messages: [...state.messages, optimisticUserMessage(run, trimmed, images, ordinal)],
        }));

        // The host can publish run.started before the POST response reaches the browser. Replaying
        // immediately closes that race; the final event also carries the complete answer text.
        const events = await api.listEvents(run.id, 0);
        for (const event of events.sort((a, b) => a.sequence - b.sequence)) {
          get().receiveEvent(api.envelopeFromStored(event));
        }
      } catch (error) {
        if (get().canvasId === canvasId) {
          set({ sending: false, error: errorMessage(error) });
          // A concurrent tab may have won the one-active-run constraint. Refreshing reveals it
          // instead of leaving this panel with only a generic 400.
          if (error instanceof api.AgentApiError && error.status === 400) {
            const run = await api.findActiveRun(canvasId).catch(() => null);
            if (run && get().canvasId === canvasId) set({ run });
          }
        }
      }
    },

    clearMessages: async () => {
      const session = get().session;
      if (
        !session
        || get().clearing
        || get().sending
        || isAgentRunActive(get().run)
      ) return;

      set({ clearing: true, error: null });
      try {
        await api.clearMessages(session.id);
        if (get().session?.id !== session.id) return;
        set({
          messages: [],
          run: null,
          streamText: "",
          thinkingText: "",
          liveBlocks: [],
          liveToolCalls: [],
          lastSequence: 0,
          error: null,
        });
      } catch (error) {
        if (get().session?.id === session.id) set({ error: errorMessage(error) });
      } finally {
        if (get().session?.id === session.id) set({ clearing: false });
      }
    },

    cancel: async () => {
      const run = get().run;
      if (!run || !isAgentRunActive(run)) return;

      try {
        await api.cancelRun(run.id);
        if (get().run?.id !== run.id) return;
        set({
          run: { ...run, status: "cancelled", error: null },
          streamText: "",
          thinkingText: "",
          liveBlocks: [],
          liveToolCalls: [],
          error: null,
        });
        void refreshMessages(run.sessionId, true);
      } catch (error) {
        set({ error: errorMessage(error) });
      }
    },

    receiveEvent: (event) => {
      const state = get();
      if (state.run?.id !== event.runId) return;

      if (event.sequence > state.lastSequence + 1) {
        const deferred = deferredEvents.get(event.runId) ?? [];
        deferred.push(event);
        deferredEvents.set(event.runId, deferred);
        void repairGap(event.runId);
        return;
      }

      applyEvent(event);
    },
  };
});
