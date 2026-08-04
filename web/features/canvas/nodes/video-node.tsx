"use client";

import { type NodeProps, NodeResizer, useReactFlow } from "@xyflow/react";
import { Video } from "lucide-react";
import { useT } from "@/components/providers/i18n-provider";
import { cancelRun, retryRun } from "@/features/canvas/execution";
import { NodeShell } from "@/features/canvas/nodes/node-shell";
import { NODE_MIN_SIZE, type VideoNodeData } from "@/features/canvas/types";

export function VideoNode({ id, data, selected }: NodeProps) {
  const t = useT();
  const { getNodes, getEdges } = useReactFlow();
  const nodeData = data as VideoNodeData;

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
        title={t("canvas.nodeVideo")}
        icon={<Video className="size-3 shrink-0 text-muted-foreground" aria-hidden />}
        selected={selected}
        execution={nodeData.execution}
        onCancel={() => cancelRun(id)}
        onRetry={() => retryRun(id, getNodes(), getEdges())}
      >
        <div className="nodrag flex h-full w-full items-center justify-center overflow-hidden bg-black/80">
          {nodeData.assetUrl ? (
            <video
              src={nodeData.assetUrl}
              poster={nodeData.posterUrl}
              controls
              className="size-full object-contain"
            />
          ) : (
            <p className="text-[0.6875rem] text-white/60">{t("node.noVideo")}</p>
          )}
        </div>
      </NodeShell>
    </>
  );
}
