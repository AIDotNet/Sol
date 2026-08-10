import { describe, expect, it } from "vitest";
import { fixedVideoResolution } from "@/features/ai/video-models";

describe("fixedVideoResolution", () => {
  it.each([
    ["sd-mini-480p", "480p"],
    ["sd-fast-720p", "720p"],
    ["sd-2.0-1080p", "1080p"],
  ] as const)("reads the resolution encoded in %s", (modelKey, resolution) => {
    expect(fixedVideoResolution(modelKey)).toBe(resolution);
  });

  it("leaves other video models configurable", () => {
    expect(fixedVideoResolution("sora-2")).toBeNull();
  });
});
