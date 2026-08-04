"use client";

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
import { Switch } from "@/components/ui/switch";
import { useAiStore } from "@/features/ai/store";
import {
  type AiModel,
  type AiProvider,
  MODEL_CATEGORIES,
  type ModelCategory,
  PROTOCOLS_BY_CATEGORY,
  type ProviderType,
  resolveProtocol,
} from "@/features/ai/types";
import { FormField } from "@/features/settings/panels/panel-shell";

/** Sentinel for "no override" — react-aria Select keys must be non-empty strings. */
const INHERIT = "__inherit__";

/**
 * Creates or edits a model.
 *
 * The protocol field is the interesting one: leaving it on "inherit" is normal, and an override
 * is only needed on aggregators that serve several protocols. The choices are filtered to those
 * that can actually serve the selected category, since e.g. `openai-images` cannot serve chat.
 */
export function ModelFormDialog({
  isOpen,
  onOpenChange,
  provider,
  model,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  provider: AiProvider;
  /** Null creates a new model. */
  model: AiModel | null;
}) {
  const t = useT();
  const addModel = useAiStore((state) => state.addModel);
  const updateModel = useAiStore((state) => state.updateModel);

  const [modelKey, setModelKey] = useState(model?.modelKey ?? "");
  const [name, setName] = useState(model?.name ?? "");
  const [category, setCategory] = useState<ModelCategory>(model?.category ?? "chat");
  const [type, setType] = useState<ProviderType | typeof INHERIT>(model?.type ?? INHERIT);
  const [contextLength, setContextLength] = useState(model?.contextLength?.toString() ?? "");
  const [maxOutputTokens, setMaxOutputTokens] = useState(model?.maxOutputTokens?.toString() ?? "");
  const [supportsVision, setSupportsVision] = useState(model?.supportsVision ?? false);
  const [supportsFunctionCall, setSupportsFunctionCall] = useState(
    model?.supportsFunctionCall ?? false,
  );
  const [supportsThinking, setSupportsThinking] = useState(model?.supportsThinking ?? false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isNew = model === null;

  // What the model would resolve to with no override — shown so "inherit" is concrete.
  const inheritedProtocol = resolveProtocol(provider, { type: null, category });
  const allowedProtocols = PROTOCOLS_BY_CATEGORY[category];

  async function save() {
    if (!modelKey.trim() || busy) return;

    setBusy(true);
    setError(null);

    const parsedContext = contextLength.trim() ? Number(contextLength) : null;
    const parsedOutput = maxOutputTokens.trim() ? Number(maxOutputTokens) : null;

    try {
      if (isNew) {
        await addModel(provider.id, {
          modelKey: modelKey.trim(),
          name: name.trim() || modelKey.trim(),
          category,
          type: type === INHERIT ? null : type,
          contextLength: parsedContext,
          maxOutputTokens: parsedOutput,
          supportsVision,
          supportsFunctionCall,
          supportsThinking,
          enabled: true,
        });
      } else {
        await updateModel(model.id, {
          name: name.trim() || modelKey.trim(),
          category,
          // "" is the server's signal to clear the override, distinct from omitting the field.
          type: type === INHERIT ? "" : type,
          contextLength: parsedContext,
          maxOutputTokens: parsedOutput,
          supportsVision,
          supportsFunctionCall,
          supportsThinking,
        });
      }

      onOpenChange(false);
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : t("errors.unknown"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      className="w-[min(32rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{isNew ? t("provider.addModel") : t("common.edit")}</DialogTitle>
        <DialogDescription>{provider.name}</DialogDescription>
      </DialogHeader>

      <div className="flex max-h-[60vh] flex-col gap-3 overflow-y-auto">
        <FormField label={t("model.id")} hint={t("model.idHint")}>
          <Input
            value={modelKey}
            onChange={(event) => setModelKey(event.target.value)}
            // The key is what gets sent upstream; changing it on an existing model would
            // silently repoint every node that references it.
            disabled={!isNew}
            placeholder="gpt-image-1"
            aria-label={t("model.id")}
            className="font-mono text-xs"
          />
        </FormField>

        <FormField label={t("model.name")}>
          <Input
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder={modelKey || "GPT Image 1"}
            aria-label={t("model.name")}
          />
        </FormField>

        <FormField label={t("model.category")}>
          <Select
            selectedKey={category}
            onSelectionChange={(key) => {
              const next = String(key) as ModelCategory;
              setCategory(next);
              // An override that the new category cannot serve would be rejected by the server;
              // drop it back to inherit instead of letting the user submit an invalid pair.
              if (type !== INHERIT && !PROTOCOLS_BY_CATEGORY[next].includes(type)) {
                setType(INHERIT);
              }
            }}
            className="w-full"
          >
            <SelectTrigger aria-label={t("model.category")}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {MODEL_CATEGORIES.map((value) => (
                <SelectItem key={value} id={value}>
                  {t(`model.category${value.charAt(0).toUpperCase()}${value.slice(1)}` as Parameters<typeof t>[0])}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </FormField>

        <FormField label={t("model.protocolOverride")} hint={t("model.protocolOverrideHint")}>
          <Select
            selectedKey={type}
            onSelectionChange={(key) => setType(String(key) as ProviderType | typeof INHERIT)}
            className="w-full"
          >
            <SelectTrigger aria-label={t("model.protocolOverride")}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem id={INHERIT}>
                {t("model.protocolInherit", { type: inheritedProtocol })}
              </SelectItem>
              {allowedProtocols.map((value) => (
                <SelectItem key={value} id={value}>
                  {value}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </FormField>

        {category === "chat" && (
          <>
            <div className="grid grid-cols-2 gap-3">
              <FormField label={t("model.contextLength")}>
                <Input
                  type="number"
                  value={contextLength}
                  onChange={(event) => setContextLength(event.target.value)}
                  placeholder="128000"
                  aria-label={t("model.contextLength")}
                />
              </FormField>

              <FormField label={t("model.maxOutputTokens")}>
                <Input
                  type="number"
                  value={maxOutputTokens}
                  onChange={(event) => setMaxOutputTokens(event.target.value)}
                  placeholder="16384"
                  aria-label={t("model.maxOutputTokens")}
                />
              </FormField>
            </div>

            <FormField label={t("model.capabilities")}>
              <div className="flex flex-col gap-2 rounded-lg border p-3">
                <Toggle
                  label={t("model.supportsVision")}
                  value={supportsVision}
                  onChange={setSupportsVision}
                />
                <Toggle
                  label={t("model.supportsFunctionCall")}
                  value={supportsFunctionCall}
                  onChange={setSupportsFunctionCall}
                />
                <Toggle
                  label={t("model.supportsThinking")}
                  value={supportsThinking}
                  onChange={setSupportsThinking}
                />
              </div>
            </FormField>
          </>
        )}

        {error && <p className="text-xs text-destructive">{error}</p>}
      </div>

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        <Button size="sm" isDisabled={!modelKey.trim() || busy} onPress={() => void save()}>
          {t("common.save")}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}

function Toggle({
  label,
  value,
  onChange,
}: {
  label: string;
  value: boolean;
  onChange: (value: boolean) => void;
}) {
  return (
    <div className="flex items-center justify-between gap-4">
      <span className="text-xs">{label}</span>
      <Switch isSelected={value} onChange={onChange} aria-label={label} />
    </div>
  );
}
