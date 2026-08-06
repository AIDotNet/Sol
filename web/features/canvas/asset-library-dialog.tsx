"use client";

import {
  Check,
  Folder,
  FolderOpen,
  FolderPlus,
  ImageIcon,
  Loader2,
  Pencil,
  Trash2,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Dialog, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import * as api from "@/features/canvas/api";
import { useCanvasStore } from "@/features/canvas/store";
import { NODE_DEFAULT_SIZE } from "@/features/canvas/types";
import { cn } from "@/lib/utils";

/** Sentinels used by the navigation rail; UUIDs returned by the API cannot collide with these. */
const ALL_ASSETS = "__all__";
const UNGROUPED_ASSETS = "__ungrouped__";

/**
 * Everything this device has generated or uploaded, for reuse on the canvas.
 *
 * Groups are folders rather than a second copy of the media. The server owns the assignment, so
 * a group survives a reload and is shared by every canvas on this device. Selecting several
 * tiles and choosing a folder performs one bulk move; dragging a tile onto a folder is the quick
 * path for the common one-at-a-time case.
 */
export function AssetLibraryDialog({
  onOpenChange,
  at,
}: {
  onOpenChange: (open: boolean) => void;
  /** Where to place inserted nodes, in flow coordinates. */
  at: () => { x: number; y: number };
}) {
  const t = useT();
  const addNode = useCanvasStore((state) => state.addNode);

  const [assets, setAssets] = useState<api.CanvasAssetSummary[]>([]);
  const [groups, setGroups] = useState<api.CanvasAssetGroupSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [activeGroupId, setActiveGroupId] = useState(ALL_ASSETS);
  const [deletingId, setDeletingId] = useState<string | null>(null);
  const [pendingDeleteGroup, setPendingDeleteGroup] =
    useState<api.CanvasAssetGroupSummary | null>(null);

  const [creatingGroup, setCreatingGroup] = useState(false);
  const [newGroupName, setNewGroupName] = useState("");
  const [editingGroupId, setEditingGroupId] = useState<string | null>(null);
  const [editingGroupName, setEditingGroupName] = useState("");
  const editingGroup = useRef<string | null>(null);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [moveTarget, setMoveTarget] = useState("");
  const [draggedAssetId, setDraggedAssetId] = useState<string | null>(null);
  const [dragOverGroupId, setDragOverGroupId] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      setLoading(true);
      setError(null);

      const [assetResult, groupResult] = await Promise.allSettled([
        api.listAssets("image"),
        api.listAssetGroups(),
      ]);

      if (cancelled) return;

      if (assetResult.status === "fulfilled") {
        setAssets(assetResult.value.assets);
      } else {
        setError(
          assetResult.reason instanceof Error ? assetResult.reason.message : t("errors.unknown"),
        );
      }

      if (groupResult.status === "fulfilled") {
        setGroups(groupResult.value.groups);
      } else if (assetResult.status === "fulfilled") {
        // A stale server without the optional group migration should not make the existing
        // picker unusable. The next save/reload will surface the migration problem explicitly.
        setError(
          groupResult.reason instanceof Error ? groupResult.reason.message : t("errors.unknown"),
        );
      }

      setLoading(false);
    })();

    return () => {
      cancelled = true;
    };
  }, [t]);

  const visibleAssets = useMemo(() => {
    if (activeGroupId === ALL_ASSETS) return assets;
    if (activeGroupId === UNGROUPED_ASSETS) {
      return assets.filter((asset) => !asset.groupId);
    }
    return assets.filter((asset) => asset.groupId === activeGroupId);
  }, [activeGroupId, assets]);

  const activeGroup = groups.find((group) => group.id === activeGroupId);
  const ungroupedCount = assets.filter((asset) => !asset.groupId).length;

  function toggle(id: string) {
    setSelected((current) =>
      current.includes(id) ? current.filter((value) => value !== id) : [...current, id],
    );
  }

  function insert() {
    const origin = at();
    const chosen = assets.filter((asset) => selected.includes(asset.id));

    chosen.forEach((asset, index) => {
      addNode(
        "image",
        {
          x: origin.x + index * (NODE_DEFAULT_SIZE.image.width + 24),
          y: origin.y,
        },
        { assetUrl: asset.url, mediaType: asset.mediaType, prompt: asset.prompt ?? undefined },
      );
    });

    onOpenChange(false);
  }

  async function remove(id: string) {
    if (busyAction) return;
    const removed = assets.find((asset) => asset.id === id);
    setBusyAction(`asset:${id}`);

    try {
      await api.deleteAsset(id);
      setAssets((current) => current.filter((asset) => asset.id !== id));
      setSelected((current) => current.filter((value) => value !== id));
      if (removed?.groupId) {
        setGroups((current) =>
          current.map((group) =>
            group.id === removed.groupId
              ? { ...group, assetCount: Math.max(0, group.assetCount - 1) }
              : group,
          ),
        );
      }
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : t("errors.unknown"));
    } finally {
      setBusyAction(null);
    }
  }

  /** Applies a move optimistically, then restores both lists if the API rejects it. */
  async function moveAssets(ids: string[], groupId: string | null) {
    if (ids.length === 0 || busyAction) return;

    const previousAssets = assets;
    const previousGroups = groups;
    const moving = new Set(ids);
    const nextAssets = assets.map((asset) =>
      moving.has(asset.id) ? { ...asset, groupId } : asset,
    );
    const nextGroups = groups.map((group) => {
      let delta = 0;
      for (const id of ids) {
        const previous = assets.find((asset) => asset.id === id)?.groupId ?? null;
        if (previous === group.id && groupId !== group.id) delta -= 1;
        if (groupId === group.id && previous !== group.id) delta += 1;
      }
      return delta === 0
        ? group
        : { ...group, assetCount: Math.max(0, group.assetCount + delta) };
    });

    setAssets(nextAssets);
    setGroups(nextGroups);
    setMoveTarget("");
    setBusyAction("assign");
    setError(null);

    try {
      await api.assignAssetsToGroup(ids, groupId);
    } catch (moveError) {
      setAssets(previousAssets);
      setGroups(previousGroups);
      setError(moveError instanceof Error ? moveError.message : t("errors.unknown"));
    } finally {
      setBusyAction(null);
    }
  }

  async function createGroup() {
    const name = newGroupName.trim();
    if (!name) {
      setError(t("canvas.assetGroupNameRequired"));
      return;
    }

    setBusyAction("create-group");
    setError(null);
    try {
      const created = await api.createAssetGroup(name);
      setGroups((current) => [...current, created]);
      setActiveGroupId(created.id);
      setNewGroupName("");
      setCreatingGroup(false);
    } catch (createError) {
      setError(
        createError instanceof api.CanvasApiError && createError.status === 409
          ? t("canvas.assetGroupNameDuplicate")
          : createError instanceof Error
            ? createError.message
            : t("errors.unknown"),
      );
    } finally {
      setBusyAction(null);
    }
  }

  function beginRename(group: api.CanvasAssetGroupSummary) {
    editingGroup.current = group.id;
    setEditingGroupId(group.id);
    setEditingGroupName(group.name);
  }

  function abandonRename() {
    editingGroup.current = null;
    setEditingGroupId(null);
    setEditingGroupName("");
  }

  async function commitRename(id: string) {
    if (editingGroup.current !== id || busyAction) return;
    editingGroup.current = null;
    const name = editingGroupName.trim();
    setEditingGroupId(null);
    setEditingGroupName("");

    const current = groups.find((group) => group.id === id);
    if (!name || !current || name === current.name) return;

    setBusyAction(`rename:${id}`);
    setError(null);
    try {
      const updated = await api.renameAssetGroup(id, name);
      setGroups((list) => list.map((group) => (group.id === id ? updated : group)));
    } catch (renameError) {
      setError(
        renameError instanceof api.CanvasApiError && renameError.status === 409
          ? t("canvas.assetGroupNameDuplicate")
          : renameError instanceof Error
            ? renameError.message
            : t("errors.unknown"),
      );
    } finally {
      setBusyAction(null);
    }
  }

  async function removeGroup(group: api.CanvasAssetGroupSummary) {
    if (busyAction) return;
    setBusyAction(`delete-group:${group.id}`);
    setError(null);

    try {
      await api.deleteAssetGroup(group.id);
      setGroups((current) => current.filter((candidate) => candidate.id !== group.id));
      setAssets((current) =>
        current.map((asset) =>
          asset.groupId === group.id ? { ...asset, groupId: null } : asset,
        ),
      );
      if (activeGroupId === group.id) setActiveGroupId(UNGROUPED_ASSETS);
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : t("errors.unknown"));
    } finally {
      setBusyAction(null);
      setPendingDeleteGroup(null);
    }
  }

  function dropOnGroup(event: React.DragEvent, groupId: string | null) {
    event.preventDefault();
    setDragOverGroupId(null);
    const id = event.dataTransfer.getData("text/asset-id") || draggedAssetId;
    if (!id) return;
    void moveAssets(selected.includes(id) ? selected : [id], groupId);
    setDraggedAssetId(null);
  }

  const activeLabel =
    activeGroupId === ALL_ASSETS
      ? t("canvas.assetAll")
      : activeGroupId === UNGROUPED_ASSETS
        ? t("canvas.assetUngrouped")
        : activeGroup?.name ?? t("canvas.assetAll");

  return (
    <>
      <Dialog
        isOpen
        onOpenChange={onOpenChange}
        className="h-[min(44rem,calc(100vh-2rem))] w-[min(60rem,calc(100vw-2rem))] max-w-none gap-0 overflow-hidden p-0 sm:max-w-none"
      >
        <div className="flex h-full min-h-0 flex-col">
          <DialogHeader className="border-b p-4 pr-12">
            <DialogTitle>{t("canvas.assetLibrary")}</DialogTitle>
            <DialogDescription>
              {t("canvas.assetLibraryHint")} {t("canvas.assetGroupDragHint")}
            </DialogDescription>
          </DialogHeader>

          {error && (
            <p className="mx-4 mt-3 rounded-md border border-destructive/30 bg-destructive/5 px-2.5 py-2 text-xs text-destructive" role="alert">
              {error}
            </p>
          )}

          <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-hidden p-4 sm:flex-row sm:gap-4">
          <aside className="flex max-h-44 w-full shrink-0 flex-col border-b pb-3 sm:max-h-none sm:w-48 sm:border-r sm:border-b-0 sm:pr-3 sm:pb-0">
            <div className="flex flex-col gap-1">
              <LibraryNavButton
                icon={ImagesIcon}
                label={t("canvas.assetAll")}
                count={assets.length}
                active={activeGroupId === ALL_ASSETS}
                onPress={() => setActiveGroupId(ALL_ASSETS)}
              />
              <LibraryNavButton
                icon={FolderOpen}
                label={t("canvas.assetUngrouped")}
                count={ungroupedCount}
                active={activeGroupId === UNGROUPED_ASSETS}
                onPress={() => setActiveGroupId(UNGROUPED_ASSETS)}
                dropTarget
                isDragOver={dragOverGroupId === UNGROUPED_ASSETS}
                onDragOver={(event) => {
                  event.preventDefault();
                  setDragOverGroupId(UNGROUPED_ASSETS);
                }}
                onDragLeave={() => setDragOverGroupId(null)}
                onDrop={(event) => dropOnGroup(event, null)}
              />
            </div>

            <div className="mt-4 flex min-h-0 flex-1 flex-col">
              <div className="mb-1 flex items-center justify-between px-1">
                <span className="text-[0.6875rem] font-medium tracking-wide text-muted-foreground uppercase">
                  {t("canvas.assetGroups")}
                </span>
                <Button
                  size="icon-xs"
                  variant="ghost"
                  aria-label={t("canvas.assetGroupCreate")}
                  onPress={() => {
                    setCreatingGroup(true);
                    setError(null);
                  }}
                  isDisabled={Boolean(busyAction)}
                >
                  <FolderPlus className="size-3.5" aria-hidden />
                </Button>
              </div>

              <div className="min-h-0 flex-1 space-y-0.5 overflow-y-auto pr-0.5">
                {groups.length === 0 ? (
                  <div className="px-1 py-3 text-[0.6875rem] leading-relaxed text-muted-foreground">
                    <p>{t("canvas.assetGroupEmpty")}</p>
                    <p className="mt-1 opacity-75">{t("canvas.assetGroupEmptyHint")}</p>
                  </div>
                ) : (
                  groups.map((group) => (
                    <GroupNavRow
                      key={group.id}
                      group={group}
                      active={activeGroupId === group.id}
                      editing={editingGroupId === group.id}
                      draft={editingGroupName}
                      busy={busyAction === `rename:${group.id}` || busyAction === `delete-group:${group.id}`}
                      isDragOver={dragOverGroupId === group.id}
                      onSelect={() => setActiveGroupId(group.id)}
                      onRename={() => beginRename(group)}
                      onDelete={() => setPendingDeleteGroup(group)}
                      onDraftChange={setEditingGroupName}
                      onCommit={() => void commitRename(group.id)}
                      onCancel={abandonRename}
                      onDragOver={(event) => {
                        event.preventDefault();
                        setDragOverGroupId(group.id);
                      }}
                      onDragLeave={() => setDragOverGroupId(null)}
                      onDrop={(event) => dropOnGroup(event, group.id)}
                    />
                  ))
                )}
              </div>
            </div>

            {creatingGroup && (
              <div className="mt-2 flex gap-1">
                <Input
                  autoFocus
                  maxLength={64}
                  value={newGroupName}
                  placeholder={t("canvas.assetGroupNamePlaceholder")}
                  aria-label={t("canvas.assetGroupNamePlaceholder")}
                  className="h-7 min-w-0 flex-1 text-xs"
                  onChange={(event) => setNewGroupName(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter") {
                      event.preventDefault();
                      void createGroup();
                    } else if (event.key === "Escape") {
                      event.preventDefault();
                      setCreatingGroup(false);
                      setNewGroupName("");
                    }
                  }}
                  onBlur={() => {
                    // Do not submit on blur; clicking the plus button should not create an empty
                    // folder, and Escape remains a reliable way to abandon the draft.
                  }}
                />
                <Button
                  size="icon-xs"
                  variant="ghost"
                  aria-label={t("common.add")}
                  onPress={() => void createGroup()}
                  isDisabled={Boolean(busyAction)}
                >
                  {busyAction === "create-group" ? (
                    <Loader2 className="size-3 animate-spin" aria-hidden />
                  ) : (
                    <Check className="size-3" aria-hidden />
                  )}
                </Button>
              </div>
            )}
          </aside>

          <section className="flex min-w-0 flex-1 flex-col gap-3">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="min-w-0">
                <p className="truncate text-sm font-medium">{activeLabel}</p>
                <p className="text-[0.6875rem] text-muted-foreground">
                  {t("canvas.assetGroupCount", { count: visibleAssets.length })}
                </p>
              </div>

              {selected.length > 0 && (
                <div className="flex items-center gap-2">
                  <span className="text-xs text-muted-foreground">
                    {t("canvas.promptSelected", { count: selected.length })}
                  </span>
                  <label className="relative">
                    <span className="sr-only">{t("canvas.assetGroupMove")}</span>
                    <select
                      aria-label={t("canvas.assetGroupMove")}
                      value={moveTarget}
                      disabled={Boolean(busyAction)}
                      onChange={(event) => {
                        const target = event.currentTarget.value;
                        if (!target) return;
                        void moveAssets(
                          selected,
                          target === UNGROUPED_ASSETS ? null : target,
                        );
                      }}
                      className="h-7 max-w-40 rounded-md border border-input bg-background px-2 text-xs outline-none focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/40 disabled:opacity-50"
                    >
                      <option value="">{t("canvas.assetGroupMove")}</option>
                      <option value={UNGROUPED_ASSETS}>{t("canvas.assetGroupMoveNone")}</option>
                      {groups.map((group) => (
                        <option key={group.id} value={group.id}>
                          {group.name}
                        </option>
                      ))}
                    </select>
                  </label>
                </div>
              )}
            </div>

            <div className="min-h-0 flex-1 overflow-y-auto pr-1">
              {loading && assets.length === 0 ? (
                <div className="flex h-48 items-center justify-center">
                  <Loader2 className="size-5 animate-spin text-muted-foreground" aria-hidden />
                </div>
              ) : assets.length === 0 ? (
                <EmptyLibrary />
              ) : visibleAssets.length === 0 ? (
                <div className="flex h-48 flex-col items-center justify-center gap-2 text-center">
                  <FolderOpen className="size-6 text-muted-foreground" aria-hidden />
                  <p className="text-sm">{t("canvas.assetGroupNoAssets")}</p>
                  <p className="max-w-sm text-xs text-muted-foreground">
                    {t("canvas.assetGroupEmptyHint")}
                  </p>
                </div>
              ) : (
                <div className="grid grid-cols-3 gap-2 sm:grid-cols-4 lg:grid-cols-5">
                  {visibleAssets.map((asset) => (
                    <AssetTile
                      key={asset.id}
                      asset={asset}
                      selected={selected.includes(asset.id)}
                      busy={busyAction === `asset:${asset.id}` || busyAction === "assign"}
                      onToggle={() => toggle(asset.id)}
                      onDelete={() => setDeletingId(asset.id)}
                      onDragStart={(event) => {
                        event.dataTransfer.effectAllowed = "move";
                        event.dataTransfer.setData("text/asset-id", asset.id);
                        setDraggedAssetId(asset.id);
                      }}
                      onDragEnd={() => {
                        setDraggedAssetId(null);
                        setDragOverGroupId(null);
                      }}
                    />
                  ))}
                </div>
              )}
            </div>
          </section>
          </div>

          <DialogFooter className="mx-0 mb-0 rounded-none rounded-b-xl">
            <span className="mr-auto text-xs text-muted-foreground">
              {selected.length > 0 ? t("canvas.promptSelected", { count: selected.length }) : ""}
            </span>
            <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
              {t("common.cancel")}
            </Button>
            <Button size="sm" isDisabled={selected.length === 0} onPress={insert}>
              {t("common.add")}
            </Button>
          </DialogFooter>
        </div>
      </Dialog>

      <ConfirmDialog
        isOpen={deletingId !== null}
        onOpenChange={(open) => !open && setDeletingId(null)}
        title={t("canvas.assetDeleteConfirm")}
        onConfirm={() => {
          if (deletingId) return remove(deletingId);
        }}
      />

      <ConfirmDialog
        isOpen={pendingDeleteGroup !== null}
        onOpenChange={(open) => !open && setPendingDeleteGroup(null)}
        title={t("canvas.assetGroupDelete")}
        description={
          pendingDeleteGroup
            ? t("canvas.assetGroupDeleteConfirm", { name: pendingDeleteGroup.name })
            : undefined
        }
        onConfirm={() =>
          pendingDeleteGroup ? removeGroup(pendingDeleteGroup) : undefined
        }
      />
    </>
  );
}

function EmptyLibrary() {
  const t = useT();
  return (
    <div className="flex h-48 flex-col items-center justify-center gap-2 text-center">
      <ImageIcon className="size-6 text-muted-foreground" aria-hidden />
      <p className="text-sm">{t("canvas.assetLibraryEmpty")}</p>
      <p className="max-w-sm text-xs text-muted-foreground">
        {t("canvas.assetLibraryEmptyHint")}
      </p>
    </div>
  );
}

function LibraryNavButton({
  icon: Icon,
  label,
  count,
  active,
  onPress,
  dropTarget = false,
  isDragOver = false,
  onDragOver,
  onDragLeave,
  onDrop,
}: {
  icon: LucideIcon;
  label: string;
  count: number;
  active: boolean;
  onPress: () => void;
  dropTarget?: boolean;
  isDragOver?: boolean;
  onDragOver?: (event: React.DragEvent<HTMLButtonElement>) => void;
  onDragLeave?: () => void;
  onDrop?: (event: React.DragEvent<HTMLButtonElement>) => void;
}) {
  return (
    <button
      type="button"
      className={cn(
        "flex h-8 w-full items-center gap-2 rounded-md px-2 text-left text-xs transition-colors",
        active ? "bg-accent text-accent-foreground" : "text-muted-foreground hover:bg-accent/60 hover:text-foreground",
        isDragOver && "bg-primary/10 text-primary ring-1 ring-primary/40",
      )}
      aria-current={active ? "page" : undefined}
      onClick={onPress}
      onDragOver={dropTarget ? onDragOver : undefined}
      onDragLeave={dropTarget ? onDragLeave : undefined}
      onDrop={dropTarget ? onDrop : undefined}
    >
      <Icon className="size-3.5 shrink-0" aria-hidden />
      <span className="min-w-0 flex-1 truncate">{label}</span>
      <span className="text-[0.625rem] tabular-nums opacity-60">{count}</span>
    </button>
  );
}

function GroupNavRow({
  group,
  active,
  editing,
  draft,
  busy,
  isDragOver,
  onSelect,
  onRename,
  onDelete,
  onDraftChange,
  onCommit,
  onCancel,
  onDragOver,
  onDragLeave,
  onDrop,
}: {
  group: api.CanvasAssetGroupSummary;
  active: boolean;
  editing: boolean;
  draft: string;
  busy: boolean;
  isDragOver: boolean;
  onSelect: () => void;
  onRename: () => void;
  onDelete: () => void;
  onDraftChange: (value: string) => void;
  onCommit: () => void;
  onCancel: () => void;
  onDragOver: (event: React.DragEvent<HTMLDivElement>) => void;
  onDragLeave: () => void;
  onDrop: (event: React.DragEvent<HTMLDivElement>) => void;
}) {
  const t = useT();

  if (editing) {
    return (
      <div className="flex items-center gap-1 rounded-md bg-accent/50 p-1">
        <Input
          autoFocus
          maxLength={64}
          value={draft}
          aria-label={t("canvas.assetGroupNamePlaceholder")}
          className="h-6 min-w-0 flex-1 text-xs"
          onChange={(event) => onDraftChange(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === "Enter") {
              event.preventDefault();
              onCommit();
            } else if (event.key === "Escape") {
              event.preventDefault();
              onCancel();
            }
          }}
          onBlur={onCommit}
        />
      </div>
    );
  }

  return (
    <div
      className={cn(
        "group flex min-h-8 items-center rounded-md transition-colors",
        active ? "bg-accent text-accent-foreground" : "hover:bg-accent/60",
        isDragOver && "bg-primary/10 ring-1 ring-primary/40",
        busy && "pointer-events-none opacity-60",
      )}
      onDragOver={onDragOver}
      onDragLeave={onDragLeave}
      onDrop={onDrop}
    >
      <button
        type="button"
        aria-current={active ? "page" : undefined}
        className="flex min-w-0 flex-1 items-center gap-2 px-2 py-1.5 text-left text-xs"
        onClick={onSelect}
        title={t("canvas.assetGroupDragHint")}
      >
        <Folder className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
        <span className="min-w-0 flex-1 truncate">{group.name}</span>
        <span className="text-[0.625rem] tabular-nums text-muted-foreground">{group.assetCount}</span>
      </button>
      <button
        type="button"
        aria-label={t("canvas.assetGroupRename")}
        title={t("canvas.assetGroupRename")}
        className="rounded p-1 text-muted-foreground opacity-0 transition-opacity hover:bg-background focus-visible:opacity-100 group-hover:opacity-100"
        onClick={onRename}
      >
        <Pencil className="size-3" aria-hidden />
      </button>
      <button
        type="button"
        aria-label={t("canvas.assetGroupDelete")}
        title={t("canvas.assetGroupDelete")}
        className="mr-1 rounded p-1 text-muted-foreground opacity-0 transition-opacity hover:bg-background hover:text-destructive focus-visible:opacity-100 group-hover:opacity-100"
        onClick={onDelete}
      >
        <Trash2 className="size-3" aria-hidden />
      </button>
    </div>
  );
}

function AssetTile({
  asset,
  selected,
  busy,
  onToggle,
  onDelete,
  onDragStart,
  onDragEnd,
}: {
  asset: api.CanvasAssetSummary;
  selected: boolean;
  busy: boolean;
  onToggle: () => void;
  onDelete: () => void;
  onDragStart: (event: React.DragEvent<HTMLButtonElement>) => void;
  onDragEnd: () => void;
}) {
  const t = useT();

  return (
    <div className="group relative">
      <button
        type="button"
        aria-label={asset.prompt ?? t("canvas.assetSelect")}
        aria-pressed={selected}
        onClick={onToggle}
        draggable
        onDragStart={onDragStart}
        onDragEnd={onDragEnd}
        title={asset.prompt ?? undefined}
        className={cn(
          "block aspect-square w-full overflow-hidden rounded-lg border-2 bg-muted/20 transition-all",
          selected
            ? "border-primary shadow-[0_0_0_2px_color-mix(in_oklab,var(--primary)_20%,transparent)]"
            : "border-transparent hover:border-border",
          busy && "cursor-wait opacity-60",
        )}
      >
        {/* Cookie-authenticated route; next/image cannot fetch it. */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src={asset.url}
          alt={asset.prompt ?? ""}
          draggable={false}
          className="size-full object-cover"
          loading="lazy"
        />
        {selected && (
          <span className="absolute top-1 left-1 flex size-5 items-center justify-center rounded-full bg-primary text-primary-foreground shadow-sm">
            <Check className="size-3" aria-hidden />
          </span>
        )}
      </button>

      <Button
        size="icon-xs"
        variant="ghost"
        aria-label={t("common.delete")}
        className="absolute top-1 right-1 size-5 bg-background/80 opacity-0 transition-opacity group-hover:opacity-100"
        onPress={onDelete}
        isDisabled={busy}
      >
        <Trash2 className="size-2.5" aria-hidden />
      </Button>
    </div>
  );
}

// Kept as a named alias so the nav's icon type remains stable if the icon set changes.
const ImagesIcon = ImageIcon;
