"use client";

import { Bot } from "lucide-react";
import Image from "next/image";
import { useState } from "react";
import { findPreset } from "@/features/ai/presets";
import { cn } from "@/lib/utils";

/**
 * Provider and model logos.
 *
 * Icons are served from public/provider-icons/ (populated by scripts/sync-provider-icons.mjs)
 * rather than fetched from a CDN — a web app shouldn't take a runtime dependency on unpkg for
 * something this small, and it would break offline and in restricted networks.
 */

/** Maps a provider's builtinId to its icon file. Defaults to the builtinId itself. */
const PROVIDER_SLUG_OVERRIDES: Record<string, string> = {
  anthropic: "claude",
  google: "gemini",
  volcengine: "doubao",
  siliconflow: "siliconcloud",
  xai: "grok",
};

/**
 * Infers a brand icon from a model identifier.
 *
 * Models discovered through a provider's /v1/models carry no icon metadata, so this gives them
 * correct branding without any configuration. Order matters: the first match wins, so more
 * specific patterns come first.
 */
const MODEL_SLUG_PATTERNS: ReadonlyArray<[RegExp, string]> = [
  [/claude/i, "claude"],
  [/^(gpt|o[1-9]|chatgpt|dall-e|sora)/i, "openai"],
  [/(gemini|imagen|veo)/i, "gemini"],
  [/(seedream|seedance|doubao)/i, "doubao"],
  [/deepseek/i, "deepseek"],
  [/(qwen|qwq)/i, "qwen"],
  [/grok/i, "grok"],
  [/(glm|chatglm)/i, "zhipu"],
  [/minimax/i, "minimax"],
  [/(moonshot|kimi)/i, "moonshot"],
  [/llama/i, "meta"],
  [/mistral/i, "mistral"],
];

export function detectModelIconSlug(modelKey: string): string | null {
  for (const [pattern, slug] of MODEL_SLUG_PATTERNS) {
    if (pattern.test(modelKey)) return slug;
  }
  return null;
}

export function providerIconSlug(builtinId: string | null | undefined): string | null {
  if (!builtinId) return null;
  return PROVIDER_SLUG_OVERRIDES[builtinId] ?? findPreset(builtinId)?.iconSlug ?? builtinId;
}

interface IconProps {
  size?: number;
  className?: string;
}

function FallbackIcon({ size = 20, className }: IconProps) {
  return (
    <Bot
      className={cn("text-muted-foreground", className)}
      style={{ width: size, height: size }}
      aria-hidden
    />
  );
}

/**
 * Renders an icon from a slug, falling back to a generic bot glyph.
 *
 * A slug can go stale (a preset renamed, the sync script not re-run), and a broken image icon
 * looks like a bug — so a load error swaps in the fallback instead.
 */
function SlugIcon({
  slug,
  alt,
  size = 20,
  className,
}: IconProps & { slug: string | null; alt: string }) {
  const [failed, setFailed] = useState(false);

  if (!slug || failed) {
    return <FallbackIcon size={size} className={className} />;
  }

  return (
    <Image
      src={`/provider-icons/${slug}.svg`}
      alt={alt}
      width={size}
      height={size}
      className={cn("object-contain", className)}
      onError={() => setFailed(true)}
      unoptimized
    />
  );
}

export function ProviderIcon({
  builtinId,
  icon,
  name,
  size = 20,
  className,
}: IconProps & {
  builtinId?: string | null;
  /** User-supplied data URL, which wins over the preset logo. */
  icon?: string | null;
  name?: string;
}) {
  const alt = name ?? builtinId ?? "provider";

  if (icon) {
    // A user-supplied data URL: next/image cannot optimize one and rejects it as a src.
    return (
      // eslint-disable-next-line @next/next/no-img-element
      <img
        src={icon}
        alt={alt}
        width={size}
        height={size}
        className={cn("object-contain", className)}
      />
    );
  }

  return <SlugIcon slug={providerIconSlug(builtinId)} alt={alt} size={size} className={className} />;
}

export function ModelIcon({
  modelKey,
  icon,
  builtinId,
  size = 16,
  className,
}: IconProps & {
  modelKey: string;
  /** Explicit slug stored on the model, which wins over inference. */
  icon?: string | null;
  /** Provider fallback, used when the model id matches no known brand. */
  builtinId?: string | null;
}) {
  const slug = icon ?? detectModelIconSlug(modelKey) ?? providerIconSlug(builtinId);
  return <SlugIcon slug={slug} alt={modelKey} size={size} className={className} />;
}
