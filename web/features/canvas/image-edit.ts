"use client";

import { uploadAsset, type UploadedAsset } from "@/features/ai/api";
import { toUploadFile } from "@/features/canvas/image-ops";
import { useCanvasStore } from "@/features/canvas/store";
import type { CanvasNodeData, NodeKind } from "@/features/canvas/types";

export interface AppliedImageEdit {
  nodeId: string;
  assetUrl: string;
  mediaType: string;
}

interface ImageEditDependencies {
  upload?: (file: File) => Promise<UploadedAsset>;
  spawn?: (
    sourceId: string,
    kind: NodeKind,
    data?: Partial<CanvasNodeData>,
    stackIndex?: number,
  ) => string | null;
}

/**
 * Produces, uploads, and places one local image edit.
 *
 * Shared by node UI and the Agent executor so both preserve the source image and use the same
 * durable asset route and output placement semantics.
 */
export async function applyImageEdit(
  sourceNodeId: string,
  sourceUrl: string,
  produce: (url: string) => Promise<Blob>,
  label: string,
  data: { role?: "reference" | "inpaint-mask" } = {},
  dependencies: ImageEditDependencies = {},
): Promise<AppliedImageEdit> {
  const blob = await produce(sourceUrl);
  const asset = await (dependencies.upload ?? uploadAsset)(toUploadFile(blob, label));
  const spawn = dependencies.spawn ?? useCanvasStore.getState().spawnOutput;
  const nodeId = spawn(sourceNodeId, "image", {
    assetUrl: asset.url,
    mediaType: asset.mediaType,
    ...data,
  });
  if (!nodeId) throw new Error("output_node_creation_failed");

  return { nodeId, assetUrl: asset.url, mediaType: asset.mediaType };
}
