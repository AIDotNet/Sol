"use client";

import { Plus } from "lucide-react";
import { useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { PROVIDER_PRESETS } from "@/features/ai/presets";
import { ProviderIcon } from "@/features/ai/provider-icons";
import { useAiStore } from "@/features/ai/store";
import { normalizeBaseUrl, PROVIDER_TYPES, type ProviderType } from "@/features/ai/types";
import { FormField } from "@/features/settings/panels/panel-shell";
import { cn } from "@/lib/utils";

/**
 * Adds a provider, either from a built-in preset or from scratch.
 *
 * A preset seeds the base URL, protocol and a starter model list so the only thing left is the
 * key. A preset already added is disabled rather than hidden, so the grid stays stable.
 */
export function AddProviderDialog({
  isOpen,
  onOpenChange,
  onCreated,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  onCreated?: (providerId: string) => void;
}) {
  const t = useT();
  const providers = useAiStore((state) => state.providers);
  const createProvider = useAiStore((state) => state.createProvider);

  const [mode, setMode] = useState<"preset" | "custom">("preset");
  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [type, setType] = useState<ProviderType>("openai-chat");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const takenBuiltinIds = new Set(providers.map((p) => p.builtinId).filter(Boolean));
  const takenNames = new Set(providers.map((p) => p.name.toLowerCase()));

  function reset() {
    setMode("preset");
    setName("");
    setBaseUrl("");
    setType("openai-chat");
    setError(null);
  }

  async function addFromPreset(builtinId: string) {
    const preset = PROVIDER_PRESETS.find((p) => p.builtinId === builtinId);
    if (!preset || busy) return;

    setBusy(true);
    setError(null);

    try {
      const created = await createProvider({
        builtinId: preset.builtinId,
        name: preset.name,
        description: preset.description,
        type: preset.type,
        baseUrl: preset.defaultBaseUrl,
        presetVersion: preset.version,
        models: preset.defaultModels.map((model) => ({
          modelKey: model.modelKey,
          name: model.name,
          category: model.category,
          type: model.type ?? null,
          contextLength: model.contextLength ?? null,
          maxOutputTokens: model.maxOutputTokens ?? null,
          supportsVision: model.supportsVision ?? false,
          supportsFunctionCall: model.supportsFunctionCall ?? false,
          supportsThinking: model.supportsThinking ?? false,
          enabled: model.enabled ?? true,
        })),
      });

      onCreated?.(created.id);
      onOpenChange(false);
      reset();
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : t("errors.unknown"));
    } finally {
      setBusy(false);
    }
  }

  async function addCustom() {
    if (!name.trim() || !baseUrl.trim() || busy) return;

    setBusy(true);
    setError(null);

    try {
      const created = await createProvider({
        name: name.trim(),
        type,
        baseUrl: normalizeBaseUrl(baseUrl, type),
      });

      onCreated?.(created.id);
      onOpenChange(false);
      reset();
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : t("errors.unknown"));
    } finally {
      setBusy(false);
    }
  }

  const duplicateName = name.trim().length > 0 && takenNames.has(name.trim().toLowerCase());

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={(open) => {
        onOpenChange(open);
        if (!open) reset();
      }}
      className="w-[min(34rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("provider.add")}</DialogTitle>
        <DialogDescription>
          {mode === "preset" ? t("provider.addFromPreset") : t("provider.addCustom")}
        </DialogDescription>
      </DialogHeader>

      <div className="flex gap-1 rounded-lg bg-muted p-0.5">
        {(["preset", "custom"] as const).map((value) => (
          <button
            key={value}
            type="button"
            onClick={() => setMode(value)}
            className={cn(
              "flex-1 rounded-md px-3 py-1 text-xs transition-colors",
              mode === value
                ? "bg-background font-medium shadow-sm"
                : "text-muted-foreground hover:text-foreground",
            )}
          >
            {value === "preset" ? t("provider.addFromPreset") : t("provider.addCustom")}
          </button>
        ))}
      </div>

      {mode === "preset" ? (
        <div className="grid max-h-72 grid-cols-2 gap-1.5 overflow-y-auto">
          {PROVIDER_PRESETS.map((preset) => {
            const taken = takenBuiltinIds.has(preset.builtinId);

            return (
              <button
                key={preset.builtinId}
                type="button"
                disabled={taken || busy}
                onClick={() => void addFromPreset(preset.builtinId)}
                className={cn(
                  "flex items-center gap-2 rounded-lg border p-2 text-left transition-colors",
                  taken
                    ? "cursor-not-allowed opacity-40"
                    : "hover:border-ring hover:bg-accent/50",
                )}
              >
                <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-background ring-1 ring-border">
                  <ProviderIcon builtinId={preset.builtinId} name={preset.name} size={17} />
                </span>
                <span className="min-w-0">
                  <span className="block truncate text-xs font-medium">{preset.name}</span>
                  <span className="block truncate text-[0.6875rem] text-muted-foreground">
                    {taken ? t("common.enabled") : preset.description}
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      ) : (
        <div className="flex flex-col gap-3">
          <FormField label={t("provider.name")}>
            <Input
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder="My Provider"
              aria-label={t("provider.name")}
              aria-invalid={duplicateName || undefined}
            />
          </FormField>

          <FormField label={t("provider.baseUrl")}>
            <Input
              value={baseUrl}
              onChange={(event) => setBaseUrl(event.target.value)}
              placeholder="https://api.example.com/v1"
              aria-label={t("provider.baseUrl")}
              className="font-mono text-xs"
            />
          </FormField>

          <FormField label={t("provider.protocol")} hint={t("provider.protocolHint")}>
            <Select
              selectedKey={type}
              onSelectionChange={(key) => setType(String(key) as ProviderType)}
              className="w-full"
            >
              <SelectTrigger aria-label={t("provider.protocol")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {PROVIDER_TYPES.map((value) => (
                  <SelectItem key={value} id={value}>
                    {value}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </FormField>
        </div>
      )}

      {error && <p className="text-xs text-destructive">{error}</p>}

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        {mode === "custom" && (
          <Button
            size="sm"
            isDisabled={!name.trim() || !baseUrl.trim() || duplicateName || busy}
            onPress={() => void addCustom()}
          >
            <Plus className="size-3.5" aria-hidden />
            {t("common.add")}
          </Button>
        )}
      </DialogFooter>
    </Dialog>
  );
}
