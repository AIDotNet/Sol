"use client";

import { ShieldAlert } from "lucide-react";
import { useEffect, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import * as agentApi from "@/features/agent/api";
import { AGENT_CANVAS_TOOL_NAMES, executeCanvasTool } from "@/features/agent/tools";
import {
  attachAgentRun,
  detachAgentRun,
  submitAgentApproval,
  submitAgentToolResult,
  subscribeAgentApprovals,
  subscribeAgentEvents,
  subscribeAgentReconnect,
  subscribeAgentToolCalls,
} from "@/features/agent/realtime";
import { useAgentStore } from "@/features/agent/store";
import {
  isAgentRunActive,
  type AgentApprovalEnvelope,
  type AgentToolCallEnvelope,
} from "@/features/agent/types";

const RESULT_CACHE_PREFIX = "sol.agent-tool-result.";
const activeToolCalls = new Set<string>();
let catalogVerified = false;

export function AgentRuntime({ canvasId }: { canvasId: string }) {
  const t = useT();
  const [approval, setApproval] = useState<AgentApprovalEnvelope | null>(null);
  const initialize = useAgentStore((state) => state.initialize);
  const discoverActiveRun = useAgentStore((state) => state.discoverActiveRun);
  const run = useAgentStore((state) => state.run);

  useEffect(() => {
    const events = subscribeAgentEvents((event) => useAgentStore.getState().receiveEvent(event));
    const calls = subscribeAgentToolCalls((call) => void handleAgentToolCall(call, canvasId));
    const approvals = subscribeAgentApprovals((request) => {
      if (request.runId === useAgentStore.getState().run?.id) setApproval(request);
    });
    const reconnect = subscribeAgentReconnect(() => {
      const current = useAgentStore.getState().run;
      if (current && current.canvasId === canvasId && isAgentRunActive(current)) {
        void attachAgentRun(current.id, canvasId);
      }
    });
    void initialize(canvasId);
    void verifyCanvasToolCatalog();

    return () => {
      const current = useAgentStore.getState().run;
      if (current && current.canvasId === canvasId && isAgentRunActive(current)) {
        void detachAgentRun(current.id);
      }
      reconnect();
      approvals();
      calls();
      events();
    };
  }, [canvasId, initialize]);

  useEffect(() => {
    void discoverActiveRun(canvasId);
    const poll = window.setInterval(() => void discoverActiveRun(canvasId), 5_000);
    return () => window.clearInterval(poll);
  }, [canvasId, discoverActiveRun]);

  useEffect(() => {
    if (!run || run.canvasId !== canvasId || !isAgentRunActive(run)) return;

    void attachAgentRun(run.id, canvasId);
    // Only one tab can claim the executor. Other tabs retry at a low cadence so one takes over
    // after the winning tab closes; the atomic database claim keeps this exactly-once.
    const retry = window.setInterval(() => {
      void attachAgentRun(run.id, canvasId);
    }, 5_000);
    return () => window.clearInterval(retry);
  }, [canvasId, run]);

  if (!approval) return null;

  async function answer(approved: boolean) {
    const current = approval;
    setApproval(null);
    if (!current) return;
    try {
      await submitAgentApproval(current.runId, current.approvalId, approved);
    } catch {
      // The request is re-sent on reconnect while it remains pending.
      setApproval(current);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/35 p-4">
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="agent-approval-title"
        className="canvas-menu w-full max-w-lg rounded-xl border bg-popover p-4 shadow-2xl"
      >
        <div className="flex items-start gap-3">
          <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-destructive/10 text-destructive">
            <ShieldAlert className="size-5" aria-hidden />
          </span>
          <div className="min-w-0 flex-1">
            <h2 id="agent-approval-title" className="text-sm font-semibold">
              {t("agent.approvalTitle")}
            </h2>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">{approval.summary}</p>
          </div>
        </div>
        <pre className="mt-3 max-h-52 overflow-auto rounded-lg bg-muted p-3 text-[0.6875rem] whitespace-pre-wrap break-all">
          {formatApprovalInput(approval.inputJson)}
        </pre>
        <div className="mt-4 flex justify-end gap-2">
          <Button variant="outline" onPress={() => void answer(false)}>
            {t("agent.deny")}
          </Button>
          <Button variant="destructive" onPress={() => void answer(true)}>
            {t("agent.approve")}
          </Button>
        </div>
      </div>
    </div>
  );
}

function formatApprovalInput(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

async function verifyCanvasToolCatalog(): Promise<void> {
  if (catalogVerified) return;
  try {
    const server = (await agentApi.listTools())
      .filter((tool) => tool.site === "canvas")
      .map((tool) => tool.name)
      .sort();
    const client = [...AGENT_CANVAS_TOOL_NAMES].sort();
    if (JSON.stringify(server) !== JSON.stringify(client)) {
      console.error("Agent canvas tool catalog does not match browser handlers.", { server, client });
      return;
    }
    catalogVerified = true;
  } catch {
    // A later mount retries. The executor itself still reports unknown tools explicitly.
  }
}

export async function handleAgentToolCall(
  call: AgentToolCallEnvelope,
  canvasId: string,
): Promise<void> {
  if (call.canvasId !== canvasId) return;

  const executionKey = `${call.runId}.${call.toolUseId}`;
  if (activeToolCalls.has(executionKey)) return;
  activeToolCalls.add(executionKey);

  const key = `${RESULT_CACHE_PREFIX}${call.runId}.${call.toolUseId}`;
  try {
    const cached = window.localStorage.getItem(key);
    if (cached) {
      try {
        const value = JSON.parse(cached) as { json: string; isError: boolean };
        await submitAgentToolResult(call.runId, call.toolUseId, value.json, value.isError);
        return;
      } catch {
        window.localStorage.removeItem(key);
      }
    }

    let result: { json: string; isError: boolean };
    try {
      result = await executeCanvasTool(call);
    } catch (error) {
      result = {
        json: JSON.stringify({
          status: "error",
          error: error instanceof Error ? error.message : "canvas_tool_failed",
        }),
        isError: true,
      };
    }

    // Written before submission: if the connection drops between execution and invoke, a resend of
    // the same toolUseId returns this result rather than mutating the canvas a second time.
    window.localStorage.setItem(key, JSON.stringify(result));
    await submitAgentToolResult(call.runId, call.toolUseId, result.json, result.isError);
  } finally {
    activeToolCalls.delete(executionKey);
  }
}
