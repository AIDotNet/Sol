"use client";

import { useCallback, useEffect } from "react";
import { uploadAsset } from "@/features/ai/api";
import { type CanvasNode, useCanvasStore } from "@/features/canvas/store";
import { NODE_DEFAULT_SIZE } from "@/features/canvas/types";

/**
 * Turns dropped or pasted image files into image nodes.
 *
 * Shared by the paste handler and the canvas drop target so both take the same path: upload
 * first, then create the node with a durable URL. Creating the node from an object URL would
 * reintroduce the bug where an image vanishes on reload and is ignored as a generation
 * reference.
 */
export function useImageDrop() {
  const addNode = useCanvasStore((state) => state.addNode);
  const updateNodeData = useCanvasStore((state) => state.updateNodeData);

  return useCallback(
    async (files: File[], at: { x: number; y: number }) => {
      const images = files.filter((file) => file.type.startsWith("image/"));

      for (const [index, file] of images.entries()) {
        // The node appears immediately so the drop feels instant; the URL lands when the
        // upload resolves.
        const id = addNode("image", {
          x: at.x + index * 32,
          y: at.y + index * 32,
        });

        try {
          const asset = await uploadAsset(file);
          updateNodeData(id, { assetUrl: asset.url, mediaType: asset.mediaType });
        } catch {
          // The node stays as an empty upload target rather than disappearing, so the user can
          // retry from it directly.
        }
      }
    },
    [addNode, updateNodeData],
  );
}

/**
 * Pastes images from the clipboard onto the canvas.
 *
 * Ignored while focus is in a text field, where paste means inserting text.
 */
export function usePasteImages(
  onFiles: (files: File[], at: { x: number; y: number }) => void,
  center: () => { x: number; y: number },
) {
  useEffect(() => {
    function onPaste(event: ClipboardEvent) {
      const target = event.target as HTMLElement | null;
      if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
      if (target?.isContentEditable) return;

      const files = [...(event.clipboardData?.files ?? [])].filter((file) =>
        file.type.startsWith("image/"),
      );
      if (files.length === 0) return;

      event.preventDefault();
      onFiles(files, center());
    }

    window.addEventListener("paste", onPaste);
    return () => window.removeEventListener("paste", onPaste);
  }, [onFiles, center]);
}

/** Places a node so the drop point is its centre rather than its top-left corner. */
export function centredAt(
  point: { x: number; y: number },
  kind: keyof typeof NODE_DEFAULT_SIZE,
): { x: number; y: number } {
  const size = NODE_DEFAULT_SIZE[kind];
  return { x: point.x - size.width / 2, y: point.y - size.height / 2 };
}

/** Downloads a canvas export as a file. */
export function downloadJson(filename: string, json: string): void {
  const blob = new Blob([json], { type: "application/json" });
  const url = URL.createObjectURL(blob);

  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();

  // Revoked on the next tick: revoking synchronously can cancel the download in some browsers.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

/**
 * Copies the pixels behind an image node to the system clipboard.
 *
 * Asset URLs are cookie-authenticated, so the image has to be fetched before creating the
 * ClipboardItem. PNG is the interoperable image clipboard format; other supported canvas image
 * formats are rasterised to PNG first so JPEG/WebP/GIF assets work in browsers that only accept
 * PNG clipboard representations.
 */
export async function copyImageToClipboard(url: string, mediaType?: string): Promise<void> {
  const clipboard = typeof navigator !== "undefined" ? navigator.clipboard : undefined;
  const ClipboardItemConstructor = globalThis.ClipboardItem;
  if (!clipboard?.write || !ClipboardItemConstructor) {
    throw new Error("Image clipboard is not supported.");
  }

  const response = await fetch(url, { credentials: "include" });
  if (!response.ok) throw new Error("The image could not be loaded.");

  const source = await response.blob();
  const sourceType = source.type.toLowerCase();
  const declaredType = mediaType?.toLowerCase();
  const type = sourceType.startsWith("image/")
    ? sourceType
    : declaredType?.startsWith("image/")
      ? declaredType
      : "image/png";
  const typedSource = source.type === type ? source : new Blob([source], { type });
  const image = type === "image/png" ? typedSource : await rasterizeAsPng(typedSource);

  await clipboard.write([new ClipboardItemConstructor({ "image/png": image })]);
}

async function rasterizeAsPng(source: Blob): Promise<Blob> {
  const objectUrl = URL.createObjectURL(source);

  try {
    if (typeof createImageBitmap === "function") {
      const bitmap = await createImageBitmap(source);
      try {
        const canvas = document.createElement("canvas");
        canvas.width = Math.max(1, bitmap.width);
        canvas.height = Math.max(1, bitmap.height);
        const context = canvas.getContext("2d");
        if (!context) throw new Error("A 2D canvas context is unavailable.");
        context.drawImage(bitmap, 0, 0);
        return await canvasToBlob(canvas);
      } finally {
        bitmap.close();
      }
    }

    const image = await loadImage(objectUrl);
    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, image.naturalWidth || image.width);
    canvas.height = Math.max(1, image.naturalHeight || image.height);
    const context = canvas.getContext("2d");
    if (!context) throw new Error("A 2D canvas context is unavailable.");
    context.drawImage(image, 0, 0);
    return await canvasToBlob(canvas);
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error("The image could not be decoded."));
    image.src = url;
  });
}

function canvasToBlob(canvas: HTMLCanvasElement): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new Error("The image could not be encoded."))),
      "image/png",
    );
  });
}

/** Nodes currently selected, for commands that act on a selection. */
export function selectedNodes(): CanvasNode[] {
  return useCanvasStore.getState().nodes.filter((node) => node.selected);
}
