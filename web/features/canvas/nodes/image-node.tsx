"use client";

import { type NodeProps, NodeResizer, useReactFlow } from "@xyflow/react";
import {
  AlertTriangle,
  Brush,
  Check,
  Copy,
  Download,
  Expand,
  FlipHorizontal,
  ImageIcon,
  Loader2,
  Maximize2,
  RotateCw,
  Upload,
} from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { uploadAsset } from "@/features/ai/api";
import { copyImageToClipboard } from "@/features/canvas/canvas-interactions";
import { cancelRun, retryRun } from "@/features/canvas/execution";
import { NodeActionButton, NodeShell } from "@/features/canvas/nodes/node-shell";
import { MaskEditorDialog } from "@/features/canvas/nodes/mask-editor-dialog";
import { ResizeImageDialog } from "@/features/canvas/nodes/resize-image-dialog";
import { useImageEdit } from "@/features/canvas/nodes/use-image-edit";
import { useCanvasStore } from "@/features/canvas/store";
import { type ImageNodeData, NODE_MIN_SIZE } from "@/features/canvas/types";

/**
 * Displays a generated or uploaded image.
 *
 * An upload is stored server-side immediately. The object URL is only an optimistic preview
 * while the request is in flight — it is never what gets persisted, because it dies with the
 * document and because the generation endpoint can only resolve `/api/v1/canvas/assets/` URLs
 * when the image is used as a reference.
 */
export function ImageNode({ id, data, selected }: NodeProps) {
  const t = useT();
  const updateNodeData = useCanvasStore((state) => state.updateNodeData);
  const { getNodes, getEdges } = useReactFlow();
  const inputRef = useRef<HTMLInputElement>(null);

  const [dragging, setDragging] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const [copying, setCopying] = useState(false);
  const [copied, setCopied] = useState(false);
  const [copyError, setCopyError] = useState<string | null>(null);
  const copiedTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  /** Optimistic object URL, revoked once the upload resolves or the node unmounts. */
  const previewRef = useRef<string | null>(null);
  const [preview, setPreview] = useState<string | null>(null);

  useEffect(
    () => () => {
      if (previewRef.current) URL.revokeObjectURL(previewRef.current);
      if (copiedTimerRef.current) clearTimeout(copiedTimerRef.current);
    },
    [],
  );

  const nodeData = data as ImageNodeData;
  const edit = useImageEdit(id, nodeData.assetUrl);
  const [masking, setMasking] = useState(false);
  const [resizing, setResizing] = useState(false);

  function releasePreview() {
    if (previewRef.current) {
      URL.revokeObjectURL(previewRef.current);
      previewRef.current = null;
    }
    setPreview(null);
  }

  async function accept(file: File | undefined) {
    if (!file || !file.type.startsWith("image/") || uploading) return;

    releasePreview();
    const objectUrl = URL.createObjectURL(file);
    previewRef.current = objectUrl;
    setPreview(objectUrl);

    setUploading(true);
    setUploadError(null);

    try {
      const asset = await uploadAsset(file);
      updateNodeData(id, { assetUrl: asset.url, mediaType: asset.mediaType });
    } catch (error) {
      setUploadError(error instanceof Error ? error.message : t("errors.unknown"));
    } finally {
      setUploading(false);
      // Released regardless: on success the stored URL takes over, on failure the node falls
      // back to the upload prompt rather than showing an image that was never saved.
      releasePreview();
    }
  }

  async function copyImage() {
    if (!nodeData.assetUrl || copying) return;

    if (copiedTimerRef.current) {
      clearTimeout(copiedTimerRef.current);
      copiedTimerRef.current = null;
    }

    setCopying(true);
    setCopied(false);
    setCopyError(null);

    try {
      await copyImageToClipboard(nodeData.assetUrl, nodeData.mediaType);
      setCopied(true);
      copiedTimerRef.current = setTimeout(() => {
        copiedTimerRef.current = null;
        setCopied(false);
      }, 1800);
    } catch {
      setCopyError(t("node.copyFailed"));
    } finally {
      setCopying(false);
    }
  }

  const shown = nodeData.assetUrl ?? preview;

  return (
    <>
      <NodeResizer
        isVisible={selected}
        minWidth={NODE_MIN_SIZE.width}
        minHeight={NODE_MIN_SIZE.height}
        lineClassName="!border-ring"
        handleClassName="!size-2 !rounded-sm !border-ring !bg-background"
      />

      <NodeShell
        title={t("canvas.nodeImage")}
        icon={<ImageIcon className="size-3 shrink-0 text-muted-foreground" aria-hidden />}
        selected={selected}
        execution={nodeData.execution}
        onCancel={() => cancelRun(id)}
        onRetry={() => void retryRun(id, getNodes(), getEdges())}
        actions={
          nodeData.assetUrl ? (
            <>
              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                label={t("node.maskEdit")}
                onPress={() => setMasking(true)}
              >
                <Brush className="size-2.5" aria-hidden />
              </NodeActionButton>

              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                isDisabled={edit.busy}
                label={t("node.rotate")}
                onPress={() => void edit.rotate(1)}
              >
                <RotateCw className="size-2.5" aria-hidden />
              </NodeActionButton>

              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                isDisabled={edit.busy}
                label={t("node.flip")}
                onPress={() => void edit.flip("horizontal")}
              >
                <FlipHorizontal className="size-2.5" aria-hidden />
              </NodeActionButton>

              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                isDisabled={edit.busy}
                label={t("node.resize")}
                onPress={() => setResizing(true)}
              >
                <Maximize2 className="size-2.5" aria-hidden />
              </NodeActionButton>

              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                isDisabled={edit.busy}
                label={t("node.expand")}
                onPress={() =>
                  void edit.expand({ top: 0.25, right: 0.25, bottom: 0.25, left: 0.25 })
                }
              >
                <Expand className="size-2.5" aria-hidden />
              </NodeActionButton>

              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                isDisabled={copying}
                label={copied ? t("common.copied") : t("common.copy")}
                onPress={() => void copyImage()}
              >
                {copying ? (
                  <Loader2 className="size-2.5 animate-spin" aria-hidden />
                ) : copied ? (
                  <Check className="size-2.5 text-emerald-600" aria-hidden />
                ) : (
                  <Copy className="size-2.5" aria-hidden />
                )}
              </NodeActionButton>

              <NodeActionButton
                size="icon-xs"
                variant="ghost"
                className="size-5"
                label={t("common.download")}
                onPress={() => {
                  const link = document.createElement("a");
                  link.href = nodeData.assetUrl!;
                  link.download = `sol-${id}.png`;
                  link.click();
                }}
              >
                <Download className="size-2.5" aria-hidden />
              </NodeActionButton>
            </>
          ) : null
        }
      >
        <div
          className="nodrag relative flex h-full w-full items-center justify-center overflow-hidden bg-muted/20"
          onDragOver={(event) => {
            event.preventDefault();
            setDragging(true);
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={(event) => {
            event.preventDefault();
            setDragging(false);
            void accept(event.dataTransfer.files[0]);
          }}
        >
          {shown ? (
            // Object URLs and cookie-authenticated asset routes are not next/image compatible,
            // and the node already constrains the rendered size.
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={shown}
              alt={nodeData.prompt ?? t("canvas.nodeImage")}
              className="size-full object-contain"
              draggable={false}
            />
          ) : (
            <button
              type="button"
              onClick={() => inputRef.current?.click()}
              className={`flex size-full flex-col items-center justify-center gap-1.5 text-[0.6875rem] transition-colors ${
                dragging ? "bg-accent text-foreground" : "text-muted-foreground hover:bg-accent/40"
              }`}
            >
              <Upload className="size-4" aria-hidden />
              {t("node.dropHint")}
            </button>
          )}

          {(uploading || edit.busy) && (
            <div className="absolute inset-0 flex items-center justify-center bg-background/60">
              <Loader2 className="size-4 animate-spin text-muted-foreground" aria-hidden />
            </div>
          )}

          <input
            ref={inputRef}
            type="file"
            accept="image/png,image/jpeg,image/webp,image/gif"
            className="hidden"
            onChange={(event) => void accept(event.target.files?.[0])}
          />
        </div>

        {(uploadError || copyError || edit.error) && (
          <div className="nodrag flex shrink-0 items-start gap-1.5 border-t bg-destructive/5 px-2 py-1.5">
            <AlertTriangle className="mt-px size-3 shrink-0 text-destructive" aria-hidden />
            <p className="min-w-0 flex-1 text-[0.625rem] leading-tight text-destructive">
              {uploadError ?? copyError ?? edit.error}
            </p>
          </div>
        )}
      </NodeShell>

      {/* Mounted only while open so its stroke state resets each time. */}
      {masking && nodeData.assetUrl && (
        <MaskEditorDialog
          onOpenChange={setMasking}
          nodeId={id}
          sourceUrl={nodeData.assetUrl}
        />
      )}

      {resizing && nodeData.assetUrl && (
        <ResizeImageDialog
          key={nodeData.assetUrl}
          onOpenChange={setResizing}
          sourceUrl={nodeData.assetUrl}
          error={edit.error}
          onResize={edit.resize}
        />
      )}
    </>
  );
}
