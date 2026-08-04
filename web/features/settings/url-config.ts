import { z } from "zod";
import {
  MODEL_CATEGORIES,
  normalizeBaseUrl,
  PROVIDER_TYPES,
  type ProviderType,
} from "@/features/ai/types";

/**
 * URL-parameter quick configuration, in the style of LobeChat's `?settings=`.
 *
 * Extended beyond LobeChat's shape to carry model definitions, because this app needs to know a
 * model's category (image vs video vs chat) and protocol before it can be used on the canvas —
 * a bare key/baseURL pair isn't enough.
 *
 * Security: the payload is attacker-controlled and arrives in a URL, so it is validated
 * strictly here and the caller must show a confirmation dialog before anything is written.
 * LobeChat's own docs warn that such links neither validate nor encrypt their contents; a key
 * pasted into a URL may already be in browser history, referrer headers and server logs.
 */

export const QUICK_CONFIG_PARAM = "settings";

const BASE64_PREFIX = "base64:";

const modelSchema = z.object({
  id: z.string().trim().min(1).max(200),
  name: z.string().trim().min(1).max(200).optional(),
  category: z.enum(MODEL_CATEGORIES).default("chat"),
  type: z.enum(PROVIDER_TYPES).optional(),
  contextLength: z.number().int().positive().max(100_000_000).optional(),
  maxOutputTokens: z.number().int().positive().max(10_000_000).optional(),
});

const providerSchema = z
  .object({
    builtinId: z.string().trim().min(1).max(64).optional(),
    name: z.string().trim().min(1).max(200).optional(),
    apiKey: z.string().trim().min(1).max(4096).optional(),
    baseUrl: z.string().trim().max(2048).optional(),
    type: z.enum(PROVIDER_TYPES).optional(),
    models: z.array(modelSchema).max(200).default([]),
  })
  // A provider with neither a preset to inherit from nor a name is unusable.
  .refine((provider) => provider.builtinId !== undefined || provider.name !== undefined, {
    message: "provider requires builtinId or name",
  });

export const quickConfigSchema = z.object({
  providers: z.array(providerSchema).min(1).max(50),
});

export type QuickConfig = z.infer<typeof quickConfigSchema>;
export type QuickConfigProvider = z.infer<typeof providerSchema>;

export type QuickConfigResult =
  | { ok: true; config: QuickConfig }
  | { ok: false; reason: "absent" }
  | { ok: false; reason: "malformed"; detail: string };

function decodeBase64(value: string): string {
  // atob yields one byte per char; the payload may be UTF-8 (Chinese provider names), so the
  // bytes have to be reassembled before decoding.
  const binary = atob(value);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

/**
 * Parses the quick-config parameter out of a query string.
 *
 * Accepts raw JSON (LobeChat-compatible) or `base64:<...>` for payloads long enough to trip URL
 * length limits or get mangled by chat clients.
 */
export function parseQuickConfig(search: string | URLSearchParams): QuickConfigResult {
  const params = typeof search === "string" ? new URLSearchParams(search) : search;
  return parseRaw(params.get(QUICK_CONFIG_PARAM));
}

/**
 * Reads the config from either the query string or the hash fragment.
 *
 * The hash is preferred and checked first, because **a fragment is never sent to the server**:
 * it stays out of access logs, out of `Referer` headers, and out of the server-rendered payload.
 * A `?settings=` query parameter travels in the request line, so by the time the browser can
 * strip it the key has already reached the server — stripping helps with browser history and
 * shoulder-surfing, not with server-side exposure.
 *
 * The query form is still accepted for compatibility with LobeChat-style links, but
 * `#settings=` is what our own share links should use.
 */
export function parseQuickConfigFromLocation(location: {
  search: string;
  hash: string;
}): QuickConfigResult {
  const hash = location.hash.startsWith("#") ? location.hash.slice(1) : location.hash;
  const fromHash = parseRaw(new URLSearchParams(hash).get(QUICK_CONFIG_PARAM));

  return fromHash.ok || (!fromHash.ok && fromHash.reason !== "absent")
    ? fromHash
    : parseQuickConfig(location.search);
}

function parseRaw(raw: string | null): QuickConfigResult {
  if (raw === null || raw.trim() === "") {
    return { ok: false, reason: "absent" };
  }

  let json: string;
  try {
    json = raw.startsWith(BASE64_PREFIX) ? decodeBase64(raw.slice(BASE64_PREFIX.length)) : raw;
  } catch {
    return { ok: false, reason: "malformed", detail: "base64 decode failed" };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(json);
  } catch {
    return { ok: false, reason: "malformed", detail: "invalid JSON" };
  }

  const result = quickConfigSchema.safeParse(parsed);
  if (!result.success) {
    return {
      ok: false,
      reason: "malformed",
      detail: result.error.issues[0]?.message ?? "schema mismatch",
    };
  }

  return { ok: true, config: result.data };
}

/** Masks a key for display. Never render the full value — the dialog exists to be screenshotted. */
export function maskApiKey(apiKey: string | undefined): string {
  if (!apiKey) return "";
  const trimmed = apiKey.trim();
  if (trimmed.length <= 8) return "••••";
  return `${trimmed.slice(0, 3)}••••${trimmed.slice(-4)}`;
}

/**
 * Normalizes a parsed entry into the payload the import endpoint expects.
 *
 * Base URL normalization is applied here so a pasted `https://api.anthropic.com/v1` doesn't
 * become `.../v1/v1/messages` downstream.
 */
export function toImportPayload(provider: QuickConfigProvider, fallbackType: ProviderType) {
  const type = provider.type ?? fallbackType;

  return {
    builtinId: provider.builtinId ?? null,
    name: provider.name ?? provider.builtinId ?? "",
    type,
    apiKey: provider.apiKey ?? null,
    baseUrl: provider.baseUrl ? normalizeBaseUrl(provider.baseUrl, type) : null,
    models: provider.models.map((model) => ({
      modelKey: model.id,
      name: model.name ?? model.id,
      category: model.category,
      type: model.type ?? null,
      contextLength: model.contextLength ?? null,
      maxOutputTokens: model.maxOutputTokens ?? null,
      enabled: true,
    })),
  };
}

/**
 * Strips the parameter from a URL so a reload does not re-trigger the prompt and the key stops
 * sitting in the address bar. Clears both the query and the hash form.
 */
export function urlWithoutQuickConfig(url: string): string {
  const parsed = new URL(url);
  parsed.searchParams.delete(QUICK_CONFIG_PARAM);

  const hash = parsed.hash.startsWith("#") ? parsed.hash.slice(1) : parsed.hash;
  if (hash) {
    const hashParams = new URLSearchParams(hash);
    hashParams.delete(QUICK_CONFIG_PARAM);
    const rest = hashParams.toString();
    parsed.hash = rest ? `#${rest}` : "";
  }

  return `${parsed.pathname}${parsed.search}${parsed.hash}`;
}

/**
 * Builds a share link that carries the config in the hash fragment.
 *
 * Base64 keeps the JSON from being mangled by chat clients that try to linkify punctuation.
 */
export function buildShareLink(origin: string, path: string, config: QuickConfig): string {
  const json = JSON.stringify(config);
  const bytes = new TextEncoder().encode(json);
  const base64 = btoa(String.fromCharCode(...bytes));

  return `${origin}${path}#${QUICK_CONFIG_PARAM}=${BASE64_PREFIX}${base64}`;
}
