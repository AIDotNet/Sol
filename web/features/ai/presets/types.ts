import type { ModelCategory, ProviderType } from "@/features/ai/types";

/**
 * A built-in provider template.
 *
 * `version` is monotonic: bumping it signals that the preset has new defaults to apply to a
 * device that already persisted it. Model upgrades are absent-only so existing user edits remain
 * authoritative.
 */
export interface ProviderPreset {
  builtinId: string;
  version: number;
  name: string;
  description: string;
  type: ProviderType;
  defaultBaseUrl: string;
  homepage: string;
  apiKeyUrl?: string;
  requiresApiKey?: boolean;
  /** Icon slug in @lobehub/icons-static-svg. Defaults to `builtinId`. */
  iconSlug?: string;
  defaultModels: PresetModel[];
}

export interface PresetModel {
  modelKey: string;
  name: string;
  category: ModelCategory;
  /** Preset version that first shipped this model. Used to add only new defaults on upgrade. */
  introducedInVersion?: number;
  /** Protocol override; omit to inherit the provider's. */
  type?: ProviderType;
  enabled?: boolean;
  contextLength?: number;
  maxOutputTokens?: number;
  supportsVision?: boolean;
  supportsFunctionCall?: boolean;
  supportsThinking?: boolean;
}
