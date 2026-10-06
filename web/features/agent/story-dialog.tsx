"use client";

import { Clapperboard } from "lucide-react";
import { useMemo, useState } from "react";
import { useI18n } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { AGENT_PROTOCOLS } from "@/features/agent/agent-panel";
import { useAgentStore } from "@/features/agent/store";
import { isAgentRunActive } from "@/features/agent/types";
import {
  buildStoryPrompt,
  estimateStoryRun,
  MAX_STORY_LENGTH,
  STORY_DURATION_OPTIONS,
  STORY_SHOT_OPTIONS,
  STORY_STYLE_OPTIONS,
  validateStoryOrchestration,
  type StoryOrchestrationOptions,
  type StoryStyle,
} from "@/features/agent/story-orchestration";
import type { ModelSlot } from "@/features/ai/store";
import { usableProviders, useAiStore } from "@/features/ai/store";
import { enabledModels, resolveProtocol } from "@/features/ai/types";

/**
 * The one-click novel → storyboard entry point.
 *
 * Collects the story plus the few parameters the orchestration prompt cannot infer (which models
 * to spend money on, pacing, whether to render videos at all), then hands a fully specified
 * prompt to the existing Agent session and opens its panel — the panel becomes the run log, the
 * canvas becomes the progress visualization. Nothing here generates anything itself.
 */

/** Composite select key; a model is only addressable as provider+model together. */
function modelKey(providerId: string, modelId: string): string {
  return `${providerId}:${modelId}`;
}

interface PickModel {
  providerId: string;
  modelId: string;
  name: string;
}

interface AgentPickModel {
  providerId: string;
  /** The Agent API addresses a chat model by its upstream key, not its config id. */
  modelKey: string;
  name: string;
}

interface PickOption<M> {
  key: string;
  providerName: string;
  model: M;
}

/** Explicit user pick first, then the configured slot, then the first usable option. */
function resolvePick<M>(
  explicit: string | null,
  slot: ModelSlot,
  options: Array<PickOption<M>>,
): PickOption<M> | null {
  if (explicit) return options.find((option) => option.key === explicit) ?? null;

  if (slot.providerId && slot.modelId) {
    const providerId = slot.providerId;
    const modelId = slot.modelId;
    const viaSlot = options.find((option) => option.key === modelKey(providerId, modelId));
    if (viaSlot) return viaSlot;
  }

  return options[0] ?? null;
}

export function StoryDialog({
  isOpen,
  onOpenChange,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const { t, locale } = useI18n();

  const providers = useAiStore((state) => state.providers);
  const slots = useAiStore((state) => state.slots);

  const setAgentOpen = useAgentStore((state) => state.setOpen);
  const sendPrompt = useAgentStore((state) => state.sendPrompt);
  const run = useAgentStore((state) => state.run);
  const sending = useAgentStore((state) => state.sending);
  const runActive = isAgentRunActive(run) || sending;

  const [story, setStory] = useState("");
  const [shotCount, setShotCount] = useState<string>("auto");
  const [secondsPerShot, setSecondsPerShot] = useState<string>("auto");
  const [generateVideos, setGenerateVideos] = useState(false);
  const [style, setStyle] = useState<StoryStyle>("none");
  const [imagePick, setImagePick] = useState<string | null>(null);
  const [videoPick, setVideoPick] = useState<string | null>(null);
  const [agentPick, setAgentPick] = useState<string | null>(null);

  const imageOptions = useMemo<PickOption<PickModel>[]>(
    () =>
      usableProviders(providers, "image").flatMap((provider) =>
        enabledModels(provider, "image").map((model) => ({
          key: modelKey(provider.id, model.id),
          providerName: provider.name,
          model: { providerId: provider.id, modelId: model.id, name: model.name },
        })),
      ),
    [providers],
  );

  const videoOptions = useMemo<PickOption<PickModel>[]>(
    () =>
      usableProviders(providers, "video").flatMap((provider) =>
        enabledModels(provider, "video").map((model) => ({
          key: modelKey(provider.id, model.id),
          providerName: provider.name,
          model: { providerId: provider.id, modelId: model.id, name: model.name },
        })),
      ),
    [providers],
  );

  // Same membership rule as the Agent panel: chat models the agent runtime can actually drive.
  const agentOptions = useMemo<PickOption<AgentPickModel>[]>(
    () =>
      providers
        .filter((provider) => provider.enabled && provider.hasApiKey)
        .flatMap((provider) =>
          provider.models
            .filter(
              (model) =>
                model.enabled &&
                model.category === "chat" &&
                AGENT_PROTOCOLS.has(resolveProtocol(provider, model)),
            )
            .map((model) => ({
              key: modelKey(provider.id, model.id),
              providerName: provider.name,
              model: { providerId: provider.id, modelKey: model.modelKey, name: model.name },
            })),
        ),
    [providers],
  );

  const resolvedImage = resolvePick(imagePick, slots.image, imageOptions);
  const resolvedVideo = resolvePick(videoPick, slots.video, videoOptions);
  const resolvedAgent = resolvePick(agentPick, slots.chat, agentOptions);

  const options: StoryOrchestrationOptions = {
    story,
    shotCount: shotCount === "auto" ? null : Number(shotCount),
    secondsPerShot: secondsPerShot === "auto" ? null : Number(secondsPerShot),
    generateVideos,
    imageProviderId: resolvedImage?.model.providerId ?? "",
    imageModelId: resolvedImage?.model.modelId ?? "",
    videoProviderId: resolvedVideo?.model.providerId,
    videoModelId: resolvedVideo?.model.modelId,
    style,
    locale,
  };

  const invalid = validateStoryOrchestration(options);
  const estimate = estimateStoryRun(options.shotCount, generateVideos);
  const noChatModel = agentOptions.length === 0;

  function start() {
    if (invalid || !resolvedAgent || runActive) return;

    const prompt = buildStoryPrompt(options);
    onOpenChange(false);
    setAgentOpen(true);
    void sendPrompt({
      providerId: resolvedAgent.model.providerId,
      modelKey: resolvedAgent.model.modelKey,
      prompt,
    });
  }

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      className="w-[min(44rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("story.title")}</DialogTitle>
        <DialogDescription>{t("story.description")}</DialogDescription>
      </DialogHeader>

      <div className="flex max-h-[60vh] flex-col gap-3 overflow-y-auto">
        <div className="flex flex-col gap-1">
          <span className="text-[0.6875rem] text-muted-foreground">{t("story.storyLabel")}</span>
          <Textarea
            value={story}
            onChange={(event) => setStory(event.target.value)}
            maxLength={MAX_STORY_LENGTH}
            placeholder={t("story.storyPlaceholder")}
            aria-label={t("story.storyLabel")}
            className="max-h-64 min-h-28 resize-y"
          />
          <span className="self-end text-[0.625rem] tabular-nums text-muted-foreground">
            {story.length}/{MAX_STORY_LENGTH}
          </span>
        </div>

        <div className="grid grid-cols-2 gap-3 max-sm:grid-cols-1">
          <LabeledSelect
            label={t("story.shotCount")}
            value={shotCount}
            onChange={setShotCount}
            ariaLabel={t("story.shotCount")}
          >
            <SelectItem id="auto">{t("common.auto")}</SelectItem>
            {STORY_SHOT_OPTIONS.map((count) => (
              <SelectItem key={count} id={String(count)}>{count}</SelectItem>
            ))}
          </LabeledSelect>

          <LabeledSelect
            label={t("story.duration")}
            value={secondsPerShot}
            onChange={setSecondsPerShot}
            ariaLabel={t("story.duration")}
          >
            <SelectItem id="auto">{t("common.auto")}</SelectItem>
            {STORY_DURATION_OPTIONS.map((seconds) => (
              <SelectItem key={seconds} id={String(seconds)}>
                {t("node.seconds", { n: seconds })}
              </SelectItem>
            ))}
          </LabeledSelect>

          <LabeledSelect
            label={t("story.imageModel")}
            value={resolvedImage?.key ?? null}
            onChange={setImagePick}
            ariaLabel={t("story.imageModel")}
            placeholder={imageOptions.length === 0 ? t("node.noImageModel") : undefined}
          >
            {imageOptions.map((option) => (
              <SelectItem key={option.key} id={option.key} textValue={option.model.name}>
                {option.model.name}
              </SelectItem>
            ))}
          </LabeledSelect>

          <LabeledSelect
            label={t("story.videoModel")}
            value={resolvedVideo?.key ?? null}
            onChange={setVideoPick}
            ariaLabel={t("story.videoModel")}
            isDisabled={!generateVideos}
            placeholder={videoOptions.length === 0 ? t("node.noVideoModel") : undefined}
          >
            {videoOptions.map((option) => (
              <SelectItem key={option.key} id={option.key} textValue={option.model.name}>
                {option.model.name}
              </SelectItem>
            ))}
          </LabeledSelect>

          <LabeledSelect
            label={t("story.styleLabel")}
            value={style}
            onChange={(key) => setStyle(key as StoryStyle)}
            ariaLabel={t("story.styleLabel")}
          >
            <SelectItem id="none">{t("story.styleNone")}</SelectItem>
            {STORY_STYLE_OPTIONS.map((option) => (
              <SelectItem key={option} id={option}>{t(`story.style.${option}`)}</SelectItem>
            ))}
          </LabeledSelect>

          <LabeledSelect
            label={t("story.agentModel")}
            value={resolvedAgent?.key ?? null}
            onChange={setAgentPick}
            ariaLabel={t("story.agentModel")}
            placeholder={noChatModel ? t("agent.noModel") : undefined}
          >
            {agentOptions.map((option) => (
              <SelectItem key={option.key} id={option.key} textValue={option.model.name}>
                {option.model.name}
              </SelectItem>
            ))}
          </LabeledSelect>
        </div>

        <label className="flex cursor-pointer items-center justify-between gap-3 rounded-lg border px-3 py-2">
          <span className="flex flex-col">
            <span className="text-xs font-medium">{t("story.generateVideos")}</span>
            <span className="text-[0.625rem] text-muted-foreground">
              {t("story.generateVideosHint")}
            </span>
          </span>
          <Switch
            isSelected={generateVideos}
            onChange={setGenerateVideos}
            aria-label={t("story.generateVideos")}
          />
        </label>

        <div className="flex min-h-4 items-center justify-between gap-3 text-[0.6875rem] text-muted-foreground">
          <span>
            {t("story.costEstimate", { images: estimate.images, videos: estimate.videos })}
          </span>
          {runActive && (
            <span className="shrink-0 text-amber-600 dark:text-amber-400">{t("story.runActive")}</span>
          )}
          {!runActive && noChatModel && (
            <span className="shrink-0 text-destructive">{t("agent.noModel")}</span>
          )}
          {!runActive && !noChatModel && invalid && (
            <span className="shrink-0 text-destructive">{t(`story.error.${invalid}`)}</span>
          )}
        </div>
      </div>

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        <Button size="sm" isDisabled={Boolean(invalid) || runActive || noChatModel} onPress={start}>
          <Clapperboard className="size-3.5" aria-hidden />
          {t("story.start")}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}

function LabeledSelect({
  label,
  value,
  onChange,
  ariaLabel,
  placeholder,
  isDisabled,
  children,
}: {
  label: string;
  value: string | null;
  onChange: (key: string) => void;
  ariaLabel: string;
  placeholder?: string;
  isDisabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[0.6875rem] text-muted-foreground">{label}</span>
      <Select
        selectedKey={value}
        onSelectionChange={(key) => {
          if (key !== null) onChange(String(key));
        }}
        isDisabled={isDisabled}
        aria-label={ariaLabel}
        className="w-full"
      >
        <SelectTrigger size="sm" className="h-7 w-full text-xs">
          <SelectValue>
            {value === null && placeholder ? (
              <span className="text-muted-foreground">{placeholder}</span>
            ) : undefined}
          </SelectValue>
        </SelectTrigger>
        <SelectContent>{children}</SelectContent>
      </Select>
    </div>
  );
}
