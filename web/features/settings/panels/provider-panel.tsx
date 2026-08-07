"use client";

import {
  Check,
  Eye,
  EyeOff,
  Loader2,
  Plus,
  RefreshCw,
  Search,
  Trash2,
  TriangleAlert,
} from "lucide-react";
import { useMemo, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipTrigger } from "@/components/ui/tooltip";
import * as api from "@/features/ai/api";
import { findPreset, presetRequiresApiKey } from "@/features/ai/presets";
import { ModelIcon, ProviderIcon } from "@/features/ai/provider-icons";
import { isProtocolReady } from "@/features/ai/protocol-support";
import { useAiStore } from "@/features/ai/store";
import {
  type AiProvider,
  normalizeBaseUrl,
  PROVIDER_TYPES,
  type ProviderType,
  resolveProtocol,
} from "@/features/ai/types";
import { AddProviderDialog } from "@/features/settings/panels/add-provider-dialog";
import { ModelFormDialog } from "@/features/settings/panels/model-form-dialog";
import {
  FormField,
  MasterDetail,
  PanelEmpty,
  PanelSection,
} from "@/features/settings/panels/panel-shell";
import { cn } from "@/lib/utils";

export function ProviderPanel() {
  const t = useT();
  const providers = useAiStore((state) => state.providers);
  const loading = useAiStore((state) => state.loading);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [adding, setAdding] = useState(false);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return q ? providers.filter((p) => p.name.toLowerCase().includes(q)) : providers;
  }, [providers, query]);

  const { enabled, disabled } = useMemo(
    () => ({
      enabled: filtered.filter((p) => p.enabled),
      disabled: filtered.filter((p) => !p.enabled),
    }),
    [filtered],
  );

  const selected =
    providers.find((p) => p.id === selectedId) ?? (providers.length > 0 ? providers[0] : null);

  return (
    <>
      <MasterDetail
        list={
          <>
            <div className="flex items-center gap-1.5 border-b p-2">
              <div className="relative flex-1">
                <Search
                  className="pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2 text-muted-foreground"
                  aria-hidden
                />
                <Input
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  placeholder={t("common.search")}
                  aria-label={t("common.search")}
                  className="h-7 pl-7 text-xs"
                />
              </div>
              <Button
                size="icon-sm"
                variant="outline"
                onPress={() => setAdding(true)}
                aria-label={t("provider.add")}
              >
                <Plus className="size-3.5" aria-hidden />
              </Button>
            </div>

            <div className="flex-1 overflow-y-auto p-1.5">
              {loading && providers.length === 0 && (
                <p className="p-3 text-xs text-muted-foreground">{t("common.loading")}</p>
              )}

              {enabled.length > 0 && (
                <ProviderGroup
                  label={t("common.enabled")}
                  providers={enabled}
                  selectedId={selected?.id ?? null}
                  onSelect={setSelectedId}
                />
              )}
              {disabled.length > 0 && (
                <ProviderGroup
                  label={t("common.disabled")}
                  providers={disabled}
                  selectedId={selected?.id ?? null}
                  onSelect={setSelectedId}
                />
              )}
            </div>
          </>
        }
        detail={
          selected ? (
            <ProviderDetail key={selected.id} provider={selected} />
          ) : (
            <PanelEmpty
              title={t("provider.empty")}
              description={t("provider.emptyDescription")}
              action={
                <Button size="sm" onPress={() => setAdding(true)}>
                  <Plus className="size-3.5" aria-hidden />
                  {t("provider.add")}
                </Button>
              }
            />
          )
        }
      />

      <AddProviderDialog
        isOpen={adding}
        onOpenChange={setAdding}
        onCreated={(id) => setSelectedId(id)}
      />
    </>
  );
}

function ProviderGroup({
  label,
  providers,
  selectedId,
  onSelect,
}: {
  label: string;
  providers: AiProvider[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  const t = useT();

  return (
    <div className="mb-2">
      <p className="px-2 py-1 text-[0.6875rem] font-medium tracking-wide text-muted-foreground uppercase">
        {label}
      </p>

      {providers.map((provider) => {
        const requiresKey = presetRequiresApiKey(provider.builtinId);
        // Three states, because "enabled but unusable" is the one worth surfacing: a provider
        // the user switched on but never gave a key to would otherwise look ready.
        const status = !provider.enabled
          ? "off"
          : provider.hasApiKey || !requiresKey
            ? "ready"
            : "needsKey";

        return (
          <button
            key={provider.id}
            type="button"
            onClick={() => onSelect(provider.id)}
            className={cn(
              "flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left transition-colors",
              provider.id === selectedId ? "bg-accent" : "hover:bg-accent/50",
            )}
          >
            <span className="flex size-7 shrink-0 items-center justify-center rounded-md bg-background ring-1 ring-border">
              <ProviderIcon
                builtinId={provider.builtinId}
                icon={provider.icon}
                name={provider.name}
                size={16}
              />
            </span>

            <span className="min-w-0 flex-1">
              <span className="block truncate text-xs font-medium">{provider.name}</span>
              <span className="block text-[0.6875rem] text-muted-foreground">
                {t("provider.modelCount", {
                  enabled: provider.models.filter((m) => m.enabled).length,
                  total: provider.models.length,
                })}
              </span>
            </span>

            <span
              aria-hidden
              className={cn(
                "size-1.5 shrink-0 rounded-full",
                status === "ready" && "bg-emerald-500",
                status === "needsKey" && "bg-amber-500",
                status === "off" && "bg-muted-foreground/40",
              )}
            />
          </button>
        );
      })}
    </div>
  );
}

function ProviderDetail({ provider }: { provider: AiProvider }) {
  const t = useT();
  const updateProvider = useAiStore((state) => state.updateProvider);
  const deleteProvider = useAiStore((state) => state.deleteProvider);
  const setAllModelsEnabled = useAiStore((state) => state.setAllModelsEnabled);
  const updateModel = useAiStore((state) => state.updateModel);
  const deleteModel = useAiStore((state) => state.deleteModel);
  const refresh = useAiStore((state) => state.refresh);

  const preset = findPreset(provider.builtinId);
  const requiresKey = presetRequiresApiKey(provider.builtinId);
  const isBuiltin = Boolean(provider.builtinId);

  const [apiKeyDraft, setApiKeyDraft] = useState("");
  const [showKey, setShowKey] = useState(false);
  const [baseUrl, setBaseUrl] = useState(provider.baseUrl);
  const [saving, setSaving] = useState(false);
  const [check, setCheck] = useState<api.ProviderCheckResult | null>(null);
  const [checking, setChecking] = useState(false);
  const [fetching, setFetching] = useState(false);
  const [fetchMessage, setFetchMessage] = useState<string | null>(null);
  const [modelQuery, setModelQuery] = useState("");
  const [editingModel, setEditingModel] = useState<string | "new" | null>(null);
  const [deletingProvider, setDeletingProvider] = useState(false);
  const [deletingModelId, setDeletingModelId] = useState<string | null>(null);

  const visibleModels = useMemo(() => {
    const q = modelQuery.trim().toLowerCase();
    return q
      ? provider.models.filter(
          (m) => m.modelKey.toLowerCase().includes(q) || m.name.toLowerCase().includes(q),
        )
      : provider.models;
  }, [provider.models, modelQuery]);

  async function saveApiKey() {
    if (!apiKeyDraft) return;
    setSaving(true);
    try {
      await updateProvider(provider.id, { apiKey: apiKeyDraft });
      setApiKeyDraft("");
      setShowKey(false);
      setCheck(null);
    } finally {
      setSaving(false);
    }
  }

  async function saveBaseUrl() {
    const normalized = normalizeBaseUrl(baseUrl, provider.type);
    if (normalized === provider.baseUrl) return;

    setSaving(true);
    try {
      await updateProvider(provider.id, { baseUrl: normalized });
      setBaseUrl(normalized);
    } finally {
      setSaving(false);
    }
  }

  async function runCheck() {
    setChecking(true);
    setCheck(null);
    try {
      setCheck(await api.checkProvider(provider.id));
    } catch {
      setCheck({ ok: false, statusCode: null, error: t("errors.networkError"), modelCount: null });
    } finally {
      setChecking(false);
    }
  }

  /**
   * Pulls the provider's catalog and adds whatever is missing.
   *
   * Import is absent-only on the server, so re-running never duplicates a model or discards a
   * per-model edit — which matters because this is the button users press when a vendor ships
   * something new.
   */
  async function fetchModels() {
    setFetching(true);
    setFetchMessage(null);
    try {
      const { models } = await api.fetchUpstreamModels(provider.id);
      if (models.length === 0) {
        setFetchMessage(t("provider.fetchedModels", { count: 0 }));
        return;
      }

      const { added } = await api.importModels(
        provider.id,
        models.map((model) => ({
          modelKey: model.modelKey,
          name: model.name,
          category: model.category,
          enabled: false,
        })),
      );

      setFetchMessage(t("provider.fetchedModels", { count: added }));
      await refresh();
    } catch (error) {
      setFetchMessage(error instanceof api.AiApiError ? error.details[0] ?? error.message : t("errors.networkError"));
    } finally {
      setFetching(false);
    }
  }

  return (
    <>
      <header className="flex shrink-0 items-center gap-3 border-b px-5 py-3.5">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-background ring-1 ring-border">
          <ProviderIcon
            builtinId={provider.builtinId}
            icon={provider.icon}
            name={provider.name}
            size={20}
          />
        </span>

        <div className="min-w-0 flex-1">
          <h2 className="truncate text-sm font-semibold">{provider.name}</h2>
          {preset && <p className="truncate text-xs text-muted-foreground">{preset.description}</p>}
        </div>

        <Switch
          isSelected={provider.enabled}
          onChange={(value) => void updateProvider(provider.id, { enabled: value })}
          aria-label={provider.enabled ? t("common.disable") : t("common.enable")}
        />

        <TooltipTrigger>
          <Button
            size="icon-sm"
            variant="ghost"
            aria-label={t("common.delete")}
            onPress={() => setDeletingProvider(true)}
          >
            <Trash2 className="size-3.5" aria-hidden />
          </Button>
          <Tooltip>{t("common.delete")}</Tooltip>
        </TooltipTrigger>
      </header>

      <div className="flex min-h-0 flex-1 flex-col gap-6 overflow-y-auto p-5">
        {requiresKey && (
          <FormField label={t("provider.apiKey")}>
            <div className="flex gap-2">
              <div className="relative flex-1">
                <Input
                  type={showKey ? "text" : "password"}
                  value={apiKeyDraft}
                  onChange={(event) => setApiKeyDraft(event.target.value)}
                  placeholder={
                    provider.hasApiKey
                      ? t("provider.apiKeyStored", { hint: provider.apiKeyHint ?? "" })
                      : t("provider.apiKeyPlaceholder")
                  }
                  aria-label={t("provider.apiKey")}
                  className="pr-8"
                />
                <TooltipTrigger>
                  <button
                    type="button"
                    onClick={() => setShowKey((value) => !value)}
                    className="absolute top-1/2 right-2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
                    aria-label={showKey ? "Hide" : "Show"}
                  >
                    {showKey ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
                  </button>
                  <Tooltip>{showKey ? "Hide" : "Show"}</Tooltip>
                </TooltipTrigger>
              </div>

              <Button
                size="sm"
                variant="outline"
                isDisabled={!apiKeyDraft || saving}
                onPress={() => void saveApiKey()}
              >
                {t("common.save")}
              </Button>
            </div>

            {preset?.apiKeyUrl && (
              <a
                href={preset.apiKeyUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="w-fit text-xs text-muted-foreground underline underline-offset-2 hover:text-foreground"
              >
                {t("provider.getApiKey")}
              </a>
            )}
          </FormField>
        )}

        <FormField label={t("provider.baseUrl")}>
          <div className="flex gap-2">
            <Input
              value={baseUrl}
              onChange={(event) => setBaseUrl(event.target.value)}
              onBlur={() => void saveBaseUrl()}
              aria-label={t("provider.baseUrl")}
              className="flex-1 font-mono text-xs"
            />
            <Button size="sm" variant="outline" isDisabled={checking} onPress={() => void runCheck()}>
              {checking ? (
                <Loader2 className="size-3.5 animate-spin" aria-hidden />
              ) : (
                <Check className="size-3.5" aria-hidden />
              )}
              {checking ? t("provider.checking") : t("provider.check")}
            </Button>
          </div>

          {check && (
            <p
              className={cn(
                "text-xs",
                check.ok ? "text-emerald-600 dark:text-emerald-500" : "text-destructive",
              )}
            >
              {check.ok
                ? `${t("provider.checkSuccess")} · ${t("provider.fetchedModels", { count: check.modelCount ?? 0 })}`
                : `${t("provider.checkFailed")}${check.statusCode ? ` (${check.statusCode})` : ""}: ${check.error ?? ""}`}
            </p>
          )}
        </FormField>

        {/* Built-ins keep the protocol their preset declares: it is what the preset's model
            list and base URL were written against, and changing it would break both. */}
        {!isBuiltin && (
          <FormField label={t("provider.protocol")} hint={t("provider.protocolHint")}>
            <Select
              selectedKey={provider.type}
              onSelectionChange={(key) =>
                void updateProvider(provider.id, { type: String(key) as ProviderType })
              }
              className="w-64"
            >
              <SelectTrigger aria-label={t("provider.protocol")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {PROVIDER_TYPES.map((value) => (
                  <SelectItem key={value} id={value}>
                    {value}
                    {!isProtocolReady(value) && (
                      <span className="ml-1.5 text-[0.625rem] text-amber-600 dark:text-amber-500">
                        {t("provider.notSupported")}
                      </span>
                    )}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </FormField>
        )}

        <PanelSection
          title={t("provider.models")}
          actions={
            <div className="flex items-center gap-1.5">
              <Button size="sm" variant="ghost" onPress={() => void setAllModelsEnabled(provider.id, true)}>
                {t("provider.enableAll")}
              </Button>
              <Button size="sm" variant="ghost" onPress={() => void setAllModelsEnabled(provider.id, false)}>
                {t("provider.disableAll")}
              </Button>
              <Button size="sm" variant="outline" isDisabled={fetching} onPress={() => void fetchModels()}>
                {fetching ? (
                  <Loader2 className="size-3.5 animate-spin" aria-hidden />
                ) : (
                  <RefreshCw className="size-3.5" aria-hidden />
                )}
                {fetching ? t("provider.fetching") : t("provider.fetchModels")}
              </Button>
              <TooltipTrigger>
                <Button size="icon-sm" variant="outline" onPress={() => setEditingModel("new")} aria-label={t("provider.addModel")}>
                  <Plus className="size-3.5" aria-hidden />
                </Button>
                <Tooltip>{t("provider.addModel")}</Tooltip>
              </TooltipTrigger>
            </div>
          }
        >
          {fetchMessage && <p className="text-xs text-muted-foreground">{fetchMessage}</p>}

          {provider.models.length > 3 && (
            <Input
              value={modelQuery}
              onChange={(event) => setModelQuery(event.target.value)}
              placeholder={t("common.search")}
              aria-label={t("common.search")}
              className="h-7 text-xs"
            />
          )}

              {provider.models.length === 0 ? (
                <p className="rounded-lg border border-dashed p-4 text-center text-xs text-muted-foreground">
                  {t("provider.noModels")}
                </p>
              ) : (
                <div className="divide-y rounded-lg border">
                  {visibleModels.map((model) => {
                    // Resolved per model, because an override can point at a protocol the
                    // provider itself does not use.
                    const ready = isProtocolReady(resolveProtocol(provider, model));

                    return (
                      <div key={model.id} className="flex items-center gap-2.5 px-3 py-2">
                        <ModelIcon
                          modelKey={model.modelKey}
                          icon={model.icon}
                          builtinId={provider.builtinId}
                          size={15}
                        />

                        <div className="min-w-0 flex-1">
                          <p className="truncate text-xs font-medium">{model.name}</p>
                          <p className="truncate font-mono text-[0.6875rem] text-muted-foreground">
                            {model.modelKey}
                          </p>
                        </div>

                        {!ready && (
                          <span
                            title={t("provider.notSupportedHint")}
                            className="shrink-0 rounded bg-amber-500/15 px-1.5 py-0.5 text-[0.625rem] text-amber-700 dark:text-amber-500"
                          >
                            {t("provider.notSupported")}
                          </span>
                        )}

                        <span className="shrink-0 rounded bg-muted px-1.5 py-0.5 text-[0.625rem] text-muted-foreground">
                          {t(`model.category${cap(model.category)}` as Parameters<typeof t>[0])}
                        </span>

                        {/* Only surfaced when set — an override is the exception, and showing the
                            inherited protocol on every row would be noise. */}
                        {model.type && (
                          <span className="shrink-0 rounded bg-muted px-1.5 py-0.5 font-mono text-[0.625rem] text-muted-foreground">
                            {model.type}
                          </span>
                        )}

                        <Switch
                          isSelected={model.enabled && ready}
                          isDisabled={!ready}
                          onChange={(value) => void updateModel(model.id, { enabled: value })}
                          aria-label={model.name}
                        />

                        <TooltipTrigger>
                          <Button
                            size="icon-sm"
                            variant="ghost"
                            onPress={() => setEditingModel(model.id)}
                            aria-label={t("common.edit")}
                          >
                            <Search className="size-3" aria-hidden />
                          </Button>
                          <Tooltip>{t("common.edit")}</Tooltip>
                        </TooltipTrigger>

                        <TooltipTrigger>
                          <Button
                            size="icon-sm"
                            variant="ghost"
                            aria-label={t("common.delete")}
                            onPress={() => setDeletingModelId(model.id)}
                          >
                            <Trash2 className="size-3" aria-hidden />
                          </Button>
                          <Tooltip>{t("common.delete")}</Tooltip>
                        </TooltipTrigger>
                      </div>
                    );
                  })}
                </div>
              )}
        </PanelSection>

        {provider.enabled && requiresKey && !provider.hasApiKey && (
          <p className="flex items-center gap-1.5 text-xs text-amber-600 dark:text-amber-500">
            <TriangleAlert className="size-3.5" aria-hidden />
            {t("provider.needsApiKey")}
          </p>
        )}
      </div>

      {editingModel && (
        <ModelFormDialog
          isOpen
          onOpenChange={(open) => !open && setEditingModel(null)}
          provider={provider}
          model={
            editingModel === "new"
              ? null
              : (provider.models.find((m) => m.id === editingModel) ?? null)
          }
        />
      )}

      <ConfirmDialog
        isOpen={deletingProvider}
        onOpenChange={setDeletingProvider}
        title={t("provider.deleteConfirm", { name: provider.name })}
        onConfirm={() => deleteProvider(provider.id)}
      />

      <ConfirmDialog
        isOpen={deletingModelId !== null}
        onOpenChange={(open) => !open && setDeletingModelId(null)}
        title={t("model.deleteConfirm", {
          name: provider.models.find((m) => m.id === deletingModelId)?.name ?? "",
        })}
        onConfirm={() => {
          if (deletingModelId) return deleteModel(deletingModelId);
        }}
      />
    </>
  );
}

function cap(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}
