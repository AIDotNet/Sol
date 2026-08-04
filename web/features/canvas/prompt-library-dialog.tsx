"use client";

import { BookOpen } from "lucide-react";
import { useState } from "react";
import { useI18n, useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  appendPrompt,
  PROMPT_CATEGORY_LABELS,
  promptsByCategory,
} from "@/features/canvas/prompt-library";
import { useCanvasStore } from "@/features/canvas/store";
import { cn } from "@/lib/utils";

/**
 * Inserts curated prompt fragments into a text node.
 *
 * Multi-select and additive: the entries are modifiers, so a user picks several and keeps their
 * own subject. Selections are applied on confirm rather than on click, so the dialog can be
 * browsed without committing.
 */
export function PromptLibraryDialog({
  isOpen,
  onOpenChange,
  targetNodeId,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  /** Node to insert into. When null, a new text node is created. */
  targetNodeId: string | null;
}) {
  const t = useT();
  const { locale } = useI18n();

  const nodes = useCanvasStore((state) => state.nodes);
  const updateNodeData = useCanvasStore((state) => state.updateNodeData);
  const addNode = useCanvasStore((state) => state.addNode);

  const [selected, setSelected] = useState<string[]>([]);

  function toggle(text: string) {
    setSelected((current) =>
      current.includes(text) ? current.filter((value) => value !== text) : [...current, text],
    );
  }

  function apply() {
    if (selected.length === 0) {
      onOpenChange(false);
      return;
    }

    if (targetNodeId) {
      const node = nodes.find((candidate) => candidate.id === targetNodeId);
      const current = ((node?.data as { text?: string } | undefined)?.text ?? "").trim();

      updateNodeData(
        targetNodeId,
        { text: selected.reduce((text, addition) => appendPrompt(text, addition), current) },
      );
    } else {
      // Placed near the origin; the user is about to drag it anyway.
      addNode("text", { x: 0, y: 0 }, { text: selected.join(", ") });
    }

    setSelected([]);
    onOpenChange(false);
  }

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={(open) => {
        onOpenChange(open);
        if (!open) setSelected([]);
      }}
      className="w-[min(40rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("canvas.promptLibrary")}</DialogTitle>
        <DialogDescription>{t("canvas.promptLibraryHint")}</DialogDescription>
      </DialogHeader>

      <div className="flex max-h-[55vh] flex-col gap-4 overflow-y-auto">
        {promptsByCategory().map(([category, entries]) => (
          <section key={category} className="flex flex-col gap-1.5">
            <h3 className="text-xs font-medium text-muted-foreground">
              {PROMPT_CATEGORY_LABELS[category][locale === "zh" ? "zh" : "en"]}
            </h3>

            <div className="flex flex-wrap gap-1.5">
              {entries.map((entry) => (
                <button
                  key={entry.id}
                  type="button"
                  onClick={() => toggle(entry.text)}
                  // The English text is what gets inserted, so it is worth seeing on hover.
                  title={entry.text}
                  className={cn(
                    "rounded-md border px-2 py-1 text-xs transition-colors",
                    selected.includes(entry.text)
                      ? "border-primary bg-primary text-primary-foreground"
                      : "border-border hover:bg-accent",
                  )}
                >
                  {entry.label}
                </button>
              ))}
            </div>
          </section>
        ))}
      </div>

      <DialogFooter>
        <span className="mr-auto text-xs text-muted-foreground">
          {selected.length > 0 ? t("canvas.promptSelected", { count: selected.length }) : ""}
        </span>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        <Button size="sm" isDisabled={selected.length === 0} onPress={apply}>
          <BookOpen className="size-3.5" aria-hidden />
          {t("common.add")}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}
