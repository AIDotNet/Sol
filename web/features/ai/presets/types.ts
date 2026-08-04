import type { ModelCategory, ProviderType } from "@/features/ai/types";

/**
 * A built-in provider template.
 *
 * `version` is monotonic: bumping it signals that the preset's values should replace what a
 * device already persisted. Without it there is no way to ship a corrected base URL or a new
 * model to existing users without also clobbering their own edits.
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
  /** Protocol override; omit to inherit the provider's. */
  type?: ProviderType;
  enabled?: boolean;
  contextLength?: number;
  maxOutputTokens?: number;
  supportsVision?: boolean;
  supportsFunctionCall?: boolean;
  supportsThinking?: boolean;
}
