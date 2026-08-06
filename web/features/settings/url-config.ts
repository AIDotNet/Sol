import { z } from "zod";
import { findPreset } from "@/features/ai/presets";
import {
  MODEL_CATEGORIES,
  normalizeBaseUrl,
  PROVIDER_TYPES,
  type AiProvider,
  type ProviderType,
} from "@/features/ai/types";

/**
 * URL-parameter quick configuration, in the style of LobeChat's `?settings=`.
 *
 * The canonical form is a Base64URL-encoded payload in the hash fragment. The query form and
 * the older `base64:` prefix remain accepted for links already in circulation. A hash is
 * preferred because it is never sent to the server, so the API key does not appear in the
 * request line or the server-rendered referrer.
 */

export const QUICK_CONFIG_PARAM = "settings";

const BASE64_PREFIX = "base64:";
const BASE64URL_PREFIX = "base64url:";
const MAX_CONFIG_BYTES = 512 * 1024;

const modelSchema = z.object({
  id: z.string().trim().min(1).max(200),
  name: z.string().trim().min(1).max(200).optional(),
  category: z.enum(MODEL_CATEGORIES).default("chat"),
  type: z.enum(PROVIDER_TYPES).optional(),
  icon: z.string().trim().max(2048).optional(),
  contextLength: z.number().int().positive().max(100_000_000).optional(),
  maxOutputTokens: z.number().int().positive().max(10_000_000).optional(),
  supportsVision: z.boolean().optional(),
  supportsFunctionCall: z.boolean().optional(),
  supportsThinking: z.boolean().optional(),
  enabled: z.boolean().optional(),
});

const providerSchema = z
  .object({
    builtinId: z.string().trim().min(1).max(64).optional(),
    name: z.string().trim().min(1).max(200).optional(),
    description: z.string().trim().max(1000).optional(),
    icon: z.string().trim().max(2048).optional(),
    presetVersion: z.number().int().positive().max(100_000).optional(),
    apiKey: z.string().trim().min(1).max(4096).optional(),
    baseUrl: z.string().trim().min(1).max(2048).optional(),
    type: z.enum(PROVIDER_TYPES).optional(),
    // Optional is intentional: built-ins can inherit their preset models and custom providers
    // may be imported before the user decides which upstream models to enable.
    models: z.array(modelSchema).max(200).optional(),
  })
  .superRefine((provider, context) => {
    if (provider.builtinId === undefined) {
      if (provider.name === undefined) {
        context.addIssue({
          code: "custom",
          path: ["name"],
          message: "custom provider requires name",
        });
      }

      if (provider.type === undefined) {
        context.addIssue({
          code: "custom",
          path: ["type"],
          message: "custom provider requires type",
        });
      }

      if (provider.baseUrl === undefined) {
        context.addIssue({
          code: "custom",
          path: ["baseUrl"],
          message: "custom provider requires baseUrl",
        });
      }
    }
  });

export const quickConfigSchema = z.object({
  autoApply: z.boolean().default(false),
  providers: z.array(providerSchema).min(1).max(50),
});

export type QuickConfigInput = z.input<typeof quickConfigSchema>;
export type QuickConfig = z.infer<typeof quickConfigSchema>;
type QuickConfigProviderInput = z.input<typeof providerSchema>;
export type QuickConfigProvider = z.infer<typeof providerSchema>;

export interface ImportConfigModel {
  modelKey: string;
  name: string;
  type: ProviderType | null;
  category: (typeof MODEL_CATEGORIES)[number];
  icon: string | null;
  contextLength: number | null;
  maxOutputTokens: number | null;
  supportsVision: boolean;
  supportsFunctionCall: boolean;
  supportsThinking: boolean;
  enabled: boolean;
}

export interface ImportConfigProvider {
  builtinId: string | null;
  name: string;
  description: string | null;
  icon: string | null;
  presetVersion: number | null;
  type: ProviderType;
  apiKey: string | null;
  baseUrl: string | null;
  models: ImportConfigModel[];
}

export interface ResolvedQuickConfig {
  autoApply: boolean;
  requiresConfirmation: boolean;
  providers: ImportConfigProvider[];
}

export type QuickConfigResult =
  | { ok: true; config: QuickConfig }
  | { ok: false; reason: "absent" }
  | { ok: false; reason: "malformed"; detail: string };

function byteString(bytes: Uint8Array): string {
  let result = "";
  const chunkSize = 0x8000;

  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    result += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize));
  }

  return result;
}

function decodeBase64(value: string, urlSafe: boolean): string {
  if (value.length > Math.ceil((MAX_CONFIG_BYTES * 4) / 3) + 16) {
    throw new Error("payload is too large");
  }

  // URLSearchParams treats `+` as a space. Repair that only for legacy standard Base64; the
  // canonical Base64URL alphabet has neither character and therefore cannot be ambiguous.
  let normalized = urlSafe ? value : value.replace(/ /g, "+");
  if (urlSafe) {
    normalized = normalized.replace(/-/g, "+").replace(/_/g, "/");
  }

  if (!/^[A-Za-z0-9+/]*={0,2}$/.test(normalized)) {
    throw new Error("invalid base64 alphabet");
  }

  normalized = normalized.replace(/=+$/, "");
  normalized += "=".repeat((4 - (normalized.length % 4)) % 4);

  const binary = atob(normalized);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  if (bytes.byteLength > MAX_CONFIG_BYTES) {
    throw new Error("payload is too large");
  }

  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

function validateBaseUrl(baseUrl: string): string | null {
  try {
    const url = new URL(baseUrl);
    if (url.protocol !== "http:" && url.protocol !== "https:") return "baseUrl must use HTTP or HTTPS";
    if (!url.hostname) return "baseUrl must include a host";
    if (url.username || url.password) return "baseUrl must not include credentials";
    if (url.search || url.hash) return "baseUrl must not include a query or fragment";
    return null;
  } catch {
    return "baseUrl must be an absolute HTTP(S) URL";
  }
}

function validateConfig(config: QuickConfig): string | null {
  for (const provider of config.providers) {
    if (provider.builtinId !== undefined && !findPreset(provider.builtinId)) {
      return `unknown builtin provider '${provider.builtinId}'`;
    }

    if (provider.baseUrl) {
      const error = validateBaseUrl(provider.baseUrl);
      if (error) return error;
    }

    for (const model of provider.models ?? []) {
      if (model.type && model.category === "chat") {
        // The backend remains the final authority, but rejecting empty/invalid model protocol
        // data here makes the preview fail before any secret is sent to the API.
        if (!PROVIDER_TYPES.includes(model.type)) return `unknown model protocol '${model.type}'`;
      }
    }
  }

  return null;
}

/** Parses the quick-config parameter out of a query string. */
export function parseQuickConfig(search: string | URLSearchParams): QuickConfigResult {
  const params = typeof search === "string" ? new URLSearchParams(search) : search;
  return parseRaw(params.get(QUICK_CONFIG_PARAM));
}

/**
 * Reads the config from either the query string or the hash fragment. A malformed hash wins over
 * a valid query so a broken canonical link is not silently replaced by an unrelated parameter.
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
    if (raw.startsWith(BASE64URL_PREFIX)) {
      json = decodeBase64(raw.slice(BASE64URL_PREFIX.length), true);
    } else if (raw.startsWith(BASE64_PREFIX)) {
      json = decodeBase64(raw.slice(BASE64_PREFIX.length), false);
    } else {
      if (new TextEncoder().encode(raw).byteLength > MAX_CONFIG_BYTES) {
        return { ok: false, reason: "malformed", detail: "payload is too large" };
      }
      json = raw;
    }
  } catch (error) {
    return {
      ok: false,
      reason: "malformed",
      detail: error instanceof Error ? error.message : "base64 decode failed",
    };
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

  const validationError = validateConfig(result.data);
  if (validationError) {
    return { ok: false, reason: "malformed", detail: validationError };
  }

  return { ok: true, config: result.data };
}

function findExistingProvider(
  provider: QuickConfigProvider | QuickConfigProviderInput,
  existingProviders: readonly AiProvider[],
): AiProvider | undefined {
  if (provider.builtinId) {
    return existingProviders.find((candidate) => candidate.builtinId === provider.builtinId);
  }

  const name = provider.name?.trim().toLowerCase();
  return name
    ? existingProviders.find((candidate) => candidate.name.trim().toLowerCase() === name)
    : undefined;
}

function presetModels(preset: NonNullable<ReturnType<typeof findPreset>>): ImportConfigModel[] {
  return preset.defaultModels.map((model) => ({
    modelKey: model.modelKey,
    name: model.name,
    type: model.type ?? null,
    category: model.category,
    icon: null,
    contextLength: model.contextLength ?? null,
    maxOutputTokens: model.maxOutputTokens ?? null,
    supportsVision: model.supportsVision ?? false,
    supportsFunctionCall: model.supportsFunctionCall ?? false,
    supportsThinking: model.supportsThinking ?? false,
    enabled: model.enabled ?? true,
  }));
}

/**
 * Resolves URL entries into the normalized payload accepted by the API.
 *
 * Preset values fill only omitted fields. Existing providers are consulted so a minimal built-in
 * link containing only `builtinId` and `apiKey` does not reset a user's custom proxy URL. Default
 * preset models are sent whenever the link omits `models`; the API inserts them absent-only so
 * existing per-model edits remain untouched.
 */
export function resolveQuickConfig(
  config: QuickConfig | QuickConfigInput,
  existingProviders: readonly AiProvider[] = [],
): ResolvedQuickConfig {
  const providers = config.providers.map((provider) => {
    const preset = findPreset(provider.builtinId);
    const existing = findExistingProvider(provider, existingProviders);
    const type = provider.type ?? existing?.type ?? preset?.type ?? "openai-chat";
    const name = provider.name ?? existing?.name ?? preset?.name ?? provider.builtinId ?? "";
    const baseUrl = provider.baseUrl ?? existing?.baseUrl ?? preset?.defaultBaseUrl ?? null;
    const models = provider.models ?? (preset ? presetModels(preset) : []);

    return {
      builtinId: provider.builtinId ?? existing?.builtinId ?? null,
      name,
      description: provider.description ?? existing?.description ?? preset?.description ?? null,
      icon: provider.icon ?? existing?.icon ?? null,
      presetVersion: provider.presetVersion ?? preset?.version ?? existing?.presetVersion ?? null,
      type,
      apiKey: provider.apiKey ?? null,
      baseUrl: baseUrl ? normalizeBaseUrl(baseUrl, type) : null,
      models: models.map((model) => {
        const modelKey = "id" in model ? model.id : model.modelKey;

        return {
          modelKey,
          name: model.name ?? modelKey,
          type: model.type ?? null,
          category: model.category ?? "chat",
          icon: model.icon ?? null,
          contextLength: model.contextLength ?? null,
          maxOutputTokens: model.maxOutputTokens ?? null,
          supportsVision: model.supportsVision ?? false,
          supportsFunctionCall: model.supportsFunctionCall ?? false,
          supportsThinking: model.supportsThinking ?? false,
          enabled: model.enabled ?? true,
        };
      }),
    } satisfies ImportConfigProvider;
  });

  return {
    autoApply: config.autoApply ?? false,
    requiresConfirmation: providers.some((provider) => provider.apiKey !== null),
    providers,
  };
}

/** Masks a key for display. Never render the full value — the dialog exists to be screenshotted. */
export function maskApiKey(apiKey: string | undefined): string {
  if (!apiKey) return "";
  const trimmed = apiKey.trim();
  if (trimmed.length <= 8) return "••••";
  return `${trimmed.slice(0, 3)}••••${trimmed.slice(-4)}`;
}

/**
 * Backwards-compatible helper used by callers that already have a single provider entry.
 * `resolveQuickConfig` should be preferred for complete links because it also handles existing
 * provider identity and preset model defaults.
 */
export function toImportPayload(
  provider: QuickConfigProvider | QuickConfigProviderInput,
  fallbackType: ProviderType,
  existingProviders: readonly AiProvider[] = [],
): ImportConfigProvider {
  const type = provider.type ?? findPreset(provider.builtinId)?.type ?? fallbackType;
  const input = provider.type === undefined ? { ...provider, type } : provider;
  const resolved = resolveQuickConfig(
    { autoApply: false, providers: [input] },
    existingProviders,
  ).providers[0];

  return {
    ...resolved,
    type: resolved.type,
  };
}

/** Strips the parameter from a URL while preserving unrelated query/hash parameters. */
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

function encodeBase64Url(bytes: Uint8Array): string {
  return btoa(byteString(bytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** Builds a canonical share link with a Base64URL payload in the fragment. */
export function buildShareLink(
  origin: string,
  path: string,
  config: QuickConfig | QuickConfigInput,
): string {
  const json = JSON.stringify(config);
  const bytes = new TextEncoder().encode(json);
  if (bytes.byteLength > MAX_CONFIG_BYTES) {
    throw new Error("configuration payload is too large");
  }

  return `${origin}${path}#${QUICK_CONFIG_PARAM}=${BASE64URL_PREFIX}${encodeBase64Url(bytes)}`;
}
