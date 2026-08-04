"use client";

import { Search } from "lucide-react";
import { useMemo, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Input } from "@/components/ui/input";
import { ModelIcon, ProviderIcon } from "@/features/ai/provider-icons";
import { useAiStore } from "@/features/ai/store";
import { type AiModel, type AiProvider, normalizeModelKey } from "@/features/ai/types";
import { PanelBody, PanelEmpty, PanelHeader } from "@/features/settings/panels/panel-shell";

interface CatalogEntry {
  key: string;
  model: AiModel;
  providers: AiProvider[];
}

/**
 * The global model catalog: every model across every provider, deduplicated by identifier.
 *
 * The same model is often reachable through several providers (an official endpoint plus two
 * aggregators). Listing it once, with the providers that serve it, answers "what can this app
 * actually run" — which the per-provider view cannot.
 */
export function ModelPanel() {
  const t = useT();
  const providers = useAiStore((state) => state.providers);
  const [query, setQuery] = useState("");

  const catalog = useMemo(() => {
    const byKey = new Map<string, CatalogEntry>();

    for (const provider of providers) {
      for (const model of provider.models) {
        const key = normalizeModelKey(model.modelKey);
        const existing = byKey.get(key);

        if (existing) {
          existing.providers.push(provider);
          // Prefer the richest record: whichever copy carries context metadata is the one
          // worth showing, since aggregators often omit it.
          if (!existing.model.contextLength && model.contextLength) {
            existing.model = model;
          }
        } else {
          byKey.set(key, { key, model, providers: [provider] });
        }
      }
    }

    return [...byKey.values()].sort((a, b) => a.model.modelKey.localeCompare(b.model.modelKey));
  }, [providers]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return catalog;
    return catalog.filter(
      (entry) =>
        entry.model.modelKey.toLowerCase().includes(q) ||
        entry.model.name.toLowerCase().includes(q),
    );
  }, [catalog, query]);

  return (
    <>
      <PanelHeader title={t("model.title")} />

      {catalog.length === 0 ? (
        <PanelEmpty title={t("model.empty")} description={t("provider.emptyDescription")} />
      ) : (
        <PanelBody className="gap-3">
          <div className="relative">
            <Search
              className="pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2 text-muted-foreground"
              aria-hidden
            />
            <Input
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              placeholder={t("common.search")}
              aria-label={t("common.search")}
              className="pl-8"
            />
          </div>

          <div className="divide-y rounded-lg border">
            {filtered.map((entry) => (
              <div key={entry.key} className="flex items-center gap-3 px-3 py-2.5">
                <ModelIcon
                  modelKey={entry.model.modelKey}
                  icon={entry.model.icon}
                  builtinId={entry.providers[0]?.builtinId}
                  size={18}
                />

                <div className="min-w-0 flex-1">
                  <p className="truncate text-xs font-medium">{entry.model.name}</p>
                  <p className="truncate font-mono text-[0.6875rem] text-muted-foreground">
                    {entry.model.modelKey}
                  </p>
                </div>

                <span className="shrink-0 rounded bg-muted px-1.5 py-0.5 text-[0.625rem] text-muted-foreground">
                  {t(
                    `model.category${entry.model.category.charAt(0).toUpperCase()}${entry.model.category.slice(1)}` as Parameters<
                      typeof t
                    >[0],
                  )}
                </span>

                {entry.model.contextLength && (
                  <span className="shrink-0 text-[0.625rem] text-muted-foreground">
                    {Math.round(entry.model.contextLength / 1000)}K
                  </span>
                )}

                <div className="flex shrink-0 items-center gap-1" title={entry.providers.map((p) => p.name).join(", ")}>
                  {entry.providers.slice(0, 3).map((provider) => (
                    <span
                      key={provider.id}
                      className="flex size-5 items-center justify-center rounded bg-background ring-1 ring-border"
                    >
                      <ProviderIcon
                        builtinId={provider.builtinId}
                        icon={provider.icon}
                        name={provider.name}
                        size={11}
                      />
                    </span>
                  ))}
                  {entry.providers.length > 3 && (
                    <span className="text-[0.625rem] text-muted-foreground">
                      +{entry.providers.length - 3}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        </PanelBody>
      )}
    </>
  );
}
