"use client";

import { useT } from "@/components/providers/i18n-provider";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectSeparator,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { ModelIcon } from "@/features/ai/provider-icons";
import { type ModelSlot, type SlotKind, useAiStore, usableProviders } from "@/features/ai/store";
import type { ModelCategory } from "@/features/ai/types";
import { PanelBody, PanelHeader, PanelSection } from "@/features/settings/panels/panel-shell";

const UNSET = "__unset__";

const SLOTS: ReadonlyArray<{
  kind: SlotKind;
  labelKey: "modelConfig.chatSlot" | "modelConfig.fastSlot" | "modelConfig.imageSlot" | "modelConfig.videoSlot";
  category: ModelCategory;
}> = [
  { kind: "image", labelKey: "modelConfig.imageSlot", category: "image" },
  { kind: "video", labelKey: "modelConfig.videoSlot", category: "video" },
  { kind: "chat", labelKey: "modelConfig.chatSlot", category: "chat" },
  { kind: "fast", labelKey: "modelConfig.fastSlot", category: "chat" },
];

/**
 * Assigns a default provider+model to each role.
 *
 * New nodes start from these, so a user who configures them once stops having to pick a
 * provider on every node they create.
 */
export function ModelConfigPanel() {
  const t = useT();
  const providers = useAiStore((state) => state.providers);
  const slots = useAiStore((state) => state.slots);
  const setSlot = useAiStore((state) => state.setSlot);

  return (
    <>
      <PanelHeader title={t("modelConfig.title")} description={t("modelConfig.description")} />

      <PanelBody>
        {SLOTS.map(({ kind, labelKey, category }) => {
          const available = usableProviders(providers, category);
          const slot = slots[kind];

          // Encodes both ids so one Select can pick a (provider, model) pair.
          const selectedKey =
            slot.providerId && slot.modelId ? `${slot.providerId}::${slot.modelId}` : UNSET;

          return (
            <PanelSection key={kind} title={t(labelKey)}>
              {available.length === 0 ? (
                <p className="text-xs text-muted-foreground">{t("modelConfig.noAvailable")}</p>
              ) : (
                <Select
                  selectedKey={selectedKey}
                  onSelectionChange={(key) => {
                    const value = String(key);
                    if (value === UNSET) {
                      setSlot(kind, { providerId: null, modelId: null });
                      return;
                    }

                    const [providerId, modelId] = value.split("::");
                    setSlot(kind, { providerId, modelId } satisfies ModelSlot);
                  }}
                  className="w-full max-w-md"
                >
                  <SelectTrigger aria-label={t(labelKey)}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem id={UNSET}>{t("modelConfig.notSet")}</SelectItem>
                    <SelectSeparator />
                    {available.flatMap((provider) =>
                      provider.models
                        .filter((model) => model.enabled && model.category === category)
                        .map((model) => (
                          <SelectItem key={model.id} id={`${provider.id}::${model.id}`}>
                            <ModelIcon
                              modelKey={model.modelKey}
                              icon={model.icon}
                              builtinId={provider.builtinId}
                              size={14}
                            />
                            {provider.name} · {model.name}
                          </SelectItem>
                        )),
                    )}
                  </SelectContent>
                </Select>
              )}
            </PanelSection>
          );
        })}
      </PanelBody>
    </>
  );
}
