"use client";

import { ArrowRight, Film, ImageIcon, Type, Wand2, Workflow } from "lucide-react";
import { useT } from "@/components/providers/i18n-provider";
import { Dialog, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { CANVAS_TEMPLATES, type CanvasTemplateDefinition } from "@/features/canvas/templates";
import type { NodeKind } from "@/features/canvas/types";
import { cn } from "@/lib/utils";

const NODE_ICONS: Record<NodeKind, typeof Type> = {
  text: Type,
  image: ImageIcon,
  video: Film,
  imageGen: Wand2,
  videoGen: Film,
};

function nodeLabel(kind: NodeKind, t: ReturnType<typeof useT>): string {
  switch (kind) {
    case "text":
      return t("canvas.nodeText");
    case "image":
      return t("canvas.nodeImage");
    case "video":
      return t("canvas.nodeVideo");
    case "imageGen":
      return t("canvas.nodeImageGen");
    case "videoGen":
      return t("canvas.nodeVideoGen");
  }
}

export function WorkflowTemplateDialog({
  isOpen,
  onOpenChange,
  onSelect,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  onSelect: (template: CanvasTemplateDefinition) => void;
}) {
  const t = useT();

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      className="w-[min(44rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("canvas.templates.title")}</DialogTitle>
        <DialogDescription>{t("canvas.templates.description")}</DialogDescription>
      </DialogHeader>

      <div className="grid max-h-[60vh] gap-3 overflow-y-auto sm:grid-cols-2">
        {CANVAS_TEMPLATES.map((template) => (
          <button
            key={template.id}
            type="button"
            onClick={() => {
              onSelect(template);
              onOpenChange(false);
            }}
            className="group flex flex-col gap-3 rounded-xl border bg-card p-3 text-left transition-colors hover:border-ring hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <div className="flex items-start justify-between gap-2">
              <div>
                <h3 className="text-sm font-medium">{t(template.nameKey)}</h3>
                <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
                  {t(template.descriptionKey)}
                </p>
              </div>
              <Workflow className="size-4 shrink-0 text-muted-foreground transition-colors group-hover:text-foreground" aria-hidden />
            </div>

            <div className="flex flex-wrap items-center gap-1.5">
              {template.nodes.map((node, index) => {
                const Icon = NODE_ICONS[node.kind];
                return (
                  <div key={node.key} className="contents">
                    {index > 0 && <ArrowRight className="size-3 shrink-0 text-muted-foreground" aria-hidden />}
                    <span
                      className={cn(
                        "inline-flex items-center gap-1 rounded-md border bg-background px-1.5 py-1 text-[0.625rem]",
                        node.kind.endsWith("Gen") && "border-primary/30 bg-primary/5",
                      )}
                    >
                      <Icon className="size-3 text-muted-foreground" aria-hidden />
                      {nodeLabel(node.kind, t)}
                    </span>
                  </div>
                );
              })}
            </div>

            <p className="mt-auto flex items-center gap-1 text-[0.625rem] text-muted-foreground">
              <Workflow className="size-3" aria-hidden />
              {t("canvas.templates.connectedEdges", { count: template.edges.length })}
            </p>
          </button>
        ))}
      </div>
    </Dialog>
  );
}

