"use client";

import { Handle, Position } from "@xyflow/react";
import { AlertTriangle, Loader2, Square } from "lucide-react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipTrigger } from "@/components/ui/tooltip";
import { ConnectBurst } from "@/features/canvas/connection-effects";
import type { NodeExecution } from "@/features/canvas/types";
import { cn } from "@/lib/utils";

/**
 * Shared chrome for every node: the frame, the two connection handles, and execution feedback.
 *
 * Handles are plain left/right dots with no ids, matching OpenCowork — a node has one input and
 * one output, and typing the ports would add rules users would have to learn for no gain here.
 */
export function NodeShell({
  title,
  icon,
  selected,
  execution,
  children,
  actions,
  className,
  onRetry,
  onCancel,
}: {
  title: string;
  icon?: React.ReactNode;
  selected?: boolean;
  execution?: NodeExecution;
  children: React.ReactNode;
  actions?: React.ReactNode;
  className?: string;
  onRetry?: () => void;
  /**
   * Stops the run this node belongs to.
   *
   * Passed by the nodes a run creates, not by the node that started it: the spinner is here, so
   * the button that ends it belongs here too.
   */
  onCancel?: () => void;
}) {
  const t = useT();
  const busy = execution?.status === "queued" || execution?.status === "running";
  const statusLabel = execution?.status === "queued" ? t("node.queued") : t("node.generating");
  const progress =
    typeof execution?.progress === "number" && Number.isFinite(execution.progress)
      ? Math.round(Math.min(1, Math.max(0, execution.progress)) * 100)
      : undefined;

  return (
    <>
      <div
        className={cn(
          "canvas-node relative flex h-full w-full flex-col overflow-hidden rounded-xl border bg-card shadow-sm",
          selected ? "border-ring shadow-md ring-2 ring-ring/40" : "border-border hover:shadow-md",
          busy && "canvas-node-busy",
          className,
        )}
      >
        <Handle
          type="target"
          position={Position.Left}
          className="!size-2.5 !border-2 !border-background !bg-muted-foreground transition-transform duration-150 hover:!scale-125"
        />

        <header className="flex shrink-0 items-center gap-1.5 border-b bg-muted/30 px-2 py-1.5">
          {icon}
          <span className="min-w-0 flex-1 truncate text-[0.6875rem] font-medium">{title}</span>
          {/* nodrag keeps a click on a button from starting a canvas drag. */}
          {actions && <div className="nodrag flex shrink-0 items-center gap-0.5">{actions}</div>}
        </header>

        <div className="relative flex min-h-0 flex-1 flex-col">
          {children}

          {busy && (
            <div className="canvas-sweep canvas-fade-in nodrag absolute inset-0 flex flex-col items-center justify-center gap-2 overflow-hidden bg-background/70 backdrop-blur-[1px]">
              <Loader2 className="size-5 animate-spin text-muted-foreground" aria-hidden />
              <p className="flex items-center gap-1.5 text-[0.6875rem] text-muted-foreground">
                <span>{statusLabel}</span>
                {progress !== undefined && (
                  <span className="font-medium tabular-nums text-foreground">{progress}%</span>
                )}
              </p>
              {progress !== undefined && (
                <div
                  role="progressbar"
                  aria-label={statusLabel}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={progress}
                  className="h-1 w-24 overflow-hidden rounded-full bg-muted"
                >
                  <div
                    className="h-full bg-primary transition-[width] duration-300 ease-out"
                    style={{ width: `${progress}%` }}
                  />
                </div>
              )}
              {onCancel && (
                <Button
                  size="xs"
                  variant="outline"
                  className="mt-0.5 h-5 px-2 text-[0.625rem]"
                  onPress={onCancel}
                >
                  <Square className="size-2.5" aria-hidden />
                  {t("node.stop")}
                </Button>
              )}
            </div>
          )}
        </div>

        {(execution?.status === "failed" ||
          execution?.status === "interrupted" ||
          execution?.status === "cancelled") && (
          <div className="canvas-fade-in nodrag flex shrink-0 items-start gap-1.5 border-t bg-destructive/5 px-2 py-1.5">
            <AlertTriangle className="mt-px size-3 shrink-0 text-destructive" aria-hidden />
            <p className="min-w-0 flex-1 text-[0.625rem] leading-tight text-destructive">
              {execution.status === "interrupted"
                ? t("node.interrupted")
                : execution.status === "cancelled"
                  ? t("node.stop")
                  : (execution.error ?? t("node.failed"))}
            </p>
            {onRetry && (
              <Button size="xs" variant="ghost" className="h-4 px-1 text-[0.625rem]" onPress={onRetry}>
                {t("node.retry")}
              </Button>
            )}
          </div>
        )}

        <Handle
          type="source"
          position={Position.Right}
          className="!size-2.5 !border-2 !border-background !bg-muted-foreground transition-transform duration-150 hover:!scale-125"
        />
      </div>
      <ConnectBurst />
    </>
  );
}

/**
 * An icon-only action button in a node's header, with a tooltip carrying the label that would
 * otherwise only reach a screen reader — the node body is too cramped for visible text.
 */
export function NodeActionButton({
  label,
  ...props
}: React.ComponentProps<typeof Button> & { label: string }) {
  return (
    <TooltipTrigger>
      <Button aria-label={label} {...props} />
      <Tooltip>{label}</Tooltip>
    </TooltipTrigger>
  );
}

