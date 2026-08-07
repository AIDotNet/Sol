"use client";

import { Check, ChevronDown, FolderOpen, Loader2, Pencil, Plus, Trash2 } from "lucide-react";
import { type Dispatch, type SetStateAction, useEffect, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import * as api from "@/features/canvas/api";
import { clearLocal } from "@/features/canvas/persistence";
import { cn } from "@/lib/utils";

/**
 * Switches between the projects on this device, and creates, renames and deletes them.
 *
 * The server has supported many canvases per device from the start, but nothing in the UI
 * reached that: the page opened whichever canvas it happened to resolve and offered no way to
 * leave it, so `renameCanvas` and `deleteCanvas` had no call sites at all. This is that missing
 * surface.
 *
 * The list is owned by the caller rather than fetched here, because the page already loads it
 * during bootstrap to decide what to open — refetching on mount would duplicate that request
 * and leave the two copies free to disagree. It is refreshed when the menu opens, which is the
 * only moment the user can act on it, and cheap: the endpoint counts nodes in SQL and never
 * transfers a graph body.
 */
export function ProjectMenu({
  currentId,
  projects,
  onProjectsChange,
  onSwitch,
}: {
  currentId: string;
  projects: api.CanvasSummary[];
  /** Must be referentially stable — it drives the refresh effect. Pass a state setter. */
  onProjectsChange: Dispatch<SetStateAction<api.CanvasSummary[]>>;
  onSwitch: (id: string) => void;
}) {
  const t = useT();

  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [draftName, setDraftName] = useState("");
  const [pendingDelete, setPendingDelete] = useState<api.CanvasSummary | null>(null);

  // Which row the rename field belongs to, claimed by whichever handler commits or abandons it
  // first. Both Enter and Escape unmount the field, and unmounting a focused input fires blur —
  // so without the claim the blur handler runs a second time behind them, re-sending a rename
  // that was already sent or reviving one the user just abandoned.
  const editing = useRef<string | null>(null);

  const current = projects.find((project) => project.id === currentId);

  // Another tab may have added or renamed something since bootstrap. A failure here is silent:
  // the list already on screen is still usable, and an error banner over a stale-by-seconds
  // list would be worse than the staleness.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;

    void (async () => {
      try {
        const { canvases } = await api.listCanvases();
        if (!cancelled) onProjectsChange(canvases);
      } catch {
        // Keep what we have.
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [open, onProjectsChange]);

  /** Runs a mutation, surfacing failures in the menu instead of throwing into the render tree. */
  async function run<T>(action: () => Promise<T>): Promise<T | null> {
    setBusy(true);
    setError(null);

    try {
      return await action();
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : t("errors.unknown"));
      return null;
    } finally {
      setBusy(false);
    }
  }

  async function create() {
    const created = await run(() =>
      api.createCanvas(nextName(projects, t("canvas.defaultProject"))),
    );
    if (!created) return;

    onProjectsChange((list) => [api.summarize(created), ...list]);
    setOpen(false);
    onSwitch(created.id);
  }

  function beginRename(project: api.CanvasSummary) {
    editing.current = project.id;
    setDraftName(project.name);
    setRenamingId(project.id);
  }

  function abandonRename() {
    editing.current = null;
    setRenamingId(null);
  }

  async function commitRename(id: string) {
    if (editing.current !== id) return;
    editing.current = null;

    const name = draftName.trim();
    setRenamingId(null);

    // An empty field means "never mind" rather than "clear the name" — the server would only
    // substitute its own placeholder anyway.
    if (!name || name === projects.find((project) => project.id === id)?.name) return;

    const updated = await run(() => api.renameCanvas(id, name));
    if (!updated) return;

    onProjectsChange((list) =>
      list.map((project) => (project.id === id ? { ...project, name: updated.name } : project)),
    );
  }

  async function remove(project: api.CanvasSummary) {
    const remaining = await run(async () => {
      await api.deleteCanvas(project.id);

      const left = projects.filter((candidate) => candidate.id !== project.id);
      if (left.length > 0) return left;

      // Deleting the last project would leave the page with nothing to open, and the bootstrap
      // that would have created a replacement only runs on load.
      return [api.summarize(await api.createCanvas(t("canvas.defaultProject")))];
    });

    if (!remaining) return;

    // The cached copy outlives the server row otherwise, holding a deleted graph in
    // localStorage until the browser is cleared.
    clearLocal(project.id);
    onProjectsChange(remaining);

    if (project.id === currentId) onSwitch(remaining[0].id);
  }

  return (
    <div className="relative">
      <Button
        size="sm"
        variant="ghost"
        className="h-7 w-full justify-start gap-1.5 px-2"
        aria-expanded={open}
        aria-haspopup="menu"
        onPress={() => setOpen((value) => !value)}
      >
        <FolderOpen className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
        <span className="min-w-0 flex-1 truncate text-left text-xs">
          {current?.name ?? t("canvas.untitled")}
        </span>
        <ChevronDown className="size-3 shrink-0 text-muted-foreground" aria-hidden />
      </Button>

      {open && (
        <>
          {/* Click-away layer. Sits above the canvas so a click outside closes the menu rather
              than landing on the graph underneath. */}
          <div className="fixed inset-0 z-20" onClick={() => setOpen(false)} />

          <div className="canvas-menu absolute top-full left-0 z-30 mt-1 flex w-64 flex-col rounded-lg border bg-popover p-1 shadow-lg">
            <div className="max-h-72 overflow-y-auto">
              {projects.map((project) =>
                renamingId === project.id ? (
                  <div key={project.id} className="p-1">
                    <Input
                      autoFocus
                      aria-label={t("canvas.projectNamePlaceholder")}
                      placeholder={t("canvas.projectNamePlaceholder")}
                      value={draftName}
                      className="h-7 text-xs"
                      onChange={(event) => setDraftName(event.target.value)}
                      onBlur={() => void commitRename(project.id)}
                      onKeyDown={(event) => {
                        if (event.key === "Enter") {
                          event.preventDefault();
                          void commitRename(project.id);
                        } else if (event.key === "Escape") {
                          event.preventDefault();
                          abandonRename();
                        }
                      }}
                    />
                  </div>
                ) : (
                  <div
                    key={project.id}
                    className={cn(
                      "group flex items-center rounded-md pr-1 hover:bg-accent",
                      project.id === currentId && "bg-accent/60",
                    )}
                  >
                    <button
                      type="button"
                      className="flex min-w-0 flex-1 items-center gap-2 px-2 py-1.5 text-left"
                      onClick={() => {
                        setOpen(false);
                        if (project.id !== currentId) onSwitch(project.id);
                      }}
                    >
                      <Check
                        className={cn(
                          "size-3.5 shrink-0",
                          project.id === currentId ? "text-foreground" : "invisible",
                        )}
                        aria-hidden
                      />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-xs">{project.name}</span>
                        <span className="block text-[0.6875rem] text-muted-foreground">
                          {t("canvas.projectNodes", { count: project.nodeCount })}
                        </span>
                      </span>
                    </button>

                    <RowAction
                      icon={Pencil}
                      label={t("canvas.projectRename")}
                      onClick={() => beginRename(project)}
                    />
                    <RowAction
                      icon={Trash2}
                      label={t("canvas.projectDelete")}
                      destructive
                      onClick={() => {
                        // Closing first keeps the click-away layer from covering the dialog.
                        setOpen(false);
                        setPendingDelete(project);
                      }}
                    />
                  </div>
                ),
              )}
            </div>

            {error && (
              <p className="px-2 py-1 text-[0.6875rem] text-destructive" role="alert">
                {error}
              </p>
            )}

            <div className="my-1 h-px bg-border" />

            <button
              type="button"
              disabled={busy}
              onClick={() => void create()}
              className="flex items-center gap-2 rounded-md px-2 py-1.5 text-left text-xs hover:bg-accent disabled:opacity-50"
            >
              {busy ? (
                <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-hidden />
              ) : (
                <Plus className="size-3.5 text-muted-foreground" aria-hidden />
              )}
              {t("canvas.newProject")}
            </button>
          </div>
        </>
      )}

      <ConfirmDialog
        isOpen={pendingDelete !== null}
        onOpenChange={(isOpen) => !isOpen && setPendingDelete(null)}
        title={t("canvas.projectDelete")}
        description={
          pendingDelete
            ? t("canvas.projectDeleteConfirm", { name: pendingDelete.name })
            : undefined
        }
        onConfirm={() => (pendingDelete ? remove(pendingDelete) : undefined)}
      />
    </div>
  );
}

function RowAction({
  icon: Icon,
  label,
  onClick,
  destructive,
}: {
  icon: typeof Pencil;
  label: string;
  onClick: () => void;
  destructive?: boolean;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      // Hidden until the row is hovered or the button is tabbed to, so the list reads as names
      // rather than as a grid of icons.
      className={cn(
        "rounded-md p-1 text-muted-foreground opacity-0 transition-opacity hover:bg-background focus-visible:opacity-100 group-hover:opacity-100",
        destructive && "hover:text-destructive",
      )}
    >
      <Icon className="size-3.5" aria-hidden />
    </button>
  );
}

/**
 * Picks a name no existing project is using: `base`, then `base 2`, `base 3`…
 *
 * Without it a user clicking "new project" three times gets three identically named entries and
 * no way to tell them apart in this very list.
 */
function nextName(projects: api.CanvasSummary[], base: string): string {
  const taken = new Set(projects.map((project) => project.name));
  if (!taken.has(base)) return base;

  let suffix = 2;
  while (taken.has(`${base} ${suffix}`)) suffix += 1;

  return `${base} ${suffix}`;
}
