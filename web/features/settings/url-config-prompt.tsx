"use client";

import { ShieldAlert } from "lucide-react";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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
import { ProviderIcon } from "@/features/ai/provider-icons";
import { useAiStore } from "@/features/ai/store";
import {
  parseQuickConfigFromLocation,
  resolveQuickConfig,
  maskApiKey,
  urlWithoutQuickConfig,
  type QuickConfigResult,
} from "@/features/settings/url-config";

/**
 * Detects a `?settings=`/`#settings=` share link and applies it after the device-scoped provider
 * list has loaded. A URL is not a trust boundary: a link containing a key always requires an
 * explicit confirmation, even when it asks for `autoApply`.
 */
export function UrlConfigPrompt() {
  const t = useT();
  const router = useRouter();
  const providers = useAiStore((state) => state.providers);
  const load = useAiStore((state) => state.load);
  const refresh = useAiStore((state) => state.refresh);
  const storeError = useAiStore((state) => state.error);

  const [busy, setBusy] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const [applyError, setApplyError] = useState<string | null>(null);
  const [appliedCount, setAppliedCount] = useState<number | null>(null);
  const [providersReady, setProvidersReady] = useState(false);
  const autoApplyStarted = useRef(false);

  // Parsed once at mount. The location is fixed for this mount, so the payload remains stable
  // while the provider store finishes loading.
  const [initial] = useState<QuickConfigResult>(() =>
    typeof window === "undefined"
      ? { ok: false, reason: "absent" }
      : parseQuickConfigFromLocation(window.location),
  );

  const config = !dismissed && initial.ok ? initial.config : null;
  const parseError =
    !dismissed && !initial.ok && initial.reason === "malformed" ? initial.detail : null;

  const resolved = useMemo(
    () => (config && providersReady ? resolveQuickConfig(config, providers) : null),
    [config, providers, providersReady],
  );

  // Strip the parameter as soon as anything was found, so a reload cannot re-prompt and the key
  // stops sitting in the address bar. The captured config remains in React state for this mount.
  useEffect(() => {
    if (initial.ok || (!initial.ok && initial.reason === "malformed")) {
      router.replace(urlWithoutQuickConfig(window.location.href), { scroll: false });
    }
  }, [initial, router]);

  // Ensure the prompt can also be used on a page where the canvas has not requested AI loading
  // yet. The store shares this promise with CanvasInner, so this does not create a second load;
  // `providersReady` deliberately waits for the seed pass as well as the initial list request.
  useEffect(() => {
    if (!initial.ok) return;

    let cancelled = false;
    void load().then(
      () => {
        if (!cancelled) setProvidersReady(true);
      },
      () => {
        // `refresh` normally converts request failures into storeError. Still unblock the
        // dialog if an unexpected seed/storage failure rejects the shared load promise.
        if (!cancelled) setProvidersReady(true);
      },
    );

    return () => {
      cancelled = true;
    };
  }, [initial, load]);

  const dismiss = useCallback(() => {
    setDismissed(true);
    setApplyError(null);
  }, []);

  const apply = useCallback(
    async (silent: boolean) => {
      if (!resolved) return;

      setBusy(true);
      setApplyError(null);

      try {
        const { created, updated } = await importConfig({
          providers: resolved.providers,
        });
        await refresh();

        router.replace(urlWithoutQuickConfig(window.location.href), { scroll: false });
        if (!silent) {
          setAppliedCount(created + updated);
        }
        dismiss();
      } catch (error) {
        setApplyError(error instanceof Error ? error.message : t("errors.unknown"));
      } finally {
        setBusy(false);
      }
    },
    [dismiss, refresh, resolved, router, t],
  );

  // `autoApply` is intentionally weaker than a user confirmation: it is honored only when the
  // link contains no plaintext API key. A malicious link cannot opt itself out of confirmation.
  useEffect(() => {
    if (
      !resolved ||
      !resolved.autoApply ||
      resolved.requiresConfirmation ||
      storeError ||
      autoApplyStarted.current
    ) {
      return;
    }

    autoApplyStarted.current = true;
    void apply(true);
  }, [apply, resolved, storeError]);

  if (parseError) {
    return (
      <Dialog
        isOpen
        onOpenChange={() => dismiss()}
        className="w-[min(28rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
      >
        <DialogHeader>
          <DialogTitle>{t("urlConfig.invalid")}</DialogTitle>
          <DialogDescription>{parseError}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button size="sm" onPress={dismiss}>
            {t("common.close")}
          </Button>
        </DialogFooter>
      </Dialog>
    );
  }

  if (appliedCount !== null) {
    return (
      <Dialog
        isOpen
        onOpenChange={() => setAppliedCount(null)}
        className="w-[min(24rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
      >
        <DialogHeader>
          <DialogTitle>{t("urlConfig.title")}</DialogTitle>
          <DialogDescription>{t("urlConfig.applied", { count: appliedCount })}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button size="sm" onPress={() => setAppliedCount(null)}>
            {t("common.close")}
          </Button>
        </DialogFooter>
      </Dialog>
    );
  }

  // The import is intentionally held until the device-scoped provider list is ready. This makes
  // the overwrite preview and built-in ID matching deterministic on a first visit.
  if (!resolved || (resolved.autoApply && !resolved.requiresConfirmation && !applyError)) {
    return null;
  }

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
        <p className="text-xs leading-relaxed text-muted-foreground">
          {resolved.requiresConfirmation ? t("urlConfig.warning") : t("urlConfig.noKeyWarning")}
        </p>
      </div>

      <div className="flex max-h-64 flex-col gap-1.5 overflow-y-auto">
        {resolved.providers.map((provider, index) => {
          const existing = provider.builtinId
            ? providers.find((candidate) => candidate.builtinId === provider.builtinId)
            : providers.find(
                (candidate) =>
                  candidate.name.trim().toLowerCase() === provider.name.trim().toLowerCase(),
              );
          const overwrites = existing !== undefined;

          return (
            <div
              key={`${provider.builtinId ?? provider.name}-${index}`}
              className="flex items-center gap-2.5 rounded-lg border p-2.5"
            >
              <span className="flex size-8 shrink-0 items-center justify-center rounded-md bg-background ring-1 ring-border">
                <ProviderIcon
                  builtinId={provider.builtinId}
                  icon={provider.icon}
                  name={provider.name}
                  size={17}
                />
              </span>

              <div className="min-w-0 flex-1">
                <p className="truncate text-xs font-medium">{provider.name}</p>
                <p className="truncate text-[0.6875rem] text-muted-foreground">
                  {provider.baseUrl ?? ""}
                </p>
                {overwrites && (
                  <p className="text-[0.6875rem] text-amber-600 dark:text-amber-500">
                    {t("urlConfig.willOverwrite")}
                  </p>
                )}
                {provider.models.length === 0 && (
                  <p className="text-[0.6875rem] text-amber-600 dark:text-amber-500">
                    {t("urlConfig.noModels")}
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
                    {t("urlConfig.models", { count: provider.models.length })}
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
        <Button size="sm" onPress={() => void apply(false)} isDisabled={busy}>
          {t("urlConfig.apply", { count: resolved.providers.length })}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}
