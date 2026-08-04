"use client";

import { ShieldAlert } from "lucide-react";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { importConfig } from "@/features/ai/api";
import { findPreset } from "@/features/ai/presets";
import { ProviderIcon } from "@/features/ai/provider-icons";
import { useAiStore } from "@/features/ai/store";
import type { ProviderType } from "@/features/ai/types";
import {
  maskApiKey,
  parseQuickConfigFromLocation,
  toImportPayload,
  urlWithoutQuickConfig,
} from "@/features/settings/url-config";

/**
 * Detects a `?settings=` share link and asks before applying it.
 *
 * The confirmation is not a formality. The payload is attacker-controlled and carries an API key
 * in plain text, so the user is shown exactly which providers would be written, with keys masked,
 * before anything touches their device. The parameter is stripped from the URL on both paths —
 * applying and discarding — so a reload cannot silently re-prompt and the key stops sitting in
 * the address bar and in `document.referrer`.
 */
export function UrlConfigPrompt() {
  const t = useT();
  const router = useRouter();
  const providers = useAiStore((state) => state.providers);
  const refresh = useAiStore((state) => state.refresh);

  const [busy, setBusy] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const [applyError, setApplyError] = useState<string | null>(null);

  // Parsed once at mount. The query string is fixed for this mount, so the prompt's contents
  // are derived rather than copied into state — no effect, no extra render pass.
  const [initial] = useState(() =>
    typeof window === "undefined"
      ? ({ ok: false, reason: "absent" } as const)
      : parseQuickConfigFromLocation(window.location),
  );

  const config = !dismissed && initial.ok ? initial.config : null;
  const parseError = !dismissed && !initial.ok && initial.reason === "malformed"
    ? initial.detail
    : null;

  // Strips the parameter as soon as anything was found, so a reload cannot re-prompt and the
  // key stops sitting in the address bar and in document.referrer.
  useEffect(() => {
    if (initial.ok || (!initial.ok && initial.reason === "malformed")) {
      router.replace(urlWithoutQuickConfig(window.location.href), { scroll: false });
    }
  }, [initial, router]);

  function dismiss() {
    setDismissed(true);
    setApplyError(null);
  }

  async function apply() {
    if (!config) return;

    setBusy(true);
    setApplyError(null);

    try {
      const payload = {
        providers: config.providers.map((provider) => {
          const preset = findPreset(provider.builtinId);
          const fallbackType: ProviderType = preset?.type ?? "openai-chat";
          const entry = toImportPayload(provider, fallbackType);

          // A link naming a known preset but omitting the URL still resolves, because the
          // preset supplies the default.
          return {
            ...entry,
            baseUrl: entry.baseUrl ?? preset?.defaultBaseUrl ?? null,
          };
        }),
      };

      const { created, updated } = await importConfig(payload);
      await refresh();

      window.alert(t("urlConfig.applied", { count: created + updated }));
      dismiss();
    } catch (error) {
      setApplyError(error instanceof Error ? error.message : t("errors.unknown"));
      setBusy(false);
    }
  }

  if (parseError) {
    return (
      <Dialog
        isOpen
        onOpenChange={() => setDismissed(true)}
        className="w-[min(28rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
      >
        <DialogHeader>
          <DialogTitle>{t("urlConfig.invalid")}</DialogTitle>
          <DialogDescription>{parseError}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button size="sm" onPress={() => setDismissed(true)}>
            {t("common.close")}
          </Button>
        </DialogFooter>
      </Dialog>
    );
  }

  if (!config) return null;

  const existingNames = new Set(providers.map((p) => p.name.toLowerCase()));

  return (
    <Dialog
      isOpen
      onOpenChange={(open) => !open && dismiss()}
      className="w-[min(32rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
      isDismissable={false}
    >
      <DialogHeader>
        <DialogTitle>{t("urlConfig.title")}</DialogTitle>
        <DialogDescription>{t("urlConfig.description")}</DialogDescription>
      </DialogHeader>

      <div className="flex gap-2 rounded-lg border border-amber-500/30 bg-amber-500/5 p-3">
        <ShieldAlert
          className="mt-0.5 size-4 shrink-0 text-amber-600 dark:text-amber-500"
          aria-hidden
        />
        <p className="text-xs leading-relaxed text-muted-foreground">{t("urlConfig.warning")}</p>
      </div>

      <div className="flex max-h-64 flex-col gap-1.5 overflow-y-auto">
        {config.providers.map((provider, index) => {
          const preset = findPreset(provider.builtinId);
          const name = provider.name ?? provider.builtinId ?? "";
          const overwrites = existingNames.has(name.toLowerCase());

          return (
            <div
              key={`${name}-${index}`}
              className="flex items-center gap-2.5 rounded-lg border p-2.5"
            >
              <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-background ring-1 ring-border">
                <ProviderIcon builtinId={provider.builtinId} name={name} size={17} />
              </span>

              <div className="min-w-0 flex-1">
                <p className="truncate text-xs font-medium">{name}</p>
                <p className="truncate text-[0.6875rem] text-muted-foreground">
                  {provider.baseUrl ?? preset?.defaultBaseUrl ?? ""}
                </p>
                {overwrites && (
                  <p className="text-[0.6875rem] text-amber-600 dark:text-amber-500">
                    {t("urlConfig.willOverwrite")}
                  </p>
                )}
              </div>

              <div className="shrink-0 text-right">
                {provider.apiKey && (
                  <p className="font-mono text-[0.6875rem] text-muted-foreground">
                    {maskApiKey(provider.apiKey)}
                  </p>
                )}
                {provider.models.length > 0 && (
                  <p className="text-[0.6875rem] text-muted-foreground">
                    {provider.models.length} models
                  </p>
                )}
              </div>
            </div>
          );
        })}
      </div>

      {applyError && <p className="text-xs text-destructive">{applyError}</p>}

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={dismiss} isDisabled={busy}>
          {t("urlConfig.discard")}
        </Button>
        <Button size="sm" onPress={() => void apply()} isDisabled={busy}>
          {t("urlConfig.apply", { count: config.providers.length })}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}
