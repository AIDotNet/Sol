import { describe, expect, it } from "vitest";
import { PROVIDER_PRESETS, findPreset } from "@/features/ai/presets";

const SD_VIDEO_MODEL_KEYS = [
  "sd-mini-720p",
  "sd-mini-480p",
  "sd-fast-720p",
  "sd-fast-480p",
  "sd-2.0-480p",
  "sd-2.0-720p",
  "sd-2.0-1080p",
] as const;

const RETIRED_MODEL_KEYS = [
  // Kimi left the Routin catalog entirely.
  "kimi-k3",
  "kimi-k2.7-code",
  // MiMo TTS and Seedance video are no longer offered.
  "mimo-v2.5-tts",
  "mimo-v2.5-tts-voiceclone",
  "doubao-seedance-2-0-260128",
  // Renamed or retired upstream.
  "grok-4.20",
  "gpt-5-chat",
  "Qwen3.8-Max-Preview",
] as const;

const V4_MODEL_KEYS = [
  "gpt-6-astra",
  "gpt-6.1-sol",
  "claude-fable-5-1",
  "claude-opus-5-5",
  "claude-sonnet-5-5",
  "mimo-v2.6-pro",
  "glm-5.3-flash",
  "qwen3.8-max",
  "gemini-3.8-flash",
  "grok-4.7",
  "grok-imagine-image-2.0",
  "gpt-4o-mini-tts",
] as const;

describe("Routin AI preset", () => {
  it("includes the SD video models with the OpenAI video protocol", () => {
    const preset = findPreset("routin-ai");

    expect(preset?.version).toBe(4);

    for (const modelKey of SD_VIDEO_MODEL_KEYS) {
      expect(preset?.defaultModels.find((model) => model.modelKey === modelKey)).toMatchObject({
        category: "video",
        introducedInVersion: 2,
        type: "openai-video",
        supportsVision: true,
        enabled: true,
      });
    }
  });

  it("ships the 2026-10 catalog refresh as v4 additions", () => {
    const preset = findPreset("routin-ai");

    for (const modelKey of V4_MODEL_KEYS) {
      expect(preset?.defaultModels.find((model) => model.modelKey === modelKey)).toMatchObject({
        introducedInVersion: 4,
        enabled: true,
      });
    }
  });

  it("drops models that no longer exist on Routin", () => {
    const preset = findPreset("routin-ai");
    const keys = new Set(preset?.defaultModels.map((model) => model.modelKey));

    for (const modelKey of RETIRED_MODEL_KEYS) {
      expect(keys.has(modelKey)).toBe(false);
    }
  });
});

describe("Vendor presets", () => {
  it("bumps every refreshed vendor to version 2 with tagged additions", () => {
    for (const builtinId of ["openai", "anthropic", "google", "volcengine", "xai", "deepseek"]) {
      const preset = findPreset(builtinId);
      expect(preset?.version, builtinId).toBe(2);
      expect(
        preset?.defaultModels.some((model) => model.introducedInVersion === 2),
        `${builtinId} should tag its v2 additions`,
      ).toBe(true);
    }
  });

  it("keeps presets free of retired upstream models", () => {
    const keys = new Set(
      PROVIDER_PRESETS.flatMap((preset) => preset.defaultModels.map((model) => model.modelKey)),
    );

    // grok-2-image retired 2026-05-15; Imagen 4 and Gemini 2.5 Flash are closed
    // to new Google API keys.
    for (const modelKey of ["grok-2-image-1212", "imagen-4.0-generate-001", "gemini-2.5-flash"]) {
      expect(keys.has(modelKey)).toBe(false);
    }
  });
});
