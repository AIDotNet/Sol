"use client";

import { type NodeProps, NodeResizer, useReactFlow } from "@xyflow/react";
import { Sparkles, Wand2 } from "lucide-react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ChipRow, ModelPicker, useSelection } from "@/features/canvas/nodes/model-picker";
import { NodeShell } from "@/features/canvas/nodes/node-shell";
import { useCanvasStore } from "@/features/canvas/store";
import { runImageNode } from "@/features/canvas/execution";
import {
  ASPECT_RATIOS,
  DEFAULT_IMAGE_OUTPUT_FORMAT,
  DEFAULT_IMAGE_RESPONSE_FORMAT,
  IMAGE_OUTPUT_FORMATS,
  IMAGE_RESPONSE_FORMATS,
  type ImageGenNodeData,
  NODE_MIN_SIZE,
} from "@/features/canvas/types";

const QUALITIES = ["auto", "low", "medium", "high"] as const;
const COUNTS = [1, 2, 3, 4] as const;

/**
 * Image generation config.
 *
 * Which parameters are shown depends on the resolved protocol, not on the provider: quality,
 * output format and response format are all OpenAI Images concepts that Gemini rejects, so
 * offering them there would produce a request the upstream refuses.
 */
export function ImageGenNode({ id, data, selected }: NodeProps) {
  const t = useT();
  const updateNodeData = useCanvasStore((state) => state.updateNodeData);
  const { getNodes, getEdges } = useReactFlow();

  const nodeData = data as ImageGenNodeData;
  const selection = useSelection(nodeData.providerId, nodeData.modelId);

  const isOpenAiImages = selection?.protocol === "openai-images";
  const supportsSeed = selection?.protocol !== "openai-images";

  return (
    <>
      <NodeResizer
        isVisible={selected}
        minWidth={NODE_MIN_SIZE.width}
        minHeight={220}
        lineClassName="!border-ring"
        handleClassName="!size-2 !rounded-sm !border-ring !bg-background"
      />

      <NodeShell
        title={t("canvas.nodeImageGen")}
        icon={<Wand2 className="size-3 shrink-0 text-muted-foreground" aria-hidden />}
        selected={selected}
        // Only ever a failure that never reached a node of its own; a run in progress lives on
        // the image nodes it created.
        execution={nodeData.execution}
        onRetry={() => void runImageNode(id, getNodes(), getEdges())}
      >
        <div className="nodrag nowheel flex min-h-0 flex-1 flex-col gap-2.5 overflow-y-auto p-2">
          <ModelPicker
            category="image"
            providerId={nodeData.providerId}
            modelId={nodeData.modelId}
            onChange={(next) => updateNodeData(id, next)}
          />

          {selection && (
            <>
              <ChipRow
                label={t("node.aspect")}
                options={ASPECT_RATIOS}
                value={nodeData.aspect ?? "1:1"}
                onChange={(aspect) => updateNodeData(id, { aspect })}
              />

              {isOpenAiImages && (
                <>
                  <ChipRow
                    label={t("node.quality")}
                    options={QUALITIES}
                    value={nodeData.quality ?? "auto"}
                    onChange={(quality) => updateNodeData(id, { quality })}
                  />

                  <ChipRow
                    label={t("node.outputFormat")}
                    options={IMAGE_OUTPUT_FORMATS}
                    value={nodeData.outputFormat ?? DEFAULT_IMAGE_OUTPUT_FORMAT}
                    onChange={(outputFormat) => updateNodeData(id, { outputFormat })}
                  />

                  <ChipRow
                    label={t("node.responseFormat")}
                    options={IMAGE_RESPONSE_FORMATS}
                    value={nodeData.responseFormat ?? DEFAULT_IMAGE_RESPONSE_FORMAT}
                    onChange={(responseFormat) => updateNodeData(id, { responseFormat })}
                  />
                </>
              )}

              <ChipRow
                label={t("node.count")}
                options={COUNTS}
                value={nodeData.count ?? 1}
                onChange={(count) => updateNodeData(id, { count })}
              />

              {supportsSeed && (
                <div className="flex flex-col gap-1">
                  <span className="text-[0.625rem] text-muted-foreground">{t("node.seed")}</span>
                  <input
                    type="number"
                    value={nodeData.seed ?? ""}
                    onChange={(event) =>
                      updateNodeData(id, {
                        seed: event.target.value ? Number(event.target.value) : undefined,
                      })
                    }
                    placeholder={t("node.seedPlaceholder")}
                    aria-label={t("node.seed")}
                    className="h-6 rounded border border-input bg-transparent px-1.5 text-[0.6875rem] outline-none focus-visible:border-ring"
                  />
                </div>
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
            onPress={() => void runImageNode(id, getNodes(), getEdges())}
          >
            <Sparkles className="size-3" aria-hidden />
            {t("node.generate")}
          </Button>
        </div>
      </NodeShell>
    </>
  );
}
