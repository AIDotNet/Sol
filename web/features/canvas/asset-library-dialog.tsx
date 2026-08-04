"use client";

import { ImageIcon, Loader2, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import * as api from "@/features/canvas/api";
import { useCanvasStore } from "@/features/canvas/store";
import { NODE_DEFAULT_SIZE } from "@/features/canvas/types";
import { cn } from "@/lib/utils";

/**
 * Everything this device has generated or uploaded, for reuse on the canvas.
 *
 * Assets already live server-side with durable URLs, so inserting one is just a node pointing
 * at an existing id — no re-upload, no copy.
 *
 * Mounted only while open (see the caller), so the fetch happens once on mount and the
 * selection resets naturally. A user opens this right after generating, and a cached list would
 * be missing the very image they came looking for.
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
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        const { assets: list } = await api.listAssets("image");
        if (!cancelled) setAssets(list);
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : t("errors.unknown"));
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [t]);

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
    try {
      await api.deleteAsset(id);
      setAssets((current) => current.filter((asset) => asset.id !== id));
      setSelected((current) => current.filter((value) => value !== id));
    } catch (deleteError) {
      setError(deleteError instanceof Error ? deleteError.message : t("errors.unknown"));
    }
  }

  return (
    <>
    <Dialog
      isOpen
      onOpenChange={onOpenChange}
      className="w-[min(46rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("canvas.assetLibrary")}</DialogTitle>
        <DialogDescription>{t("canvas.assetLibraryHint")}</DialogDescription>
      </DialogHeader>

      {error && <p className="text-xs text-destructive">{error}</p>}

      <div className="min-h-48">
        {loading && assets.length === 0 ? (
          <div className="flex h-48 items-center justify-center">
            <Loader2 className="size-5 animate-spin text-muted-foreground" aria-hidden />
          </div>
        ) : assets.length === 0 ? (
          <div className="flex h-48 flex-col items-center justify-center gap-2 text-center">
            <ImageIcon className="size-6 text-muted-foreground" aria-hidden />
            <p className="text-sm">{t("canvas.assetLibraryEmpty")}</p>
            <p className="max-w-sm text-xs text-muted-foreground">
              {t("canvas.assetLibraryEmptyHint")}
            </p>
          </div>
        ) : (
          <div className="grid max-h-[50vh] grid-cols-4 gap-2 overflow-y-auto sm:grid-cols-5">
            {assets.map((asset) => (
              <div key={asset.id} className="group relative">
                <button
                  type="button"
                  onClick={() => toggle(asset.id)}
                  title={asset.prompt ?? undefined}
                  className={cn(
                    "block aspect-square w-full overflow-hidden rounded-lg border-2 transition-colors",
                    selected.includes(asset.id)
                      ? "border-primary"
                      : "border-transparent hover:border-border",
                  )}
                >
                  {/* Cookie-authenticated route; next/image cannot fetch it. */}
                  {/* eslint-disable-next-line @next/next/no-img-element */}
                  <img
                    src={asset.url}
                    alt={asset.prompt ?? ""}
                    className="size-full bg-muted/30 object-cover"
                    loading="lazy"
                  />
                </button>

                <Button
                  size="icon-xs"
                  variant="ghost"
                  aria-label={t("common.delete")}
                  className="absolute top-1 right-1 size-5 bg-background/80 opacity-0 transition-opacity group-hover:opacity-100"
                  onPress={() => setDeletingId(asset.id)}
                >
                  <Trash2 className="size-2.5" aria-hidden />
                </Button>
              </div>
            ))}
          </div>
        )}
      </div>

      <DialogFooter>
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
    </Dialog>

    <ConfirmDialog
      isOpen={deletingId !== null}
      onOpenChange={(open) => !open && setDeletingId(null)}
      title={t("canvas.assetDeleteConfirm")}
      onConfirm={() => {
        if (deletingId) return remove(deletingId);
      }}
    />
    </>
  );
}
