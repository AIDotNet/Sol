"use client";

import { useMemo } from "react";
import { useT } from "@/components/providers/i18n-provider";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { ModelIcon, ProviderIcon } from "@/features/ai/provider-icons";
import { usableProviders, useAiStore } from "@/features/ai/store";
import { type AiModel, type AiProvider, type ModelCategory, resolveProtocol } from "@/features/ai/types";
import { cn } from "@/lib/utils";

/**
 * Two-step provider → model picker.
 *
 * Deliberately two steps rather than one flat model list: a model id alone is ambiguous once the
 * same model is reachable through several providers, and which provider is used decides the
 * credentials, the endpoint, and the protocol.
 */
export function ModelPicker({
  category,
  providerId,
  modelId,
  onChange,
}: {
  category: ModelCategory;
  providerId?: string;
  modelId?: string;
  onChange: (next: { providerId?: string; modelId?: string }) => void;
}) {
  const t = useT();
  const providers = useAiStore((state) => state.providers);

  const available = useMemo(
    () => usableProviders(providers, category),
    [providers, category],
  );

  const provider = available.find((candidate) => candidate.id === providerId);
  const models = useMemo(
    () =>
      provider
        ? provider.models.filter((model) => model.enabled && model.category === category)
        : [],
    [provider, category],
  );

  if (available.length === 0) {
    return (
      <p className="rounded-md bg-muted/50 px-2 py-1.5 text-[0.6875rem] text-muted-foreground">
        {t("node.noProvider")}
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-1.5">
      <Select
        selectedKey={providerId ?? null}
        onSelectionChange={(key) => {
          // Clearing the model matters: the previous one belongs to a different provider and
          // would silently point at a model this provider does not serve.
          onChange({ providerId: String(key), modelId: undefined });
        }}
        className="w-full"
        aria-label={t("node.provider")}
      >
        <SelectTrigger size="sm" className="text-[0.6875rem]">
          <SelectValue>
            {provider ? (
              <span className="flex items-center gap-1.5">
                <ProviderIcon
                  builtinId={provider.builtinId}
                  icon={provider.icon}
                  name={provider.name}
                  size={13}
                />
                {provider.name}
              </span>
            ) : (
              t("node.selectProvider")
            )}
          </SelectValue>
        </SelectTrigger>
        <SelectContent>
          {available.map((candidate) => (
            <SelectItem key={candidate.id} id={candidate.id}>
              <ProviderIcon
                builtinId={candidate.builtinId}
                icon={candidate.icon}
                name={candidate.name}
                size={14}
              />
              {candidate.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>

      <Select
        selectedKey={modelId ?? null}
        onSelectionChange={(key) => onChange({ providerId, modelId: String(key) })}
        isDisabled={!provider || models.length === 0}
        className="w-full"
        aria-label={t("node.model")}
      >
        <SelectTrigger size="sm" className="text-[0.6875rem]">
          <SelectValue>
            {(() => {
              const model = models.find((candidate) => candidate.id === modelId);
              if (!model) {
                return !provider
                  ? t("node.selectModel")
                  : models.length === 0
                    ? t(category === "video" ? "node.noVideoModel" : "node.noImageModel")
                    : t("node.selectModel");
              }

              return (
                <span className="flex items-center gap-1.5">
                  <ModelIcon
                    modelKey={model.modelKey}
                    icon={model.icon}
                    builtinId={provider?.builtinId}
                    size={13}
                  />
                  {model.name}
                </span>
              );
            })()}
          </SelectValue>
        </SelectTrigger>
        <SelectContent>
          {models.map((model) => (
            <SelectItem key={model.id} id={model.id}>
              <ModelIcon
                modelKey={model.modelKey}
                icon={model.icon}
                builtinId={provider?.builtinId}
                size={14}
              />
              {model.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </div>
  );
}

/** Resolves a node's stored ids into the live provider and model, or null if either is stale. */
export function useSelection(
  providerId: string | undefined,
  modelId: string | undefined,
): { provider: AiProvider; model: AiModel; protocol: ReturnType<typeof resolveProtocol> } | null {
  const providers = useAiStore((state) => state.providers);

  return useMemo(() => {
    if (!providerId || !modelId) return null;

    const provider = providers.find((candidate) => candidate.id === providerId);
    if (!provider) return null;

    const model = provider.models.find((candidate) => candidate.id === modelId);
    if (!model) return null;

    return { provider, model, protocol: resolveProtocol(provider, model) };
  }, [providers, providerId, modelId]);
}

/** A compact row of mutually exclusive options, used for aspect/quality/resolution. */
export function ChipRow<T extends string | number>({
  label,
  options,
  value,
  onChange,
}: {
  label: string;
  options: readonly T[];
  value: T | undefined;
  onChange: (value: T) => void;
}) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[0.625rem] text-muted-foreground">{label}</span>
      <div className="flex flex-wrap gap-1">
        {options.map((option) => (
          <button
            key={String(option)}
            type="button"
            onClick={() => onChange(option)}
            className={cn(
              "rounded border px-1.5 py-0.5 text-[0.625rem] transition-colors",
              value === option
                ? "border-primary bg-primary text-primary-foreground"
                : "border-border text-muted-foreground hover:bg-accent",
            )}
          >
            {String(option)}
          </button>
        ))}
      </div>
    </div>
  );
}
