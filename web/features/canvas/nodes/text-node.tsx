"use client";

import { type NodeProps, NodeResizer, useReactFlow } from "@xyflow/react";
import { Loader2, Minus, Plus, Sparkles, Type, Wand2 } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { resolveSlot, useAiStore } from "@/features/ai/store";
import { NodeActionButton, NodeShell } from "@/features/canvas/nodes/node-shell";
import { useCanvasStore } from "@/features/canvas/store";
import { generateText, spawnTextResult, type TextTask } from "@/features/canvas/text-generation";
import { NODE_MIN_SIZE, type TextNodeData } from "@/features/canvas/types";

const FONT_STEP = 0.15;
const FONT_RANGE = { min: 0.7, max: 2.2 };

export function TextNode({ id, data, selected }: NodeProps) {
  const t = useT();
  const updateNodeData = useCanvasStore((state) => state.updateNodeData);
  const { getNodes, getEdges } = useReactFlow();

  const providers = useAiStore((state) => state.providers);
  const chatSlot = useAiStore((state) => state.slots.chat);

  const [busy, setBusy] = useState<TextTask | null>(null);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const nodeData = data as unknown as TextNodeData;
  const fontScale = nodeData.fontScale ?? 1;
  const storedText = nodeData.text ?? "";

  /**
   * The textarea is driven by local state rather than straight off the store.
   *
   * A stored value round-trips through zustand and React Flow's own reconciliation before it
   * comes back down as `data`. Re-applying `value` to a textarea mid-composition destroys the
   * IME's composing buffer, so typing pinyin committed the raw latin letters instead of the
   * characters they were composing into. A local draft means the element is never handed a value
   * it did not just produce.
   */
  const [draft, setDraft] = useState(storedText);
  const composing = useRef(false);

  useEffect(() => {
    // Undo, an AI rewrite, or a prompt-library insert changes the text from outside. A
    // composition in progress belongs to the user, so it wins until it commits.
    if (!composing.current) setDraft(storedText);
  }, [storedText]);

  function commit(value: string) {
    setDraft(value);
    // Mid-composition the store stays on the last committed text: writing every intermediate
    // keystroke is what fed the broken value back into the element.
    if (!composing.current) updateNodeData(id, { text: value });
  }

  // A chat model must be configured for either action to do anything.
  const hasChatModel = resolveSlot(providers, chatSlot) !== null;
  const hasText = Boolean(draft.trim());

  function adjustFont(delta: number) {
    const next = Math.min(FONT_RANGE.max, Math.max(FONT_RANGE.min, fontScale + delta));
    updateNodeData(id, { fontScale: Number(next.toFixed(2)) });
  }

  async function run(task: TextTask) {
    if (busy) return;

    setBusy(task);
    setError(null);

    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    const result = await generateText(id, task, getNodes(), getEdges(), controller.signal);

    if (result.ok && result.text) {
      // Rewriting replaces this node's text; writing produces a new node, so the source
      // material stays on the canvas next to its result.
      if (task === "rewrite") {
        updateNodeData(id, { text: result.text });
      } else {
        spawnTextResult(id, result.text);
      }
    } else if (result.error && result.error !== "aborted") {
      setError(
        result.error === "no-chat-model"
          ? t("node.noChatModel")
          : result.error === "empty"
            ? t("node.textEmpty")
            : result.error,
      );
    }

    setBusy(null);
  }

  return (
    <>
      <NodeResizer
        isVisible={selected}
        minWidth={NODE_MIN_SIZE.width}
        minHeight={NODE_MIN_SIZE.height}
        lineClassName="!border-ring"
        handleClassName="!size-2 !rounded-sm !border-ring !bg-background"
      />

      <NodeShell
        title={t("canvas.nodeText")}
        icon={<Type className="size-3 shrink-0 text-muted-foreground" aria-hidden />}
        selected={selected}
        actions={
          <>
            <NodeActionButton
              size="icon-xs"
              variant="ghost"
              className="size-5"
              onPress={() => adjustFont(-FONT_STEP)}
              label="Decrease font size"
            >
              <Minus className="size-2.5" aria-hidden />
            </NodeActionButton>
            <NodeActionButton
              size="icon-xs"
              variant="ghost"
              className="size-5"
              onPress={() => adjustFont(FONT_STEP)}
              label="Increase font size"
            >
              <Plus className="size-2.5" aria-hidden />
            </NodeActionButton>
          </>
        }
      >
        {/* nodrag and nowheel: without them a drag inside the textarea would pan the canvas and
            a scroll would zoom it, making long text unusable. */}
        <textarea
          value={draft}
          onChange={(event) => commit(event.target.value)}
          onCompositionStart={() => {
            composing.current = true;
          }}
          onCompositionEnd={(event) => {
            // Browsers disagree on whether `input` fires before or after `compositionend`, so
            // commit from both — whichever runs second writes the same value.
            composing.current = false;
            commit(event.currentTarget.value);
          }}
          placeholder={t("node.textPlaceholder")}
          aria-label={t("canvas.nodeText")}
          className="nodrag nowheel min-h-0 flex-1 resize-none bg-transparent p-2.5 leading-relaxed outline-none placeholder:text-muted-foreground"
          style={{ fontSize: `${fontScale * 0.8125}rem` }}
        />

        {hasChatModel && (
          <div className="nodrag flex shrink-0 items-center gap-1 border-t p-1.5">
            <Button
              size="xs"
              variant="outline"
              className="flex-1 text-[0.625rem]"
              isDisabled={busy !== null}
              onPress={() => void run("write")}
            >
              {busy === "write" ? (
                <Loader2 className="size-2.5 animate-spin" aria-hidden />
              ) : (
                <Sparkles className="size-2.5" aria-hidden />
              )}
              {t("node.writePrompt")}
            </Button>

            <Button
              size="xs"
              variant="outline"
              className="flex-1 text-[0.625rem]"
              isDisabled={busy !== null || !hasText}
              onPress={() => void run("rewrite")}
            >
              {busy === "rewrite" ? (
                <Loader2 className="size-2.5 animate-spin" aria-hidden />
              ) : (
                <Wand2 className="size-2.5" aria-hidden />
              )}
              {t("node.rewrite")}
            </Button>
          </div>
        )}

        {error && (
          <div className="nodrag shrink-0 border-t bg-destructive/5 px-2 py-1.5">
            <p className="text-[0.625rem] leading-tight text-destructive">{error}</p>
          </div>
        )}
      </NodeShell>
    </>
  );
}
