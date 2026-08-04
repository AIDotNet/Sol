/**
 * AI provider and model types.
 *
 * Ported from OpenCowork, with the Electron-only concerns dropped (OAuth device flows, system
 * proxy, TLS bypass) — those don't apply to a browser client talking to our own backend.
 *
 * The load-bearing idea, kept intact: a model may carry its own `type`, and falls back to the
 * provider's when it doesn't. One aggregator provider can therefore serve models that speak
 * three different wire protocols. `resolveProtocol` below is the single place that rule lives,
 * and the backend implements the identical rule.
 */

/** Wire protocol. Determines how a request is built, not who serves it. */
export const PROVIDER_TYPES = [
  "openai-chat",
  "openai-responses",
  "anthropic",
  "gemini",
  "openai-images",
  "openai-video",
  "seedance-video",
  "xai-video",
] as const;

export type ProviderType = (typeof PROVIDER_TYPES)[number];

export const MODEL_CATEGORIES = ["chat", "image", "video", "embedding", "speech"] as const;

export type ModelCategory = (typeof MODEL_CATEGORIES)[number];

/** Which protocols can serve which category. Used to validate a model's protocol override. */
export const PROTOCOLS_BY_CATEGORY: Record<ModelCategory, readonly ProviderType[]> = {
  chat: ["openai-chat", "openai-responses", "anthropic", "gemini"],
  image: ["openai-images", "gemini"],
  video: ["openai-video", "seedance-video", "xai-video"],
  embedding: ["openai-chat"],
  speech: ["openai-chat"],
};

export interface AiModel {
  id: string;
  /** The identifier sent upstream, e.g. `gpt-image-1`. Distinct from the row id. */
  modelKey: string;
  name: string;
  enabled: boolean;
  /** Protocol override. Falls back to the provider's `type` when unset. */
  type?: ProviderType | null;
  category: ModelCategory;
  /** Icon slug; when unset it is inferred from `modelKey`. */
  icon?: string | null;
  contextLength?: number | null;
  maxOutputTokens?: number | null;
  supportsVision?: boolean;
  supportsFunctionCall?: boolean;
  supportsThinking?: boolean;
}

export interface AiProvider {
  id: string;
  /** Set when this provider came from a built-in preset; null for user-defined ones. */
  builtinId?: string | null;
  name: string;
  description?: string | null;
  /** User-supplied icon (data URL). Falls back to the preset's logo. */
  icon?: string | null;
  type: ProviderType;
  baseUrl: string;
  enabled: boolean;
  /**
   * Whether a key is stored. The key itself is never sent to the browser — the server holds it
   * encrypted and injects it when proxying.
   */
  hasApiKey: boolean;
  /** Last four characters of the stored key, for recognition only. */
  apiKeyHint?: string | null;
  models: AiModel[];
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

/**
 * Resolves the protocol for a (provider, model) pair.
 *
 * An image model with no explicit override defaults to `openai-images` rather than the
 * provider's chat protocol, because a provider is almost always declared by its chat protocol
 * and its image endpoint speaks something else.
 */
export function resolveProtocol(
  provider: Pick<AiProvider, "type">,
  model: Pick<AiModel, "type" | "category"> | undefined,
): ProviderType {
  if (!model) return provider.type;
  if (model.type) return model.type;

  if (model.category === "image") return "openai-images";
  if (model.category === "video") return "seedance-video";

  return provider.type;
}

/**
 * Trims provider-specific suffixes that the protocol client re-appends itself.
 *
 * Users paste whatever the vendor's docs show, which is usually the OpenAI-compatible URL.
 * Without this, an Anthropic base URL of `https://api.anthropic.com/v1` becomes
 * `https://api.anthropic.com/v1/v1/messages`.
 */
export function normalizeBaseUrl(baseUrl: string, protocol: ProviderType): string {
  const trimmed = baseUrl.trim().replace(/\/+$/, "");

  if (protocol === "anthropic") {
    return trimmed.replace(/\/v1(?:\/messages)?$/i, "");
  }

  if (protocol === "gemini") {
    return trimmed.replace(/\/openai$/i, "");
  }

  return trimmed;
}

/** Dedupe key for the global model catalog: the same model reached via two providers is one entry. */
export function normalizeModelKey(modelKey: string): string {
  return modelKey.trim().toLowerCase();
}

export function enabledModels(provider: AiProvider, category?: ModelCategory): AiModel[] {
  return provider.models.filter(
    (model) => model.enabled && (category === undefined || model.category === category),
  );
}

/** A provider is usable when it is enabled and either holds a key or doesn't need one. */
export function isProviderReady(provider: AiProvider, requiresApiKey: boolean): boolean {
  return provider.enabled && (provider.hasApiKey || !requiresApiKey);
}
