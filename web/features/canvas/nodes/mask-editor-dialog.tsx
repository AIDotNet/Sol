"use client";

import { Eraser, Loader2, Undo2 } from "lucide-react";
import { useCallback, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { uploadAsset } from "@/features/ai/api";
import { buildInpaintMask, toUploadFile } from "@/features/canvas/image-ops";
import { useCanvasStore } from "@/features/canvas/store";

interface Stroke {
  points: Array<{ x: number; y: number }>;
  radius: number;
}

/** Brush sizes as a fraction of image width, so a stroke scales with the picture. */
const BRUSH_SIZES = [0.02, 0.05, 0.1] as const;

/**
 * Paints a region to regenerate, then produces the masked image an inpaint expects.
 *
 * The counter-intuitive part is the output: OpenAI's images/edits convention is that **painted
 * pixels are punched transparent** and the model regenerates exactly those. Filling them opaque
 * would regenerate everything *else*. `buildInpaintMask` handles that; this component only
 * collects strokes.
 *
 * The result becomes a new image node, which the user wires into a generation node. Keeping the
 * model call on the one existing path means inpainting needs no special-case plumbing.
 */
export function MaskEditorDialog({
  onOpenChange,
  nodeId,
  sourceUrl,
}: {
  onOpenChange: (open: boolean) => void;
  nodeId: string;
  sourceUrl: string;
}) {
  const t = useT();
  const spawnOutput = useCanvasStore((state) => state.spawnOutput);

  const [strokes, setStrokes] = useState<Stroke[]>([]);
  const [radius, setRadius] = useState<number>(BRUSH_SIZES[1]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const surfaceRef = useRef<HTMLDivElement>(null);
  const drawing = useRef(false);

  /**
   * Displayed size of the paint surface.
   *
   * Strokes are stored as fractions so they survive a resize, but the preview has to draw in
   * pixels: a 0-1 SVG viewBox cannot express a round brush on a non-square element, and a
   * percentage stroke width does not resolve against one.
   */
  const [surfaceSize, setSurfaceSize] = useState({ width: 0, height: 0 });

  const measure = useCallback((node: HTMLDivElement | null) => {
    surfaceRef.current = node;
    if (!node) return;

    const observer = new ResizeObserver(([entry]) => {
      setSurfaceSize({
        width: entry.contentRect.width,
        height: entry.contentRect.height,
      });
    });

    observer.observe(node);
    return () => observer.disconnect();
  }, []);

  /** Pointer position as a fraction of the image, so strokes survive any preview size. */
  const toFraction = useCallback((event: React.PointerEvent) => {
    const rect = surfaceRef.current?.getBoundingClientRect();
    if (!rect) return null;

    return {
      x: (event.clientX - rect.left) / rect.width,
      y: (event.clientY - rect.top) / rect.height,
    };
  }, []);

  function start(event: React.PointerEvent) {
    const point = toFraction(event);
    if (!point) return;

    // Capture so a stroke continues even if the pointer leaves the surface mid-drag.
    event.currentTarget.setPointerCapture(event.pointerId);
    drawing.current = true;
    setStrokes((current) => [...current, { points: [point], radius }]);
  }

  function extend(event: React.PointerEvent) {
    if (!drawing.current) return;

    const point = toFraction(event);
    if (!point) return;

    setStrokes((current) => {
      if (current.length === 0) return current;

      const last = current[current.length - 1];
      return [...current.slice(0, -1), { ...last, points: [...last.points, point] }];
    });
  }

  function end() {
    drawing.current = false;
  }

  async function apply() {
    if (strokes.length === 0 || busy) return;

    setBusy(true);
    setError(null);

    try {
      const mask = await buildInpaintMask(sourceUrl, strokes);
      const asset = await uploadAsset(toUploadFile(mask, "masked"));

      spawnOutput(nodeId, "image", { assetUrl: asset.url, mediaType: asset.mediaType });
      onOpenChange(false);
    } catch (maskError) {
      setError(maskError instanceof Error ? maskError.message : t("errors.unknown"));
      setBusy(false);
    }
  }

  return (
    <Dialog
      isOpen
      onOpenChange={onOpenChange}
      className="w-[min(44rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("node.maskEdit")}</DialogTitle>
        <DialogDescription>{t("node.maskEditHint")}</DialogDescription>
      </DialogHeader>

      <div className="flex items-center gap-2">
        <span className="text-xs text-muted-foreground">{t("node.brushSize")}</span>
        {BRUSH_SIZES.map((size, index) => (
          <button
            key={size}
            type="button"
            onClick={() => setRadius(size)}
            aria-label={`${t("node.brushSize")} ${index + 1}`}
            className={`flex size-7 items-center justify-center rounded-md border transition-colors ${
              radius === size ? "border-primary bg-accent" : "border-border hover:bg-accent/50"
            }`}
          >
            <span
              className="rounded-full bg-foreground"
              style={{ width: 4 + index * 5, height: 4 + index * 5 }}
            />
          </button>
        ))}

        <Button
          size="sm"
          variant="ghost"
          className="ml-auto"
          isDisabled={strokes.length === 0}
          onPress={() => setStrokes((current) => current.slice(0, -1))}
        >
          <Undo2 className="size-3.5" aria-hidden />
          {t("canvas.undo")}
        </Button>

        <Button
          size="sm"
          variant="ghost"
          isDisabled={strokes.length === 0}
          onPress={() => setStrokes([])}
        >
          <Eraser className="size-3.5" aria-hidden />
          {t("node.clearMask")}
        </Button>
      </div>

      <div
        ref={measure}
        onPointerDown={start}
        onPointerMove={extend}
        onPointerUp={end}
        onPointerCancel={end}
        className="relative max-h-[52vh] cursor-crosshair touch-none overflow-hidden rounded-lg border bg-muted/20 select-none"
      >
        {/* Cookie-authenticated route; next/image cannot fetch it. */}
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img
          src={sourceUrl}
          alt=""
          className="pointer-events-none block max-h-[52vh] w-full object-contain"
          draggable={false}
        />

        {/* Preview only — the real mask is rendered at native resolution on apply. Drawn in
            pixels so the brush stays round on a non-square surface. */}
        <svg className="pointer-events-none absolute inset-0 size-full">
          {strokes.map((stroke, index) => (
            <polyline
              key={index}
              points={stroke.points
                .map(
                  (point) =>
                    `${point.x * surfaceSize.width},${point.y * surfaceSize.height}`,
                )
                .join(" ")}
              fill="none"
              stroke="rgba(239,68,68,0.55)"
              strokeWidth={stroke.radius * 2 * surfaceSize.width}
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          ))}
        </svg>
      </div>

      {error && <p className="text-xs text-destructive">{error}</p>}

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        <Button size="sm" isDisabled={strokes.length === 0 || busy} onPress={() => void apply()}>
          {busy && <Loader2 className="size-3.5 animate-spin" aria-hidden />}
          {t("node.createMasked")}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}
