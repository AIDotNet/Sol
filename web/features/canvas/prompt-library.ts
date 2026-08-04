/**
 * Curated prompt fragments.
 *
 * These are modifiers rather than complete prompts: a user brings the subject, the library
 * supplies the vocabulary that image models actually respond to. That is why each entry appends
 * to the existing text instead of replacing it.
 *
 * Kept as data with no UI knowledge so the same list can back a picker, a command palette, or
 * autocomplete later.
 */

export type PromptCategory = "style" | "lighting" | "composition" | "quality" | "medium";

export interface PromptEntry {
  id: string;
  category: PromptCategory;
  /** Shown in the picker. */
  label: string;
  /** Appended to the prompt. English, because that is what models are trained on. */
  text: string;
}

export const PROMPT_CATEGORY_LABELS: Record<PromptCategory, { zh: string; en: string }> = {
  style: { zh: "风格", en: "Style" },
  lighting: { zh: "光线", en: "Lighting" },
  composition: { zh: "构图", en: "Composition" },
  medium: { zh: "媒介", en: "Medium" },
  quality: { zh: "质感", en: "Quality" },
};

export const PROMPT_LIBRARY: readonly PromptEntry[] = [
  // Style
  { id: "cinematic", category: "style", label: "电影感", text: "cinematic, film still, anamorphic" },
  { id: "documentary", category: "style", label: "纪实", text: "documentary photography, candid, unposed" },
  { id: "minimal", category: "style", label: "极简", text: "minimalist, negative space, restrained palette" },
  { id: "retro-film", category: "style", label: "胶片", text: "35mm film, Kodak Portra 400, subtle grain" },
  { id: "ukiyoe", category: "style", label: "浮世绘", text: "ukiyo-e woodblock print, flat colour, bold outlines" },
  { id: "isometric", category: "style", label: "等距", text: "isometric view, clean vector shapes" },

  // Lighting
  { id: "golden-hour", category: "lighting", label: "黄金时刻", text: "golden hour, warm rim light, long shadows" },
  { id: "overcast", category: "lighting", label: "阴天", text: "soft overcast light, no harsh shadows" },
  { id: "rembrandt", category: "lighting", label: "伦勃朗光", text: "Rembrandt lighting, single key light, deep falloff" },
  { id: "neon", category: "lighting", label: "霓虹", text: "neon signage, wet reflective ground, night" },
  { id: "backlit", category: "lighting", label: "逆光", text: "backlit, silhouette, glowing edges" },

  // Composition
  { id: "wide", category: "composition", label: "广角", text: "wide establishing shot, 24mm" },
  { id: "closeup", category: "composition", label: "特写", text: "tight close-up, shallow depth of field, 85mm" },
  { id: "topdown", category: "composition", label: "俯视", text: "top-down flat lay, centred" },
  { id: "rule-of-thirds", category: "composition", label: "三分构图", text: "rule of thirds, subject off-centre" },
  { id: "symmetry", category: "composition", label: "对称", text: "perfectly symmetrical, centred composition" },

  // Medium
  { id: "watercolour", category: "medium", label: "水彩", text: "watercolour on cold-press paper, visible bleed" },
  { id: "oil", category: "medium", label: "油画", text: "oil on canvas, visible brushwork, impasto" },
  { id: "ink", category: "medium", label: "水墨", text: "ink wash painting, monochrome, rice paper" },
  { id: "3d-render", category: "medium", label: "三维渲染", text: "3D render, soft global illumination, subsurface scattering" },
  { id: "pencil", category: "medium", label: "铅笔", text: "graphite pencil sketch, cross-hatching" },

  // Quality
  { id: "detailed", category: "quality", label: "高细节", text: "highly detailed, sharp focus" },
  { id: "muted", category: "quality", label: "低饱和", text: "muted desaturated palette" },
  { id: "high-contrast", category: "quality", label: "高对比", text: "high contrast, deep blacks" },
  { id: "pastel", category: "quality", label: "柔和", text: "soft pastel tones, gentle gradients" },
];

/**
 * Appends a fragment to existing prompt text.
 *
 * Comma-joined because that is the separator image models are trained to read as "and also".
 * Re-selecting an entry is a no-op rather than a duplicate — the picker stays open for
 * multi-select, so double-clicking is easy and should be harmless.
 */
export function appendPrompt(current: string, addition: string): string {
  const trimmed = current.trim();
  if (!trimmed) return addition;

  const existing = trimmed
    .split(",")
    .map((part) => part.trim().toLowerCase())
    .filter(Boolean);

  if (existing.includes(addition.trim().toLowerCase())) {
    return trimmed;
  }

  return `${trimmed}, ${addition}`;
}

export function promptsByCategory(): Array<[PromptCategory, PromptEntry[]]> {
  const order: PromptCategory[] = ["style", "lighting", "composition", "medium", "quality"];

  return order.map((category) => [
    category,
    PROMPT_LIBRARY.filter((entry) => entry.category === category),
  ]);
}
