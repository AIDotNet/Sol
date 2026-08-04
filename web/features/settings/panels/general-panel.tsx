"use client";

import { Loader2, ShieldAlert } from "lucide-react";
import { useTheme } from "next-themes";
import { useCallback, useEffect, useState } from "react";
import { useI18n, useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { LOCALE_LABELS, LOCALES, type Locale } from "@/lib/i18n/config";
import { type DeviceIdentity, handshake, storedDeviceId } from "@/lib/device";
import { PanelBody, PanelHeader, PanelSection } from "@/features/settings/panels/panel-shell";

/**
 * General settings: appearance, language, and the device identity this configuration is bound to.
 *
 * The device section is not decoration. With no accounts, the device identity *is* the account —
 * it decides which providers and canvases the user can reach, so it belongs somewhere visible
 * along with the consequence of clearing browser storage.
 */
export function GeneralPanel() {
  const t = useT();
  const { locale, setLocale } = useI18n();
  const { theme, setTheme } = useTheme();

  const [identity, setIdentity] = useState<DeviceIdentity | null>(null);
  const [resolving, setResolving] = useState(false);

  // Read once at mount via lazy initial state. These values do not change while settings are
  // open, so an effect would only add a render pass.
  const [signals] = useState(collectDisplaySignals);

  const resolve = useCallback(async () => {
    setResolving(true);
    try {
      setIdentity(await handshake());
    } catch {
      setIdentity(null);
    } finally {
      setResolving(false);
    }
  }, []);

  // Reports an identity the browser already holds. Opening settings must not mint one — the
  // handshake is an explicit action behind the button below.
  //
  // No synchronous setState here: the first state update happens after the await, and the
  // cancelled flag stops a late response from writing to an unmounted panel.
  useEffect(() => {
    if (!storedDeviceId()) return;

    let cancelled = false;

    void (async () => {
      try {
        const resolved = await handshake();
        if (!cancelled) setIdentity(resolved);
      } catch {
        if (!cancelled) setIdentity(null);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, []);

  const methodLabel = identity
    ? identity.method === "Deterministic"
      ? t("general.methodDeterministic")
      : identity.method === "ProbabilisticCoarse"
        ? t("general.methodProbabilistic")
        : t("general.methodManual")
    : null;

  return (
    <>
      <PanelHeader title={t("settings.nav.general")} />

      <PanelBody>
        <PanelSection title={t("general.appearance")}>
          <Field label={t("general.theme")}>
            <Select
              selectedKey={theme ?? "system"}
              onSelectionChange={(key) => setTheme(String(key))}
              className="w-48"
            >
              <SelectTrigger aria-label={t("general.theme")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem id="light">{t("general.themeLight")}</SelectItem>
                <SelectItem id="dark">{t("general.themeDark")}</SelectItem>
                <SelectItem id="system">{t("general.themeSystem")}</SelectItem>
              </SelectContent>
            </Select>
          </Field>

          <Field label={t("general.language")}>
            <Select
              selectedKey={locale}
              onSelectionChange={(key) => setLocale(String(key) as Locale)}
              className="w-48"
            >
              <SelectTrigger aria-label={t("general.language")}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {LOCALES.map((value) => (
                  <SelectItem key={value} id={value}>
                    {LOCALE_LABELS[value]}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        </PanelSection>

        <PanelSection title={t("general.device")} description={t("general.deviceDescription")}>
          {identity ? (
            <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[10rem_1fr]">
              <Row label={t("general.deviceId")} value={identity.deviceId} mono />
              <Row label={t("general.visitorId")} value={identity.visitorId} mono />
              <Row label={t("general.method")} value={methodLabel ?? ""} />
              <Row label={t("general.confidence")} value={identity.confidence.toFixed(2)} />
            </dl>
          ) : (
            <div className="flex items-center gap-3">
              <p className="text-sm text-muted-foreground">{t("general.deviceNotResolved")}</p>
              <Button variant="outline" size="sm" onPress={() => void resolve()} isDisabled={resolving}>
                {resolving && <Loader2 className="size-3.5 animate-spin" aria-hidden />}
                {t("general.resolveDevice")}
              </Button>
            </div>
          )}

          <div className="flex gap-2 rounded-lg border border-amber-500/30 bg-amber-500/5 p-3">
            <ShieldAlert className="mt-0.5 size-4 shrink-0 text-amber-600 dark:text-amber-500" aria-hidden />
            <p className="text-xs leading-relaxed text-muted-foreground">
              {t("general.securityNotice")}
            </p>
          </div>
        </PanelSection>

        {signals.length > 0 && (
          <PanelSection title={t("general.signals")}>
            <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-[10rem_1fr]">
              {signals.map(([key, value]) => (
                <Row key={key} label={t(key as Parameters<typeof t>[0])} value={value} />
              ))}
            </dl>
          </PanelSection>
        )}
      </PanelBody>
    </>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <span className="text-sm">{label}</span>
      {children}
    </div>
  );
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className={mono ? "font-mono text-xs break-all" : "break-words"}>{value}</dd>
    </>
  );
}

/**
 * Reads the browser-independent signals the handshake sends.
 *
 * Shown so the fingerprinting is visible to the person being fingerprinted rather than silent —
 * `docs/device-identification.md` notes that PIPL, GDPR and CCPA all treat these as personal
 * data. Deliberately excludes the volatile, high-entropy signals: this is a disclosure, not a
 * second collection point.
 */
function collectDisplaySignals(): Array<[string, string]> {
  if (typeof window === "undefined") return [];

  const nav = navigator as Navigator & { deviceMemory?: number };
  const entries: Array<[string, string]> = [];

  const timeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  if (timeZone) entries.push(["general.timeZone", timeZone]);
  if (nav.platform) entries.push(["general.platform", nav.platform]);
  if (nav.hardwareConcurrency) {
    entries.push(["general.cpuCores", String(nav.hardwareConcurrency)]);
  }
  if (nav.deviceMemory) entries.push(["general.memory", `${nav.deviceMemory} GB`]);
  if (window.screen) {
    entries.push([
      "general.screen",
      `${window.screen.width}×${window.screen.height} @${window.devicePixelRatio}x`,
    ]);
  }
  if (navigator.language) entries.push(["general.primaryLanguage", navigator.language]);

  return entries;
}
