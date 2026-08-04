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

/** Nodes currently selected, for commands that act on a selection. */
export function selectedNodes(): CanvasNode[] {
  return useCanvasStore.getState().nodes.filter((node) => node.selected);
}
