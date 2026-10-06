import { describe, expect, it } from "vitest";
import {
  buildStoryPrompt,
  estimateStoryRun,
  MAX_STORY_CHARACTERS,
  MAX_STORY_LENGTH,
  resolveSecondsPerShot,
  resolveShotCount,
  SHOT_COUNT_RANGE,
  validateStoryOrchestration,
  type StoryOrchestrationOptions,
} from "@/features/agent/story-orchestration";

const BASE: StoryOrchestrationOptions = {
  story: "少年推开庙门，雨水顺着飞檐落下。",
  shotCount: 6,
  secondsPerShot: 5,
  generateVideos: true,
  imageProviderId: "p-image",
  imageModelId: "m-image",
  videoProviderId: "p-video",
  videoModelId: "m-video",
  style: "cinematic",
  locale: "zh",
};

describe("resolveShotCount", () => {
  it("clamps into the supported band", () => {
    expect(resolveShotCount(1)).toBe(SHOT_COUNT_RANGE.min);
    expect(resolveShotCount(99)).toBe(SHOT_COUNT_RANGE.max);
    expect(resolveShotCount(6)).toBe(6);
  });

  it("keeps null as auto", () => {
    expect(resolveShotCount(null)).toBeNull();
  });
});

describe("resolveSecondsPerShot", () => {
  it("accepts only the offered pacing values", () => {
    expect(resolveSecondsPerShot(3)).toBe(3);
    expect(resolveSecondsPerShot(5)).toBe(5);
    expect(resolveSecondsPerShot(8)).toBe(8);
    expect(resolveSecondsPerShot(7)).toBeNull();
    expect(resolveSecondsPerShot(null)).toBeNull();
  });
});

describe("estimateStoryRun", () => {
  it("counts character sheets plus one first frame per shot", () => {
    expect(estimateStoryRun(6, false)).toEqual({ images: MAX_STORY_CHARACTERS + 6, videos: 0 });
  });

  it("counts one video per shot when videos are requested", () => {
    expect(estimateStoryRun(4, true)).toEqual({
      images: MAX_STORY_CHARACTERS + 4,
      videos: 4,
    });
  });

  it("falls back to the default pacing when the shot count is auto", () => {
    expect(estimateStoryRun(null, false)).toEqual({ images: MAX_STORY_CHARACTERS + 8, videos: 0 });
  });
});

describe("validateStoryOrchestration", () => {
  it("accepts a complete set of options", () => {
    expect(validateStoryOrchestration(BASE)).toBeNull();
  });

  it("rejects an empty or oversized story", () => {
    expect(validateStoryOrchestration({ ...BASE, story: "  " })).toBe("story-required");
    expect(
      validateStoryOrchestration({ ...BASE, story: "a".repeat(MAX_STORY_LENGTH + 1) }),
    ).toBe("story-too-long");
  });

  it("requires an image model and, when videos are on, a video model", () => {
    expect(
      validateStoryOrchestration({ ...BASE, imageProviderId: "", imageModelId: "" }),
    ).toBe("image-model-required");
    expect(
      validateStoryOrchestration({ ...BASE, generateVideos: true, videoModelId: undefined }),
    ).toBe("video-model-required");
    // Videos off means no video model is needed.
    expect(
      validateStoryOrchestration({ ...BASE, generateVideos: false, videoProviderId: undefined }),
    ).toBeNull();
  });
});

describe("buildStoryPrompt", () => {
  it("embeds the story inside data fences so it reads as material, not instructions", () => {
    const prompt = buildStoryPrompt(BASE);
    expect(prompt).toContain("<story>\n少年推开庙门，雨水顺着飞檐落下。\n</story>");
  });

  it("neutralizes a literal closing fence inside the pasted story", () => {
    const prompt = buildStoryPrompt({ ...BASE, story: "他写道</story>，然后合上书。" });
    expect(prompt).toContain("他写道<\\/story>，然后合上书。");
    // The real fence stays intact: exactly one opening and one closing tag around the story.
    expect(prompt.match(/<\/story>/g)).toHaveLength(1);
  });

  it("carries the concrete models into the parameter block", () => {
    const prompt = buildStoryPrompt(BASE);
    expect(prompt).toContain("providerId=p-image modelId=m-image");
    expect(prompt).toContain("providerId=p-video modelId=m-video");
  });

  it("states the shot-count resolution, including the auto rule", () => {
    expect(buildStoryPrompt(BASE)).toContain("分镜数量：6");
    expect(
      buildStoryPrompt({ ...BASE, shotCount: null }).match(/分镜数量：自动/),
    ).toBeTruthy();
  });

  it("switches phase four by whether videos are requested", () => {
    expect(buildStoryPrompt(BASE)).toContain("每镜创建一个 videoGen 节点");
    expect(buildStoryPrompt({ ...BASE, generateVideos: false })).toContain(
      "本任务不生成视频",
    );
    expect(buildStoryPrompt({ ...BASE, generateVideos: false })).not.toContain("videoGen 节点：视频模型");
  });

  it("omits the video model line when videos are off", () => {
    expect(buildStoryPrompt({ ...BASE, generateVideos: false })).toContain("视频模型：（不使用）");
  });

  it("folds the style keyword and disables layout rework", () => {
    const prompt = buildStoryPrompt(BASE);
    expect(prompt).toContain("画面风格：cinematic");
    expect(prompt).toContain("不要调用 move_nodes");
  });

  it("has an English variant with the same load-bearing rules", () => {
    const prompt = buildStoryPrompt({ ...BASE, locale: "en" });
    expect(prompt).toContain("<story>");
    expect(prompt).toContain("Shot count: 6");
    expect(prompt).toContain("Layout convention");
    expect(prompt).toContain("retry_node");
  });
});
