"use client";

import { useCallback, useState } from "react";
import { applyImageEdit } from "@/features/canvas/image-edit";
import {
  cropImage,
  type CropRect,
  expandCanvas,
  type FlipAxis,
  flipImage,
  resizeImage,
  rotateImage,
  upscaleImage,
} from "@/features/canvas/image-ops";
import { useCanvasStore } from "@/features/canvas/store";

/**
 * Applies a local image edit and puts the result on the canvas.
 *
 * The result becomes a new node rather than replacing the source. Editing is exploratory —
 * a user who crops and dislikes it should still have the original, and undo alone would not
 * bring back an image whose only copy was overwritten.
 *
 * The edited bytes go through the normal upload endpoint, so the new node holds a durable
 * asset URL like any other.
 */
export function useImageEdit(nodeId: string, sourceUrl: string | undefined) {
  const spawnOutput = useCanvasStore((state) => state.spawnOutput);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const apply = useCallback(
    async (produce: (url: string) => Promise<Blob>, label: string): Promise<boolean> => {
      if (!sourceUrl || busy) return false;

      setBusy(true);
      setError(null);

      try {
        await applyImageEdit(nodeId, sourceUrl, produce, label, {}, { spawn: spawnOutput });
        return true;
      } catch (editError) {
        setError(editError instanceof Error ? editError.message : "Edit failed");
        return false;
      } finally {
        setBusy(false);
      }
    },
    [nodeId, sourceUrl, busy, spawnOutput],
  );

  return {
    busy,
    error,
    clearError: () => setError(null),

    rotate: (quarterTurns: number) =>
      apply((url) => rotateImage(url, quarterTurns), "rotated"),

    flip: (axis: FlipAxis) => apply((url) => flipImage(url, axis), "flipped"),

    upscale: (factor: number) => apply((url) => upscaleImage(url, factor), "upscaled"),

    crop: (rect: CropRect) => apply((url) => cropImage(url, rect), "cropped"),

    /**
     * Extends the canvas with transparent margins, ready for an outpaint.
     *
     * Produces the input rather than performing the generation: the user then wires the result
     * into an image-generation node, which keeps one path for everything that calls a model.
     */
    expand: (insets: { top: number; right: number; bottom: number; left: number }) =>
      apply((url) => expandCanvas(url, insets), "expanded"),

    resize: (width: number, height: number) =>
      apply((url) => resizeImage(url, width, height), "resized"),
  };
}
