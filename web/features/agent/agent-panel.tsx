"use client";

import {
  Bot,
  Brain,
  Check,
  ChevronDown,
  CircleAlert,
  CircleStop,
  ImagePlus,
  Loader2,
  ArrowUp,
  Trash2,
  Wrench,
  X,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipTrigger } from "@/components/ui/tooltip";
import { ModelIcon } from "@/features/ai/provider-icons";
import { uploadAsset } from "@/features/ai/api";
import { resolveSlot, useAiStore } from "@/features/ai/store";
import { resolveProtocol } from "@/features/ai/types";
import { useAgentStore } from "@/features/agent/store";
import {
  isAgentRunActive,
  parseAgentContent,
  type AgentContentBlock,
  type AgentImageAttachment,
  type AgentLiveToolCall,
  type AgentMessage,
} from "@/features/agent/types";
import { useCanvasStore, type CanvasNode } from "@/features/canvas/store";

/** Chat protocols the Agent runtime can drive. Shared with the story dialog's model picker. */
export const AGENT_PROTOCOLS = new Set(["anthropic", "openai-chat", "openai-responses"]);
const MAX_AGENT_IMAGES = 8;
const IMAGE_ACCEPT = "image/png,image/jpeg,image/webp,image/gif";
const IMAGE_TYPES = new Set(["image/png", "image/jpeg", "image/webp", "image/gif"]);

interface ModelOption {
  value: string;
  providerId: string;
  modelKey: string;
  providerName: string;
  providerBuiltinId: string | null | undefined;
  modelName: string;
  modelIcon: string | null | undefined;
  protocol: string;
  supportsVision: boolean;
  supportsThinking: boolean;
  supportsFunctionCall: boolean;
  contextLength: number | null | undefined;
}

interface ToolCallView {
  id: string;
  name: string;
  input: string;
  result: string | null;
  isError: boolean;
}

const TOOL_LABELS = {
  read_canvas: "agent.toolNames.readCanvas",
  get_node_status: "agent.toolNames.getNodeStatus",
  subscribe_node: "agent.toolNames.subscribeNode",
  wait_for_node_event: "agent.toolNames.waitForNodeEvent",
  create_node: "agent.toolNames.createNode",
  update_node: "agent.toolNames.updateNode",
  connect_nodes: "agent.toolNames.connectNodes",
  select_nodes: "agent.toolNames.selectNodes",
  duplicate_nodes: "agent.toolNames.duplicateNodes",
  delete_nodes: "agent.toolNames.deleteNodes",
  disconnect_nodes: "agent.toolNames.disconnectNodes",
  move_nodes: "agent.toolNames.moveNodes",
  resize_node: "agent.toolNames.resizeNode",
  run_node: "agent.toolNames.runNode",
  retry_node: "agent.toolNames.retryNode",
  cancel_node: "agent.toolNames.cancelNode",
  manage_canvas: "agent.toolNames.manageCanvas",
  load_skill: "agent.toolNames.loadSkill",
  run_skill_script: "agent.toolNames.runSkillScript",
  edit_image: "agent.toolNames.editImage",
} as const;

const SERVER_TOOLS = new Set(["load_skill", "run_skill_script"]);

function isServerTool(name: string): boolean {
  return SERVER_TOOLS.has(name) || !TOOL_LABELS[name as keyof typeof TOOL_LABELS];
}

function ModelCapabilityBadges({
  model,
  t,
}: {
  model: ModelOption;
  t: ReturnType<typeof useT>;
}) {
  return (
    <span className="ml-auto flex shrink-0 items-center gap-1 text-[0.5625rem] text-muted-foreground">
      {model.supportsVision && <span title={t("agent.modelVision")}>{t("agent.modelVision")}</span>}
      {model.supportsThinking && <span title={t("agent.modelThinking")}>{t("agent.modelThinking")}</span>}
      {model.supportsFunctionCall && <span title={t("agent.modelTools")}>{t("agent.modelTools")}</span>}
    </span>
  );
}

export function AgentPanel() {
  const t = useT();
  const canvasNodes = useCanvasStore((state) => state.nodes);
  const providers = useAiStore((state) => state.providers);
  const slots = useAiStore((state) => state.slots);
  const setOpen = useAgentStore((state) => state.setOpen);
  const sendPrompt = useAgentStore((state) => state.sendPrompt);
  const clearMessages = useAgentStore((state) => state.clearMessages);
  const cancel = useAgentStore((state) => state.cancel);
  const messages = useAgentStore((state) => state.messages);
  const run = useAgentStore((state) => state.run);
  const liveBlocks = useAgentStore((state) => state.liveBlocks);
  const liveToolCalls = useAgentStore((state) => state.liveToolCalls);
  const loading = useAgentStore((state) => state.loading);
  const sending = useAgentStore((state) => state.sending);
  const clearing = useAgentStore((state) => state.clearing);
  const error = useAgentStore((state) => state.error);

  const [prompt, setPrompt] = useState("");
  const [selectedModel, setSelectedModel] = useState("");
  const [attachments, setAttachments] = useState<AgentImageAttachment[]>([]);
  const [uploading, setUploading] = useState(false);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [clearConfirmOpen, setClearConfirmOpen] = useState(false);
  const endRef = useRef<HTMLDivElement>(null);
  const imageInputRef = useRef<HTMLInputElement>(null);

  const models = useMemo<ModelOption[]>(
    () =>
      providers.flatMap((provider) => {
        if (!provider.enabled || !provider.hasApiKey) return [];
        return provider.models
          .filter(
            (model) =>
              model.enabled &&
              model.category === "chat" &&
              AGENT_PROTOCOLS.has(resolveProtocol(provider, model)),
          )
          .map((model) => ({
            value: model.id,
            providerId: provider.id,
            modelKey: model.modelKey,
            providerName: provider.name,
            providerBuiltinId: provider.builtinId,
            modelName: model.name,
            modelIcon: model.icon,
            protocol: resolveProtocol(provider, model),
            supportsVision: model.supportsVision === true,
            supportsThinking: model.supportsThinking === true,
            supportsFunctionCall: model.supportsFunctionCall === true,
            contextLength: model.contextLength,
          }));
      }),
    [providers],
  );

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => {
      endRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
    });
    return () => window.cancelAnimationFrame(frame);
  }, [messages, liveBlocks, run?.status]);

  const active = isAgentRunActive(run);
  const preferred = resolveSlot(providers, slots.chat);
  const preferredOption = preferred
    ? models.find(
        (option) =>
          option.providerId === preferred.provider.id &&
          option.modelKey === preferred.model.modelKey,
      )
    : undefined;
  const effectiveModel = models.some((option) => option.value === selectedModel)
    ? selectedModel
    : (preferredOption?.value ?? models[0]?.value ?? "");
  const selected = models.find((option) => option.value === effectiveModel) ?? null;
  const canAttachImages = Boolean(selected?.supportsVision && !active && !sending);
  const imagesSupported = !attachments.length || Boolean(selected?.supportsVision);
  const canSend = Boolean(
    prompt.trim() && selected && !active && !sending && !uploading && imagesSupported,
  );
  const toolCalls = useMemo(
    () => mergeToolCalls(collectToolCalls(messages), liveToolCalls),
    [messages, liveToolCalls],
  );
  const conversation = useMemo(() => buildConversationEntries(messages), [messages]);
  const modelGroups = useMemo(() => {
    const groups = new Map<string, { id: string; name: string; models: ModelOption[] }>();
    for (const model of models) {
      const current = groups.get(model.providerId);
      if (current) current.models.push(model);
      else groups.set(model.providerId, {
        id: model.providerId,
        name: model.providerName,
        models: [model],
      });
    }
    return [...groups.values()];
  }, [models]);
  const selectedNodeSummaries = useMemo(
    () => canvasNodes.filter((node) => node.selected).map((node) => summarizeSelectedNode(node, t)),
    [canvasNodes, t],
  );

  function submit() {
    if (!canSend || !selected) return;
    const value = prompt;
    setPrompt("");
    const selectedImages = attachments;
    setAttachments([]);
    setAttachmentError(null);
    void sendPrompt({
      providerId: selected.providerId,
      modelKey: selected.modelKey,
      prompt: value,
      images: selectedImages,
    });
  }

  async function acceptImages(files: FileList | null) {
    if (!files || !canAttachImages) return;
    const available = MAX_AGENT_IMAGES - attachments.length;
    if (available <= 0) {
      setAttachmentError(t("agent.imageLimit"));
      return;
    }

    setAttachmentError(null);
    setUploading(true);
    try {
      for (const file of Array.from(files).slice(0, available)) {
        if (!IMAGE_TYPES.has(file.type.toLowerCase())) {
          setAttachmentError(t("agent.imageTypeError"));
          continue;
        }
        const asset = await uploadAsset(file);
        setAttachments((current) => [
          ...current,
          { id: asset.assetId, url: asset.url, mediaType: asset.mediaType, name: file.name },
        ]);
      }
    } catch (error) {
      setAttachmentError(error instanceof Error ? error.message : t("errors.unknown"));
    } finally {
      setUploading(false);
      if (imageInputRef.current) imageInputRef.current.value = "";
    }
  }

  function removeAttachment(id: string) {
    setAttachments((current) => current.filter((image) => image.id !== id));
    setAttachmentError(null);
  }

  function clearConversation() {
    if (!messages.length || active || sending || clearing) return;
    setClearConfirmOpen(true);
  }

  return (
    <>
        <aside className="canvas-panel absolute top-16 right-4 bottom-4 z-20 flex w-[min(24rem,calc(100%-2rem))] flex-col overflow-hidden rounded-xl border shadow-xl">
          <header className="flex shrink-0 items-center gap-2 border-b px-3 py-2.5">
            <span className="flex size-7 items-center justify-center rounded-lg bg-primary text-primary-foreground">
              <Bot className="size-4" aria-hidden />
            </span>
            <div className="min-w-0 flex-1">
              <h2 className="text-sm font-semibold">{t("agent.title")}</h2>
              <p className="truncate text-[0.6875rem] text-muted-foreground">
                {run ? statusLabel(run.status, t) : t("agent.ready")}
              </p>
            </div>
            <TooltipTrigger>
              <Button
                size="icon-sm"
                variant="ghost"
                onPress={clearConversation}
                isDisabled={clearing || active || sending || messages.length === 0}
                aria-label={t("agent.clear")}
              >
                {clearing ? (
                  <Loader2 className="size-3.5 animate-spin" aria-hidden />
                ) : (
                  <Trash2 className="size-3.5" aria-hidden />
                )}
              </Button>
              <Tooltip>{t("agent.clear")}</Tooltip>
            </TooltipTrigger>
            <TooltipTrigger>
              <Button
                size="icon-sm"
                variant="ghost"
                onPress={() => setOpen(false)}
                aria-label={t("common.close")}
              >
                <X className="size-3.5" aria-hidden />
              </Button>
              <Tooltip>{t("common.close")}</Tooltip>
            </TooltipTrigger>
          </header>

          <div className="min-h-0 flex-1 overflow-y-auto px-3 py-3" aria-live="polite">
            {loading ? (
              <div className="flex h-full items-center justify-center gap-2 text-xs text-muted-foreground">
                <Loader2 className="size-4 animate-spin" aria-hidden />
                {t("common.loading")}
              </div>
            ) : messages.length === 0 && liveBlocks.length === 0 && !active ? (
              <div className="flex h-full flex-col items-center justify-center px-6 text-center">
                <Bot className="mb-3 size-8 text-muted-foreground/60" aria-hidden />
                <p className="text-sm font-medium">{t("agent.empty")}</p>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">
                  {t("agent.emptyHint")}
                </p>
              </div>
            ) : (
              <div className="space-y-3">
                {conversation.map((entry, index) => {
                  if (entry.kind === "user") {
                    return <MessageBubble key={entry.id} index={index} message={entry.message} />;
                  }

                  const currentRun = run?.id === entry.runId;
                  return (
                    <AssistantCard
                      key={entry.id}
                      index={index}
                      blocks={entry.blocks}
                      liveBlocks={currentRun ? liveBlocks : []}
                      live={currentRun && active}
                      toolCalls={toolCalls}
                      t={t}
                    />
                  );
                })}

                {run
                  && !conversation.some(
                    (entry) => entry.kind === "assistant" && entry.runId === run.id,
                  )
                  && (active || liveBlocks.length > 0) && (
                    <AssistantCard
                      key={`assistant-${run.id}`}
                      index={conversation.length}
                      blocks={[]}
                      liveBlocks={liveBlocks}
                      live={active}
                      toolCalls={toolCalls}
                      t={t}
                    />
                  )}
                <div ref={endRef} />
              </div>
            )}
          </div>

          <footer className="shrink-0 border-t bg-background/95 p-3">
            {error && (
              <p className="mb-2 rounded-md bg-destructive/10 px-2.5 py-2 text-xs text-destructive">
                {error}
              </p>
            )}

            <input
              ref={imageInputRef}
              type="file"
              accept={IMAGE_ACCEPT}
              multiple
              className="hidden"
              onChange={(event) => void acceptImages(event.target.files)}
            />

            <div className="rounded-xl border bg-background p-2 focus-within:border-ring focus-within:ring-2 focus-within:ring-ring/30">
              {selectedNodeSummaries.length > 1 && (
                <div
                  data-testid="agent-selected-nodes"
                  className="mb-2 border-b border-border/70 pb-2"
                  aria-label={t("agent.selectedNodes", { count: selectedNodeSummaries.length })}
                >
                  <p className="text-[0.625rem] font-medium text-muted-foreground">
                    {t("agent.selectedNodes", { count: selectedNodeSummaries.length })}
                  </p>
                  <div className="mt-1.5 flex max-h-16 flex-wrap gap-1 overflow-y-auto">
                    {selectedNodeSummaries.map((node) => (
                      <span
                        key={node.id}
                        title={node.detail ? `${node.typeLabel}: ${node.detail}` : node.typeLabel}
                        className="inline-flex max-w-full items-center gap-1 rounded-md bg-muted/70 px-1.5 py-1 text-[0.625rem]"
                      >
                        <span className="shrink-0 font-medium text-foreground">{node.typeLabel}</span>
                        {node.detail && (
                          <span className="min-w-0 truncate text-muted-foreground">{node.detail}</span>
                        )}
                      </span>
                    ))}
                  </div>
                </div>
              )}
              <Textarea
                value={prompt}
                onChange={(event) => setPrompt(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === "Enter" && !event.shiftKey) {
                    event.preventDefault();
                    submit();
                  }
                }}
                disabled={active || sending || models.length === 0}
                placeholder={t("agent.placeholder")}
                rows={3}
                aria-label={t("agent.placeholder")}
                className="block max-h-40 min-h-16 resize-none border-0 bg-transparent px-1 py-0 text-sm outline-none focus-visible:ring-0 disabled:opacity-50"
              />
              {attachments.length > 0 && (
                <div className="mt-2 flex flex-wrap gap-1.5 border-t pt-2">
                  {attachments.map((image) => (
                    <div
                      key={image.id}
                      className="group relative size-12 overflow-hidden rounded-md border bg-muted"
                    >
                      {/* eslint-disable-next-line @next/next/no-img-element */}
                      <img src={image.url} alt={image.name} className="size-full object-cover" />
                      <TooltipTrigger>
                        <Button
                          size="icon-xs"
                          variant="destructive"
                          className="absolute top-0.5 right-0.5 size-4 opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
                          onPress={() => removeAttachment(image.id)}
                          aria-label={t("agent.removeImage")}
                        >
                          <X className="size-2.5" aria-hidden />
                        </Button>
                        <Tooltip>{t("agent.removeImage")}</Tooltip>
                      </TooltipTrigger>
                    </div>
                  ))}
                </div>
              )}
              {(attachmentError || attachments.length > 0 || selected) && (
                <div className="mt-2 flex min-h-7 items-center justify-between gap-2">
                  <div className="min-w-0 text-[0.625rem] text-muted-foreground">
                    {attachmentError ? (
                      <span className="text-destructive">{attachmentError}</span>
                    ) : selected?.supportsVision ? (
                      <span>{t("agent.imageSupportHint")}</span>
                    ) : attachments.length > 0 ? (
                      <span className="text-amber-600">{t("agent.imageUnsupported")}</span>
                    ) : null}
                  </div>
                  <Button
                    size="xs"
                    variant="ghost"
                    onPress={() => imageInputRef.current?.click()}
                    isDisabled={!canAttachImages || uploading || attachments.length >= MAX_AGENT_IMAGES}
                  >
                    {uploading ? (
                      <Loader2 className="size-3 animate-spin" aria-hidden />
                    ) : (
                      <ImagePlus className="size-3.5" aria-hidden />
                    )}
                    {t("agent.attachImage")}
                  </Button>
                </div>
              )}
              <div className="mt-1 flex items-center gap-1.5">
                <Select
                  selectedKey={models.length === 0 ? "none" : effectiveModel || null}
                  onSelectionChange={(key) => {
                    if (key !== null) setSelectedModel(String(key));
                  }}
                  isDisabled={active || sending || models.length === 0}
                  className="min-w-0 flex-1"
                  aria-label={t("agent.model")}
                >
                  <SelectTrigger
                    size="sm"
                    className="h-7 min-w-0 border-0 bg-transparent px-1 text-xs shadow-none hover:bg-muted/60 focus-visible:border-transparent focus-visible:ring-0 dark:bg-transparent dark:hover:bg-muted/40"
                  >
                    <SelectValue>
                      {selected ? (
                        <span className="flex min-w-0 items-center gap-1.5">
                          <ModelIcon
                            modelKey={selected.modelKey}
                            icon={selected.modelIcon}
                            builtinId={selected.providerBuiltinId}
                            size={14}
                          />
                          <span className="truncate">{selected.modelName}</span>
                        </span>
                      ) : (
                        t("agent.noModel")
                      )}
                    </SelectValue>
                  </SelectTrigger>
                  <SelectContent>
                    {models.length === 0 ? (
                      <SelectItem id="none" isDisabled>
                        {t("agent.noModel")}
                      </SelectItem>
                    ) : modelGroups.map((group) => (
                      <SelectGroup key={group.id}>
                        <SelectLabel>{group.name}</SelectLabel>
                        {group.models.map((model) => (
                          <SelectItem
                            key={model.value}
                            id={model.value}
                            textValue={model.modelName}
                          >
                            <ModelIcon
                              modelKey={model.modelKey}
                              icon={model.modelIcon}
                              builtinId={model.providerBuiltinId}
                              size={15}
                            />
                            <span className="min-w-0 truncate">{model.modelName}</span>
                            <ModelCapabilityBadges model={model} t={t} />
                          </SelectItem>
                        ))}
                      </SelectGroup>
                    ))}
                  </SelectContent>
                </Select>
                {active ? (
                  <Button size="sm" variant="destructive" onPress={() => void cancel()}>
                    <CircleStop className="size-3.5" aria-hidden />
                    {t("agent.stop")}
                  </Button>
                ) : (
                  <TooltipTrigger>
                    <Button
                      size="icon-sm"
                      className="rounded-full"
                      onPress={submit}
                      isDisabled={!canSend}
                      aria-label={t("agent.send")}
                    >
                      {sending ? (
                        <Loader2 className="size-3.5 animate-spin" aria-hidden />
                      ) : (
                        <ArrowUp className="size-3.5" aria-hidden />
                      )}
                    </Button>
                    <Tooltip>{t("agent.send")}</Tooltip>
                  </TooltipTrigger>
                )}
              </div>
            </div>
          </footer>
      </aside>
      <ConfirmDialog
        isOpen={clearConfirmOpen}
        onOpenChange={setClearConfirmOpen}
        title={t("agent.clear")}
        description={t("agent.clearConfirm")}
        confirmLabel={t("agent.clear")}
        onConfirm={() => clearMessages()}
      />
    </>
  );
}

interface UserConversationEntry {
  kind: "user";
  id: string;
  message: AgentMessage;
}

interface AssistantConversationEntry {
  kind: "assistant";
  id: string;
  runId: string | null;
  blocks: AgentContentBlock[];
}

type ConversationEntry = UserConversationEntry | AssistantConversationEntry;

function buildConversationEntries(messages: AgentMessage[]): ConversationEntry[] {
  const entries: ConversationEntry[] = [];
  const assistantGroups = new Map<string, AssistantConversationEntry>();

  for (const message of messages) {
    const blocks = parseAgentContent(message.contentJson);
    if (message.role === "assistant") {
      const groupId = message.runId ?? message.id;
      let group = assistantGroups.get(groupId);
      if (!group) {
        group = {
          kind: "assistant",
          id: `assistant-${groupId}`,
          runId: message.runId,
          blocks: [],
        };
        assistantGroups.set(groupId, group);
        entries.push(group);
      }
      group.blocks.push(...blocks);
      continue;
    }

    if (hasVisibleUserContent(blocks)) {
      entries.push({
        kind: "user",
        id: `user-${message.runId ?? message.id}`,
        message,
      });
    }
  }

  return entries;
}

function hasVisibleUserContent(blocks: AgentContentBlock[]): boolean {
  return blocks.some(
    (block) =>
      (block.kind === "text" && Boolean(block.text))
      || (block.kind === "image" && Boolean(block.imageUrl)),
  );
}

function MessageBubble({
  index,
  message,
}: {
  index: number;
  message: AgentMessage;
}) {
  const blocks = parseAgentContent(message.contentJson);
  const text = blocks
    .filter((block) => block.kind === "text" && block.text)
    .map((block) => block.text)
    .join("\n");
  const images = blocks.filter((block) => block.kind === "image" && block.imageUrl);
  if (!text && images.length === 0) return null;

  return (
    <div
      style={{ animationDelay: Math.min(index * 35, 280) + "ms" }}
      className="agent-message-enter ml-auto max-w-[88%] rounded-xl rounded-br-sm bg-primary px-3 py-2 text-sm text-primary-foreground"
    >
      {images.length > 0 && (
        <div className="mb-2 flex flex-wrap gap-1.5">
          {images.map((image) => (
            <div key={image.imageUrl} className="overflow-hidden rounded-md border border-white/20">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={image.imageUrl!}
                alt=""
                className="max-h-32 max-w-32 object-cover"
              />
            </div>
          ))}
        </div>
      )}
      {text && <p className="whitespace-pre-wrap break-words leading-6">{text}</p>}
    </div>
  );
}

export function AssistantCard({
  index,
  blocks,
  liveBlocks = [],
  liveText = "",
  liveThinking = "",
  live,
  toolCalls,
  t,
}: {
  index: number;
  blocks: AgentContentBlock[];
  liveBlocks?: AgentContentBlock[];
  /** Kept for callers from before liveBlocks was introduced. */
  liveText?: string;
  liveThinking?: string;
  live: boolean;
  toolCalls: Map<string, ToolCallView>;
  t: ReturnType<typeof useT>;
}) {
  const compatibilityBlocks: AgentContentBlock[] = [
    ...(liveThinking ? [{ kind: "thinking" as const, text: liveThinking }] : []),
    ...(liveText ? [{ kind: "text" as const, text: liveText }] : []),
  ];
  const runtimeBlocks = liveBlocks.length > 0 ? liveBlocks : compatibilityBlocks;
  const displayBlocks = mergeDisplayBlocks(blocks, runtimeBlocks).filter(
    (block) =>
      (block.kind === "text" && Boolean(block.text))
      || (block.kind === "thinking" && Boolean(block.text))
      || (block.kind === "tool_use" && Boolean(block.toolUseId)),
  );
  if (displayBlocks.length === 0 && !live) {
    return null;
  }

  return (
    <article
      style={{ animationDelay: Math.min(index * 35, 280) + "ms" }}
      className="agent-message-enter max-w-[92%] rounded-xl rounded-bl-sm border bg-card px-3 py-2 text-sm shadow-sm"
    >
      <div className="space-y-2">
        {displayBlocks.map((block, blockIndex) => {
          if (block.kind === "text") {
            return (
              <p
                key={`${block.kind}-${blockIndex}`}
                data-agent-block="text"
                className="whitespace-pre-wrap break-words leading-6"
              >
                {block.text}
              </p>
            );
          }

          if (block.kind === "thinking") {
            return (
              <div key={`${block.kind}-${blockIndex}`} data-agent-block="thinking">
                <ThinkingBlock text={block.text} live={live} t={t} />
              </div>
            );
          }

          const tool = toolCalls.get(block.toolUseId!);
          if (!tool) return null;
          return (
            <div key={tool.id} data-agent-block="tool">
              <ToolCallCard call={tool} index={blockIndex} t={t} />
            </div>
          );
        })}

        {displayBlocks.length === 0 && live && (
          <span data-agent-block="working" className="flex items-center gap-1.5 text-xs text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" aria-hidden />
            {t("agent.working")}
          </span>
        )}
      </div>
    </article>
  );
}

function ThinkingBlock({
  text,
  live = false,
  t,
}: {
  text?: string | null;
  live?: boolean;
  t: ReturnType<typeof useT>;
}) {
  const hasText = Boolean(text);
  return (
    <details
      className={
        live
          ? "agent-thinking-live group rounded-lg border px-2.5 py-2 text-xs"
          : "group rounded-lg border border-border/70 bg-muted/25 px-2.5 py-2 text-xs"
      }
      open={live || undefined}
    >
      <summary className="flex cursor-pointer list-none items-center gap-2 select-none">
        <span className="flex size-5 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
          <Brain className="size-3.5" aria-hidden />
        </span>
        <span className="font-medium">{live ? t("agent.thinkingLive") : t("agent.thinking")}</span>
        {live && (
          <span className="ml-auto flex items-center gap-1 text-[0.625rem] text-muted-foreground">
            <span className="agent-thinking-dot size-1.5 rounded-full bg-primary" aria-hidden />
            <span>{t("agent.working")}</span>
          </span>
        )}
        {hasText && (
          <ChevronDown
            className="ml-auto size-3.5 text-muted-foreground transition-transform duration-200 group-open:rotate-180"
            aria-hidden
          />
        )}
      </summary>
      {hasText && (
        <p className="mt-2 border-l-2 border-primary/20 pl-2 whitespace-pre-wrap leading-5 text-muted-foreground">
          {text}
        </p>
      )}
    </details>
  );
}

function ToolCallCard({
  call,
  index,
  t,
}: {
  call: ToolCallView;
  index: number;
  t: ReturnType<typeof useT>;
}) {
  const running = call.result === null;
  const label = toolLabel(call.name, t);
  const status = running ? "running" : call.isError ? "failed" : "succeeded";
  const statusLabel = running
    ? t("agent.toolRunning")
    : call.isError
      ? t("agent.toolFailed")
      : t("agent.toolSucceeded");

  return (
    <details
      className="agent-tool-enter group rounded-lg border border-border/70 bg-background/55 text-xs"
      style={{ animationDelay: Math.min(index * 45, 180) + "ms" }}
      open={running}
    >
      <summary className="flex cursor-pointer list-none items-center gap-2 px-2.5 py-2 select-none">
        <span className="flex size-6 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground">
          <Wrench className="size-3.5" aria-hidden />
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex min-w-0 items-center gap-1.5">
            <span className="truncate font-medium">{label}</span>
            <span className="shrink-0 rounded bg-muted px-1 py-0.5 text-[0.5625rem] text-muted-foreground">
              {isServerTool(call.name) ? t("agent.toolServer") : t("agent.toolCanvas")}
            </span>
          </span>
          <span className="mt-0.5 flex items-center gap-1 text-[0.625rem] text-muted-foreground">
            <ToolStatusIcon status={status} />
            {statusLabel}
          </span>
        </span>
        <ChevronDown
          className="size-3.5 shrink-0 text-muted-foreground transition-transform duration-200 group-open:rotate-180"
          aria-hidden
        />
      </summary>
      <div className="space-y-2 border-t px-2.5 py-2">
        <PayloadPreview label={t("agent.toolInput")} value={call.input} />
        {call.result !== null && (
          <PayloadPreview label={t("agent.toolOutput")} value={call.result} error={call.isError} />
        )}
      </div>
    </details>
  );
}

function ToolStatusIcon({ status }: { status: "running" | "succeeded" | "failed" }) {
  if (status === "running") return <Loader2 className="size-3 animate-spin" aria-hidden />;
  if (status === "failed") return <CircleAlert className="size-3 text-destructive" aria-hidden />;
  return <Check className="size-3 text-emerald-600" aria-hidden />;
}

function PayloadPreview({
  label,
  value,
  error = false,
}: {
  label: string;
  value: string;
  error?: boolean;
}) {
  return (
    <div>
      <p className="mb-1 text-[0.625rem] font-medium uppercase tracking-wide text-muted-foreground">
        {label}
      </p>
      <pre
        className={
          error
            ? "max-h-28 overflow-auto whitespace-pre-wrap break-words rounded-md bg-destructive/10 px-2 py-1.5 font-mono text-[0.625rem] leading-4 text-destructive"
            : "max-h-28 overflow-auto whitespace-pre-wrap break-words rounded-md bg-muted/60 px-2 py-1.5 font-mono text-[0.625rem] leading-4 text-muted-foreground"
        }
      >
        {formatPayload(value)}
      </pre>
    </div>
  );
}

function collectToolCalls(messages: AgentMessage[]): Map<string, ToolCallView> {
  const calls = new Map<string, ToolCallView>();
  for (const message of messages) {
    for (const block of parseAgentContent(message.contentJson)) {
      if (block.kind === "tool_use" && block.toolUseId) {
        calls.set(block.toolUseId, {
          id: block.toolUseId,
          name: block.toolName ?? "tool",
          input: block.json ?? "{}",
          result: null,
          isError: false,
        });
      } else if (block.kind === "tool_result" && block.toolUseId) {
        const call = calls.get(block.toolUseId);
        if (call) {
          call.result = block.json ?? block.text ?? "";
          call.isError = block.isError === true;
        }
      }
    }
  }
  return calls;
}

function mergeDisplayBlocks(
  stored: AgentContentBlock[],
  live: AgentContentBlock[],
): AgentContentBlock[] {
  const result = [...stored];
  const storedToolIds = new Set(
    stored
      .filter((block) => block.kind === "tool_use" && block.toolUseId)
      .map((block) => block.toolUseId),
  );

  for (const block of live) {
    if (block.kind === "tool_use" && block.toolUseId) {
      if (storedToolIds.has(block.toolUseId)) continue;
      storedToolIds.add(block.toolUseId);
    }
    result.push(block);
  }
  return result;
}

function mergeToolCalls(
  stored: Map<string, ToolCallView>,
  live: AgentLiveToolCall[],
): Map<string, ToolCallView> {
  const calls = new Map(stored);
  for (const tool of live) {
    calls.set(tool.id, {
      id: tool.id,
      name: tool.name,
      input: tool.input,
      result: tool.result,
      isError: tool.isError,
    });
  }
  return calls;
}

function toolLabel(name: string, t: ReturnType<typeof useT>): string {
  const translationKey = TOOL_LABELS[name as keyof typeof TOOL_LABELS];
  return translationKey
    ? t(translationKey)
    : name.replaceAll("_", " ").replace(/\b\w/g, (character) => character.toUpperCase());
}

function formatPayload(value: string): string {
  try {
    const formatted = JSON.stringify(JSON.parse(value), null, 2);
    return formatted.length > 2_000 ? formatted.slice(0, 2_000) + "…" : formatted;
  } catch {
    return value.length > 2_000 ? value.slice(0, 2_000) + "…" : value;
  }
}

interface SelectedNodeSummary {
  id: string;
  typeLabel: string;
  detail?: string;
}

function summarizeSelectedNode(
  node: CanvasNode,
  t: ReturnType<typeof useT>,
): SelectedNodeSummary {
  const data = node.data as Record<string, unknown>;
  const typeLabel = (() => {
    switch (node.type) {
      case "image":
        return t("canvas.nodeImage");
      case "video":
        return t("canvas.nodeVideo");
      case "imageGen":
        return t("canvas.nodeImageGen");
      case "videoGen":
        return t("canvas.nodeVideoGen");
      default:
        return t("canvas.nodeText");
    }
  })();

  const detail = node.type === "text" || node.type === "image" || node.type === "video"
    ? compactNodeText(data.text ?? data.prompt)
    : compactNodeText(data.modelId);

  return { id: node.id, typeLabel, detail };
}

function compactNodeText(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const compact = value.trim().replace(/\s+/g, " ");
  if (!compact) return undefined;
  return compact.length > 72 ? `${compact.slice(0, 72)}…` : compact;
}

function statusLabel(
  status: NonNullable<ReturnType<typeof useAgentStore.getState>["run"]>["status"],
  t: ReturnType<typeof useT>,
): string {
  switch (status) {
    case "queued":
      return t("agent.statusQueued");
    case "running":
      return t("agent.statusRunning");
    case "awaiting_canvas":
      return t("agent.statusAwaitingCanvas");
    case "awaiting_approval":
      return t("agent.statusAwaitingApproval");
    case "succeeded":
      return t("agent.statusSucceeded");
    case "failed":
      return t("agent.statusFailed");
    case "cancelled":
      return t("agent.statusCancelled");
    case "interrupted":
      return t("agent.statusInterrupted");
  }
}
