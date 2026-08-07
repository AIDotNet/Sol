"use client";

import { Loader2 } from "lucide-react";
import { useEffect, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { MAX_IMAGE_EDGE } from "@/features/canvas/image-ops";

interface ImageSize {
  width: number;
  height: number;
}

export function ResizeImageDialog({
  onOpenChange,
  sourceUrl,
  error,
  onResize,
}: {
  onOpenChange: (open: boolean) => void;
  sourceUrl: string;
  error: string | null;
  onResize: (width: number, height: number) => Promise<boolean>;
}) {
  const t = useT();
  const [originalSize, setOriginalSize] = useState<ImageSize | null>(null);
  const [width, setWidth] = useState("");
  const [height, setHeight] = useState("");
  const [lockAspect, setLockAspect] = useState(true);
  const [loadingSize, setLoadingSize] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const image = new Image();
    image.crossOrigin = "anonymous";

    image.onload = () => {
      if (cancelled) return;

      const next = {
        width: image.naturalWidth,
        height: image.naturalHeight,
      };
      setOriginalSize(next);
      setWidth(String(next.width));
      setHeight(String(next.height));
      setLoadingSize(false);
    };
    image.onerror = () => {
      if (cancelled) return;
      setLoadingSize(false);
      setLoadError(t("node.resizeLoadFailed"));
    };
    image.src = sourceUrl;

    return () => {
      cancelled = true;
      image.onload = null;
      image.onerror = null;
    };
  }, [sourceUrl, t]);

  function changeWidth(value: string) {
    setWidth(value);
    if (!lockAspect || !originalSize || !value) return;

    const nextWidth = Number(value);
    if (Number.isFinite(nextWidth) && nextWidth > 0) {
      setHeight(String(Math.max(1, Math.round((nextWidth * originalSize.height) / originalSize.width))));
    }
  }

  function changeHeight(value: string) {
    setHeight(value);
    if (!lockAspect || !originalSize || !value) return;

    const nextHeight = Number(value);
    if (Number.isFinite(nextHeight) && nextHeight > 0) {
      setWidth(String(Math.max(1, Math.round((nextHeight * originalSize.width) / originalSize.height))));
    }
  }

  const targetWidth = parseDimension(width);
  const targetHeight = parseDimension(height);
  const hasInput = width.length > 0 || height.length > 0;
  const dimensionsValid =
    targetWidth !== null
    && targetHeight !== null
    && targetWidth <= MAX_IMAGE_EDGE
    && targetHeight <= MAX_IMAGE_EDGE;
  const dimensionError = hasInput && !dimensionsValid
    ? t("node.invalidDimensions", { max: MAX_IMAGE_EDGE })
    : null;
  const displayError = loadError ?? submitError ?? error ?? dimensionError;

  async function apply() {
    if (!dimensionsValid || busy || loadingSize || targetWidth === null || targetHeight === null) {
      return;
    }

    setBusy(true);
    setSubmitError(null);
    const succeeded = await onResize(targetWidth, targetHeight);
    if (succeeded) {
      onOpenChange(false);
    } else {
      setSubmitError(t("node.resizeFailed"));
    }
    setBusy(false);
  }

  return (
    <Dialog
      isOpen
      onOpenChange={onOpenChange}
      className="w-[min(28rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("node.resizeTitle")}</DialogTitle>
        <DialogDescription>{t("node.resizeHint")}</DialogDescription>
      </DialogHeader>

      <div className="flex flex-col gap-4">
        <div className="rounded-lg border bg-muted/20 px-3 py-2 text-xs text-muted-foreground">
          {loadingSize
            ? t("common.loading")
            : originalSize
              ? t("node.currentSize", {
                  width: originalSize.width,
                  height: originalSize.height,
                })
              : t("node.resizeLoadFailed")}
        </div>

        <div className="grid grid-cols-2 gap-3">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="resize-image-width">{t("node.width")}</Label>
            <Input
              id="resize-image-width"
              type="number"
              min={1}
              max={MAX_IMAGE_EDGE}
              step={1}
              value={width}
              onChange={(event) => changeWidth(event.target.value)}
              aria-label={t("node.width")}
              disabled={loadingSize || busy}
            />
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="resize-image-height">{t("node.height")}</Label>
            <Input
              id="resize-image-height"
              type="number"
              min={1}
              max={MAX_IMAGE_EDGE}
              step={1}
              value={height}
              onChange={(event) => changeHeight(event.target.value)}
              aria-label={t("node.height")}
              disabled={loadingSize || busy}
            />
          </div>
        </div>

        <Switch
          size="sm"
          isSelected={lockAspect}
          onChange={setLockAspect}
          isDisabled={loadingSize || busy}
        >
          <span className="ml-1 text-xs text-muted-foreground">{t("node.lockAspect")}</span>
        </Switch>

        {displayError && <p className="text-xs text-destructive">{displayError}</p>}
      </div>

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        <Button
          size="sm"
          isDisabled={!dimensionsValid || loadingSize || busy}
          onPress={() => void apply()}
        >
          {busy && <Loader2 className="size-3.5 animate-spin" aria-hidden />}
          {t("node.applyResize")}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}

function parseDimension(value: string): number | null {
  if (!/^\d+$/.test(value)) return null;

  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}
