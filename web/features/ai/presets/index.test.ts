import { describe, expect, it } from "vitest";
import { findPreset } from "@/features/ai/presets";

const SD_VIDEO_MODEL_KEYS = [
  "sd-mini-720p",
  "sd-mini-480p",
  "sd-fast-720p",
  "sd-fast-480p",
  "sd-2.0-480p",
  "sd-2.0-720p",
  "sd-2.0-1080p",
] as const;

describe("Routin AI preset", () => {
  it("includes the SD video models with the OpenAI video protocol", () => {
    const preset = findPreset("routin-ai");

    expect(preset?.version).toBe(2);

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
});
