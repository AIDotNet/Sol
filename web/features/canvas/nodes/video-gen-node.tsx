"use client";

import { type NodeProps, NodeResizer, useReactFlow } from "@xyflow/react";
import { Film, Sparkles } from "lucide-react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { runVideoNode } from "@/features/canvas/execution";
import { ChipRow, ModelPicker, useSelection } from "@/features/canvas/nodes/model-picker";
import { NodeShell } from "@/features/canvas/nodes/node-shell";
import { useCanvasStore } from "@/features/canvas/store";
import { NODE_MIN_SIZE, type VideoGenNodeData } from "@/features/canvas/types";

const RESOLUTIONS = ["480p", "720p", "1080p"] as const;
const DURATIONS = [3, 5, 10] as const;
const FPS_OPTIONS = [16, 24] as const;
const VIDEO_ASPECTS = ["16:9", "9:16", "1:1"] as const;

/**
 * Video generation config.
 *
 * The parameter set diverges sharply by protocol, which is why this branches rather than showing
 * every control:
 *  - `seedance-video` accepts the full set, including fps, watermark and audio.
 *  - `openai-video` (Sora) and `xai-video` take only duration, aspect and resolution; sending
 *    fps or a seed is rejected.
 *  - xAI caps out at 720p, so 1080p is hidden rather than offered and then refused.
 */
export function VideoGenNode({ id, data, selected }: NodeProps) {
  const t = useT();
  const updateNodeData = useCanvasStore((state) => state.updateNodeData);
  const { getNodes, getEdges } = useReactFlow();

  const nodeData = data as VideoGenNodeData;
  const selection = useSelection(nodeData.providerId, nodeData.modelId);

  const protocol = selection?.protocol;
  const isSeedance = protocol === "seedance-video";
  const resolutions = protocol === "xai-video"
    ? RESOLUTIONS.filter((value) => value !== "1080p")
    : RESOLUTIONS;

  return (
    <>
      <NodeResizer
        isVisible={selected}
        minWidth={NODE_MIN_SIZE.width}
        minHeight={240}
        lineClassName="!border-ring"
        handleClassName="!size-2 !rounded-sm !border-ring !bg-background"
      />

      <NodeShell
        title={t("canvas.nodeVideoGen")}
        icon={<Film className="size-3 shrink-0 text-muted-foreground" aria-hidden />}
        selected={selected}
        // Only ever a failure that never reached a node of its own; a run in progress lives on
        // the video node it created.
        execution={nodeData.execution}
        onRetry={() => void runVideoNode(id, getNodes(), getEdges())}
      >
        <div className="nodrag nowheel flex min-h-0 flex-1 flex-col gap-2.5 overflow-y-auto p-2">
          <ModelPicker
            category="video"
            providerId={nodeData.providerId}
            modelId={nodeData.modelId}
            onChange={(next) => updateNodeData(id, next)}
          />

          {selection && (
            <>
              <ChipRow
                label={t("node.aspect")}
                options={VIDEO_ASPECTS}
                value={nodeData.aspect ?? "16:9"}
                onChange={(aspect) => updateNodeData(id, { aspect })}
              />

              <ChipRow
                label={t("node.resolution")}
                options={resolutions}
                value={nodeData.resolution ?? "720p"}
                onChange={(resolution) => updateNodeData(id, { resolution })}
              />

              <ChipRow
                label={t("node.duration")}
                options={DURATIONS}
                value={nodeData.duration ?? 5}
                onChange={(duration) => updateNodeData(id, { duration })}
              />

              {isSeedance && (
                <>
                  <ChipRow
                    label={t("node.fps")}
                    options={FPS_OPTIONS}
                    value={nodeData.fps ?? 24}
                    onChange={(fps) => updateNodeData(id, { fps })}
                  />

                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[0.625rem] text-muted-foreground">
                      {t("node.generateAudio")}
                    </span>
                    <Switch
                      isSelected={nodeData.generateAudio ?? false}
                      onChange={(value) => updateNodeData(id, { generateAudio: value })}
                      aria-label={t("node.generateAudio")}
                    />
                  </div>

                  <div className="flex items-center justify-between gap-2">
                    <span className="text-[0.625rem] text-muted-foreground">
                      {t("node.watermark")}
                    </span>
                    <Switch
                      isSelected={nodeData.watermark ?? false}
                      onChange={(value) => updateNodeData(id, { watermark: value })}
                      aria-label={t("node.watermark")}
                    />
                  </div>
                </>
              )}
            </>
          )}
        </div>

        <div className="nodrag shrink-0 border-t p-1.5">
          {/* Never disabled by a run in progress: each press starts its own generation, and the
              one already going is stopped from the node it is filling. */}
          <Button
            size="sm"
            className="w-full text-[0.6875rem]"
            isDisabled={!selection}
            onPress={() => void runVideoNode(id, getNodes(), getEdges())}
          >
            <Sparkles className="size-3" aria-hidden />
            {t("node.generate")}
          </Button>
        </div>
      </NodeShell>
    </>
  );
}
