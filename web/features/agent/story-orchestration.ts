import type { Locale } from "@/lib/i18n/config";

/**
 * The built-in novel → storyboard orchestration prompt.
 *
 * A one-click story run is one long Agent prompt, not new plumbing: every canvas tool it needs
 * (create_node, connect_nodes, run_node, retry_node, move_nodes) already exists, so the quality
 * of the run lives or dies on how precisely the prompt pins down the pipeline — which nodes to
 * build, how to keep a character looking like themselves across shots, how long each shot should
 * run, and where on the canvas everything lands.
 *
 * Characters get the two-step treatment before any shot exists: a structured anatomy dossier
 * (face/hair/outfit layers/palette, everything reproducible) plus a turnaround sheet — one
 * 16:9 image holding front/side/back views of the same character on a clean background. Every
 * shot then wires that sheet in as its reference image, so cross-shot consistency comes from
 * data on the graph, not from the model's memory of an earlier prompt.
 *
 * The layout convention is load-bearing: `create_node` requires an explicit position, and the
 * config/output split means a run's outputs spawn to the RIGHT of their config node. Each shot
 * is therefore one horizontal chain (text → imageGen → [image] → videoGen → [video]), and shots
 * stack downward — which is also the direction the canvas already reads.
 */

/** Longest novel accepted in one run. Longer inputs should be split by the user for now. */
export const MAX_STORY_LENGTH = 8000;

/** The story extractor pulls at most this many character cards, leads first. */
export const MAX_STORY_CHARACTERS = 4;

/** Shot counts the dialog offers; "auto" is `null` (the model paces from the story's beats). */
export const STORY_SHOT_OPTIONS = [4, 6, 8, 12] as const;

/** Fixed per-shot durations the dialog offers; "auto" is `null` (3s/5s/8s by shot type). */
export const STORY_DURATION_OPTIONS = [3, 5, 8] as const;

export const STORY_STYLE_OPTIONS = [
  "cinematic",
  "anime",
  "guofeng",
  "realistic",
  "storybook",
] as const;

export type StoryStyle = (typeof STORY_STYLE_OPTIONS)[number] | "none";

export interface StoryOrchestrationOptions {
  /** The novel text to adapt. Assumed already trimmed and length-checked by the caller. */
  story: string;
  /** Target shot count; `null` lets the model decide from the story's beats. */
  shotCount: number | null;
  /** Fixed seconds per shot; `null` lets the model pace each shot (3s/5s/8s by type). */
  secondsPerShot: number | null;
  /** When false the run stops after storyboard frames — the cheap, reviewable checkpoint. */
  generateVideos: boolean;
  /** The image model to configure every imageGen with (provider/model config ids). */
  imageProviderId: string;
  imageModelId: string;
  /** The video model; required when `generateVideos` is true. */
  videoProviderId?: string;
  videoModelId?: string;
  /** Style keyword folded into every shot prompt. */
  style: StoryStyle;
  locale: Locale;
}

/** Shot counts clamp to this band whether the user or the model picked them. */
export const SHOT_COUNT_RANGE = { min: 2, max: 12 } as const;

export function resolveShotCount(requested: number | null): number | null {
  if (requested === null) return null;
  return Math.min(SHOT_COUNT_RANGE.max, Math.max(SHOT_COUNT_RANGE.min, Math.round(requested)));
}

/** A fixed duration must be one of the offered pacing values, or the model paces freely. */
export function resolveSecondsPerShot(requested: number | null): number | null {
  if (requested === null) return null;
  return (STORY_DURATION_OPTIONS as readonly number[]).includes(requested) ? requested : null;
}

/**
 * What one run will ask the providers for, shown in the dialog before the user commits.
 *
 * Images: one turnaround sheet per character (worst case, `MAX_STORY_CHARACTERS`) plus one first
 * frame per shot. Videos: at most one per shot. Generation failure means these are ceilings, not
 * promises — which is the right direction for a cost warning.
 */
export function estimateStoryRun(shotCount: number | null, generateVideos: boolean): {
  images: number;
  videos: number;
} {
  const shots = resolveShotCount(shotCount) ?? 8;
  return {
    images: MAX_STORY_CHARACTERS + shots,
    videos: generateVideos ? shots : 0,
  };
}

export type StoryValidationError =
  | "story-required"
  | "story-too-long"
  | "image-model-required"
  | "video-model-required";

export function validateStoryOrchestration(
  options: StoryOrchestrationOptions,
): StoryValidationError | null {
  if (!options.story.trim()) return "story-required";
  if (options.story.length > MAX_STORY_LENGTH) return "story-too-long";
  if (!options.imageProviderId || !options.imageModelId) return "image-model-required";
  if (options.generateVideos && (!options.videoProviderId || !options.videoModelId)) {
    return "video-model-required";
  }
  return null;
}

/**
 * Builds the orchestration prompt.
 *
 * The novel rides inside explicit fences and a literal closing fence in the pasted text is
 * neutralized, so a story that quotes tags or mentions tool names reads as data — this prompt is
 * assembled from user input and handed to a model that can spend money.
 */
export function buildStoryPrompt(options: StoryOrchestrationOptions): string {
  // Rendered once here so both locale templates interpolate exactly the same sanitized text.
  const story = options.story.replace(/<\/(story)/gi, "<\\/$1");
  return options.locale === "en"
    ? buildEnglish({ ...options, story })
    : buildChinese({ ...options, story });
}

function parameterBlock(options: StoryOrchestrationOptions): string {
  const shotCount = resolveShotCount(options.shotCount);
  const seconds = resolveSecondsPerShot(options.secondsPerShot);

  if (options.locale === "en") {
    return [
      `- Shot count: ${shotCount ?? `auto — decide from the story's beats, ${SHOT_COUNT_RANGE.min}-${SHOT_COUNT_RANGE.max} shots`}`,
      `- Seconds per shot: ${seconds ?? "auto — 3s dialogue/reaction, 5s single action, 8s establishing"}`,
      `- Generate videos: ${options.generateVideos ? "yes" : "no — stop after storyboard frames"}`,
      `- Image model: providerId=${options.imageProviderId} modelId=${options.imageModelId}`,
      options.generateVideos
        ? `- Video model: providerId=${options.videoProviderId} modelId=${options.videoModelId}`
        : "- Video model: (unused)",
      `- Visual style: ${options.style === "none" ? "none" : options.style}`,
    ].join("\n");
  }

  return [
    `- 分镜数量：${shotCount ?? `自动 — 按剧情节奏决定，${SHOT_COUNT_RANGE.min}-${SHOT_COUNT_RANGE.max} 镜`}`,
    `- 每镜时长：${seconds ?? "自动 — 对话/反应镜头 3s，单动作镜头 5s，场景建立 8s"}`,
    `- 生成视频：${options.generateVideos ? "是" : "否 — 生成完分镜图即停止"}`,
    `- 图片模型：providerId=${options.imageProviderId} modelId=${options.imageModelId}`,
    options.generateVideos
      ? `- 视频模型：providerId=${options.videoProviderId} modelId=${options.videoModelId}`
      : "- 视频模型：（不使用）",
    `- 画面风格：${options.style === "none" ? "无" : options.style}`,
  ].join("\n");
}

function buildChinese(options: StoryOrchestrationOptions): string {
  const params = parameterBlock(options);

  return `你将把一段小说改编为画布上的分镜视频工作流。严格按四个阶段顺序执行，不要跳步，不要向用户征询确认，遇到失败按各阶段的重试规则处理。

## 任务参数
${params}

## 剧情原文
<story>
${options.story}
</story>

## 布局约定（创建节点时必须给出下列坐标）
- 角色区：第 i 个角色（从 0 数起）占一行 y=i*420。档案 text 在 (0, y)，设定图提示词 text 在 (320, y)，imageGen 在 (640, y)；run_node 产出的三视图设定图会自动出现在 imageGen 右侧。
- 分镜区：第 n 镜（从 1 数起）的一条链放在 y=(n-1)*460，起点 x=1500。text 在 (1500, y)，imageGen 在 (1840, y)，videoGen 在 (2600, y)；产出的首帧图和视频会自动出现在各自配置节点右侧。
- 不要调用 move_nodes / auto arrange：坐标已在创建时给定。

## 阶段一：人物档案与三视图设定图
1. 通读剧情，提取最多 ${MAX_STORY_CHARACTERS} 个主要角色（主角优先）。
2. 每个角色先创建"人物档案"text 节点，用以下分节格式逐项写清。只写可见外观，禁止性格、剧情和抽象形容词（"美丽""帅气"）；颜色必须具体（"藏青色"而非"深色"）；剧情没写到的项按题材合理设定，并在该项末尾标注"（推测）"：
   【档案】{角色名}
   基本：年龄感、性别、体型与身高、姿态气质
   面部：脸型、眉眼、鼻唇、肤色、标记（痣/疤/常妆）
   发型：长度、颜色、造型
   服装-外层：品类、版型、材质、主色与辅色、细节（纹样/磨损/扣件）
   服装-中层与底层：同上，逐层写
   鞋履与配饰：鞋、首饰、随身道具
   色板：全身上下不超过 5 种主色，逐个列出
3. 再创建"设定图提示词"text 节点，内容是一段自包含的图像提示词：
   角色三视图设定图，同一角色：{角色名}。全身站姿，正面视图、侧面视图、背面视图水平并排，三个视图必须是同一个人，面容、发型、服装完全一致。纯净浅灰背景，均匀柔光，无场景、无道具、无其他人物。{把档案全部要点压缩成 80-120 字的外观描述，颜色与服装细节逐项保留}。角色设定图风格，线条与细节完整清晰。
4. 创建一个 imageGen 节点：图片模型（providerId/modelId 用任务参数中的值）、aspect="16:9"、count=1；用一条 connect_nodes 的 connections 批量把档案 text 与设定图提示词 text 连入该 imageGen。
5. 逐个 run_node 生成三视图设定图，记录返回的输出 image 节点 id——这是该角色的标准形象，阶段三要逐镜引用。某个输出节点最终状态为 failed 时，用 retry_node 对该输出节点重试一次；仍失败则跳过该角色并在最终报告中说明。
6. 阶段一结束才进入阶段二。

## 阶段二：分镜拆解
将剧情切分为分镜：
- 每镜是一个完整动作或情节点；场景切换、时间跳变、视角转换处必须分镜。
- 时长规则：对话/反应镜头 3 秒；单个动作镜头 5 秒；场景建立或复杂调度 8 秒。任务参数指定了每镜时长时统一使用该值。duration 必须是 1-60 的整数。
- 每镜恰好一个主运镜，从：推镜（缓推近）、拉镜、摇镜、移镜（横移）、跟镜、固定镜头 中选；并写明主体动作（谁、做什么、情绪、幅度）。
- 每镜创建一个 text 节点，首行是元信息行，其后是画面提示词：
  [镜{n}][{秒数}s][{运镜}]
  {画面提示词}
  提示词要求：具体名词优先；写明环境、光线、构图；每个出场角色必须点名，并逐个复述其档案中的发型、服装主色与标志性配饰（保持跨镜一致，不得只写名字）；含动作与运镜描述；60-120 字；任务参数有画面风格时把风格词放进提示词。
- 记录每镜的出场角色。

## 阶段三：分镜首帧生成
先铺完全部首帧图，再进入阶段四——这样即使中途被停止，已完成的分镜图仍然完整可用。
1. 每镜创建一个 imageGen 节点：图片模型、aspect="16:9"、count=1。
2. 连线：该镜 text → 该镜 imageGen；该镜每个出场角色的三视图设定图 image → 该镜 imageGen（作为角色一致性参考图）。没有出场角色的镜只连 text。连线全部用一条 connect_nodes 的 connections 完成。
3. 逐镜 run_node，记录产出的首帧 image 节点 id。failed 的输出节点用 retry_node 重试一次。

## 阶段四：视频生成与收尾
${options.generateVideos ? `1. 每镜创建一个 videoGen 节点：视频模型、aspect="16:9"、duration={该镜秒数}、resolution="720p"。
2. 连线：该镜首帧 image → 该镜 videoGen。
3. 逐镜 run_node（会等待生成完成，耗时较长）。failed 的输出节点用 retry_node 重试一次。` : "本任务不生成视频，直接进入收尾。"}
最后输出最终报告：角色清单（名字+节点 id）、分镜数、总时长（各镜秒数之和）、失败清单。控制在 200 字以内。

## 执行约束
- 不要复述剧情原文；工具调用之外只允许简短的阶段说明和最终报告。
- 节点一律用 create_node 创建，连线一律用 connect_nodes；imageGen/videoGen 的 data 只能含其支持的字段。
- run_node 在图片/视频生成完成前不会返回，属于正常现象，耐心等待即可。`;
}

function buildEnglish(options: StoryOrchestrationOptions): string {
  const params = parameterBlock(options);

  return `You will adapt a novel excerpt into a storyboard video workflow on the canvas. Execute the four phases in order. Do not skip steps, do not ask the user for confirmation, and apply each phase's retry rule on failure.

## Task parameters
${params}

## Story
<story>
${options.story}
</story>

## Layout convention (every create_node call must pass these coordinates)
- Character zone: character i (0-based) is one row at y=i*420. Put its dossier text at (0, y), its turnaround-prompt text at (320, y), and its imageGen at (640, y); the turnaround sheet that run_node produces appears to the right of the imageGen automatically.
- Shot zone: shot n (1-based) is one chain at y=(n-1)*460, starting at x=1500. Put its text at (1500, y), its imageGen at (1840, y), and its videoGen at (2600, y); the first frame and the video appear to the right of their config nodes automatically.
- Do not call move_nodes or auto-arrange: positions are assigned at creation time.

## Phase 1 — Character dossiers and turnaround sheets
1. Read the story and extract at most ${MAX_STORY_CHARACTERS} main characters (leads first).
2. Create one "dossier" text node per character first, in exactly this sectioned format. Visible appearance only — no personality, no plot, no abstract adjectives ("beautiful", "handsome"); colors must be concrete ("navy blue", never "dark"); for items the story does not specify, invent plausibly for the genre and tag them "(inferred)":
   [DOSSIER] {name}
   Basics: apparent age, gender, build and height, posture and bearing
   Face: face shape, brows and eyes, nose and mouth, skin tone, marks (mole/scar/everyday makeup)
   Hair: length, color, style
   Outfit outer layer: garment type, cut, fabric, primary and accent colors, details (patterns/wear/fasteners)
   Outfit mid and base layers: same, layer by layer
   Footwear and accessories: shoes, jewelry, carried items
   Palette: at most 5 primary colors across the whole body, listed one by one
3. Then create one "sheet prompt" text node per character holding a self-contained image prompt:
   Character reference sheet, turnaround of the same character: {name}. Full body, standing; front view, side view, and back view side by side; all three views must be the same person with identical face, hair, and outfit. Plain light-gray background, even soft light, no scenery, no props, no other people. {Compress every dossier point into an 80-120 word appearance description, keeping each color and outfit detail}. Character-design-sheet style, complete and crisp detail.
4. Create one imageGen node per character: image model (providerId/modelId from the parameters), aspect="16:9", count=1; connect both of the character's text nodes into it with a single connect_nodes batch.
5. run_node each turnaround sheet one at a time and record the returned output image node ids — this sheet is the character's canonical look, referenced per shot in phase 3. If an output node ends up failed, retry_node it once; if it fails again, skip that character and say so in the final report.
6. Finish phase 1 before starting phase 2.

## Phase 2 — Storyboard breakdown
Split the story into shots:
- Each shot is one complete action or beat; cut at scene changes, time jumps, and point-of-view shifts.
- Duration rules: 3s for dialogue/reaction, 5s for a single action, 8s for establishing or complex blocking. If a fixed per-shot duration was specified, use it for every shot. duration must be an integer 1-60.
- Exactly one primary camera move per shot, chosen from: push-in, pull-out, pan, truck (lateral), follow, static; state the subject action (who, does what, emotion, intensity).
- Create one text node per shot; the first line is a metadata line, followed by the visual prompt:
  [Shot {n}][{seconds}s][{camera move}]
  {visual prompt}
  Prompt requirements: concrete nouns first; state environment, lighting, composition; name every character present and restate their dossier's hair, outfit colors, and signature accessories one by one (keeps them consistent across shots — never a bare name); include the action and camera move; 60-120 words; fold the configured visual style keyword into the prompt if one was set.
- Record which characters appear in each shot.

## Phase 3 — First-frame generation
Build every first frame before phase 4 — if the run is stopped midway, the finished storyboard frames remain complete and usable.
1. Create one imageGen node per shot: image model, aspect="16:9", count=1.
2. Connect: that shot's text → its imageGen; every present character's turnaround sheet image → its imageGen (as the character-consistency reference). A shot with no characters connects only its text. Use a single connect_nodes batch for all of a shot's edges.
3. run_node each imageGen one at a time and record the returned first-frame image node ids. retry_node failed outputs once.

## Phase 4 — Video generation and wrap-up
${options.generateVideos ? `1. Create one videoGen node per shot: video model, aspect="16:9", duration={that shot's seconds}, resolution="720p".
2. Connect: that shot's first-frame image → its videoGen.
3. run_node each videoGen in order (this awaits generation and can take minutes). retry_node failed outputs once.` : "This task does not generate videos; go straight to wrap-up."}
Finish with a final report: character list (name + node id), shot count, total duration (sum of shot seconds), and any failures. Keep it under 150 words.

## Execution constraints
- Never repeat the story text; outside tool calls, only brief phase notes and the final report.
- Create nodes only with create_node, edges only with connect_nodes; imageGen/videoGen data may only contain their supported fields.
- run_node does not return until an image/video finishes generating — that is expected; wait patiently.`;
}
