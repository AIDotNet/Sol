"use client";

import { type NodeProps, NodeResizer, useReactFlow } from "@xyflow/react";
import { Film, Sparkles } from "lucide-react";
import { useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { fixedVideoResolution } from "@/features/ai/video-models";
import { runVideoNode } from "@/features/canvas/execution";
import { ChipRow, ModelPicker, useSelection } from "@/features/canvas/nodes/model-picker";
import { NodeShell } from "@/features/canvas/nodes/node-shell";
import { useCanvasStore } from "@/features/canvas/store";
import {
  DEFAULT_SEEDANCE_INPUT_MODE,
  DEFAULT_VIDEO_DURATION,
  isValidVideoDuration,
  NODE_MIN_SIZE,
  type VideoGenNodeData,
  VIDEO_DURATION_MAX,
  VIDEO_DURATION_MIN,
} from "@/features/canvas/types";
import { cn } from "@/lib/utils";

const RESOLUTIONS = ["480p", "720p", "1080p"] as const;
const FPS_OPTIONS = [16, 24] as const;
const VIDEO_ASPECTS = ["16:9", "9:16", "1:1"] as const;
const SEEDANCE_INPUT_MODES = ["reference", "first-last"] as const;

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
  const durationSource = nodeData.duration ?? DEFAULT_VIDEO_DURATION;
  const [durationEdit, setDurationEdit] = useState({
    source: durationSource,
    draft: String(durationSource),
  });

  // Prop changes come from undo, loading or another editor. Reset the draft during render rather
  // than in an effect so the input never paints a stale value for a frame.
  if (durationEdit.source !== durationSource) {
    setDurationEdit({ source: durationSource, draft: String(durationSource) });
  }

  const durationDraft =
    durationEdit.source === durationSource ? durationEdit.draft : String(durationSource);
  const parsedDuration = Number(durationDraft);
  const durationValid = durationDraft.trim() !== "" && isValidVideoDuration(parsedDuration);

  const protocol = selection?.protocol;
  const isSeedance = protocol === "seedance-video";
  const seedanceInputMode = nodeData.seedanceInputMode ?? DEFAULT_SEEDANCE_INPUT_MODE;
  const fixedResolution = selection ? fixedVideoResolution(selection.model.modelKey) : null;
  const resolutions = fixedResolution
    ? [fixedResolution]
    : protocol === "xai-video"
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
              {isSeedance && (
                <div className="flex flex-col gap-1">
                  <span className="text-[0.625rem] text-muted-foreground">
                    {t("node.videoInputMode")}
                  </span>
                  <div className="flex flex-wrap gap-1">
                    {SEEDANCE_INPUT_MODES.map((mode) => (
                      <button
                        key={mode}
                        type="button"
                        aria-pressed={seedanceInputMode === mode}
                        onClick={() => updateNodeData(id, { seedanceInputMode: mode })}
                        className={cn(
                          "rounded border px-1.5 py-0.5 text-[0.625rem] transition-colors",
                          seedanceInputMode === mode
                            ? "border-primary bg-primary text-primary-foreground"
                            : "border-border text-muted-foreground hover:bg-accent",
                        )}
                      >
                        {t(
                          mode === "first-last"
                            ? "node.videoInputFirstLast"
                            : "node.videoInputReference",
                        )}
                      </button>
                    ))}
                  </div>
                  <p className="text-[0.5625rem] leading-3 text-muted-foreground">
                    {t(
                      seedanceInputMode === "first-last"
                        ? "node.videoInputFirstLastHint"
                        : "node.videoInputReferenceHint",
                    )}
                  </p>
                </div>
              )}

              <ChipRow
                label={t("node.aspect")}
                options={VIDEO_ASPECTS}
                value={nodeData.aspect ?? "16:9"}
                onChange={(aspect) => updateNodeData(id, { aspect })}
              />

              <ChipRow
                label={t("node.resolution")}
                options={resolutions}
                value={fixedResolution ?? nodeData.resolution ?? "720p"}
                onChange={(resolution) => updateNodeData(id, { resolution })}
              />

              <div className="flex flex-col gap-1">
                <span className="text-[0.625rem] text-muted-foreground">
                  {t("node.durationSeconds")}
                </span>
                <input
                  type="number"
                  min={VIDEO_DURATION_MIN}
                  max={VIDEO_DURATION_MAX}
                  step={1}
                  value={durationDraft}
                  onChange={(event) => {
                    const next = event.target.value;
                    const parsed = Number(next);
                    setDurationEdit({ source: durationSource, draft: next });

                    if (next.trim() !== "" && isValidVideoDuration(parsed)) {
                      updateNodeData(id, { duration: parsed });
                    }
                  }}
                  aria-label={t("node.durationSeconds")}
                  aria-invalid={!durationValid}
                  className="h-6 rounded border border-input bg-transparent px-1.5 text-[0.6875rem] outline-none focus-visible:border-ring aria-invalid:border-destructive"
                />
              </div>

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
            isDisabled={!selection || !durationValid}
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
