/**
 * Minimal i18n. A typed dictionary and a context hook — no framework.
 *
 * `next-intl` and friends want to own routing (a `[locale]` segment or middleware). This app
 * keeps one URL per page and switches language client-side from settings, so the locale is a
 * preference rather than a route. That makes a dictionary lookup sufficient.
 *
 * `zh` is the source of truth: it is declared first and `Dictionary` is derived from it, so
 * adding a key to `zh` without adding it to `en` is a type error.
 */

export const LOCALES = ["zh", "en"] as const;
export type Locale = (typeof LOCALES)[number];

export const DEFAULT_LOCALE: Locale = "zh";

export const LOCALE_LABELS: Record<Locale, string> = {
  zh: "简体中文",
  en: "English",
};

export function isLocale(value: unknown): value is Locale {
  return typeof value === "string" && (LOCALES as readonly string[]).includes(value);
}
