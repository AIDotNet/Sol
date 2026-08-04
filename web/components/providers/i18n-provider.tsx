"use client";

import { createContext, useCallback, useContext, useMemo, useSyncExternalStore } from "react";
import { DEFAULT_LOCALE, isLocale, type Locale } from "@/lib/i18n/config";
import { en } from "@/lib/i18n/dictionaries/en";
import { type Dictionary, zh } from "@/lib/i18n/dictionaries/zh";

const DICTIONARIES: Record<Locale, Dictionary> = { zh, en };

const STORAGE_KEY = "sol.locale";

/** Dot-separated paths into the dictionary, e.g. `"provider.apiKey"`. */
type Path<T> = {
  [K in keyof T & string]: T[K] extends string ? K : `${K}.${Path<T[K]>}`;
}[keyof T & string];

export type TranslationKey = Path<Dictionary>;

export type Translate = (key: TranslationKey, values?: Record<string, string | number>) => string;

interface I18nContextValue {
  locale: Locale;
  setLocale: (locale: Locale) => void;
  t: Translate;
}

const I18nContext = createContext<I18nContextValue | null>(null);

function lookup(dictionary: Dictionary, key: string): string | undefined {
  const value = key
    .split(".")
    .reduce<unknown>(
      (node, segment) =>
        typeof node === "object" && node !== null
          ? (node as Record<string, unknown>)[segment]
          : undefined,
      dictionary,
    );

  return typeof value === "string" ? value : undefined;
}

function interpolate(template: string, values?: Record<string, string | number>): string {
  if (!values) return template;
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    name in values ? String(values[name]) : match,
  );
}

/**
 * Reads the stored locale as an external store.
 *
 * `useSyncExternalStore` rather than `useState` + an effect, because the locale genuinely
 * differs between server and client: the server has no localStorage. Its `getServerSnapshot`
 * parameter exists for exactly this — React hydrates with the server value, then re-renders
 * with the client one, instead of throwing a hydration mismatch.
 *
 * An earlier version initialised `useState` from localStorage directly. That reads the stored
 * value during the *first client render*, which is hydration, so any page rendered on the
 * server with translations mismatched for a user whose preference was not the default.
 *
 * Mirrors the `useMounted` helper in `components/layout/theme-toggle.tsx`.
 */
const localeStore = {
  listeners: new Set<() => void>(),

  subscribe(listener: () => void) {
    localeStore.listeners.add(listener);
    // Another tab changing the preference should propagate here too.
    window.addEventListener("storage", listener);

    return () => {
      localeStore.listeners.delete(listener);
      window.removeEventListener("storage", listener);
    };
  },

  getSnapshot(): Locale {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return isLocale(stored) ? stored : DEFAULT_LOCALE;
  },

  getServerSnapshot(): Locale {
    return DEFAULT_LOCALE;
  },

  set(locale: Locale) {
    window.localStorage.setItem(STORAGE_KEY, locale);
    document.documentElement.lang = locale === "zh" ? "zh-CN" : "en";
    localeStore.listeners.forEach((listener) => listener());
  },
};

export function I18nProvider({ children }: { children: React.ReactNode }) {
  const locale = useSyncExternalStore(
    localeStore.subscribe,
    localeStore.getSnapshot,
    localeStore.getServerSnapshot,
  );

  const setLocale = useCallback((next: Locale) => localeStore.set(next), []);

  const t = useCallback<Translate>(
    (key, values) => {
      const template = lookup(DICTIONARIES[locale], key) ?? lookup(zh, key) ?? key;
      return interpolate(template, values);
    },
    [locale],
  );

  const value = useMemo(() => ({ locale, setLocale, t }), [locale, setLocale, t]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18nContextValue {
  const context = useContext(I18nContext);
  if (!context) {
    throw new Error("useI18n must be used within an I18nProvider");
  }
  return context;
}

/** Convenience hook for components that only need the translate function. */
export function useT(): Translate {
  return useI18n().t;
}
