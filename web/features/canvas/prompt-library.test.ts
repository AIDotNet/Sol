import { describe, expect, it } from "vitest";
import {
  appendPrompt,
  PROMPT_CATEGORY_LABELS,
  PROMPT_LIBRARY,
  promptsByCategory,
} from "@/features/canvas/prompt-library";

describe("appendPrompt", () => {
  it("uses the fragment alone when there is nothing yet", () => {
    expect(appendPrompt("", "golden hour")).toBe("golden hour");
    expect(appendPrompt("   ", "golden hour")).toBe("golden hour");
  });

  it("comma-joins onto existing text", () => {
    // Comma is what image models read as "and also".
    expect(appendPrompt("a red barn", "golden hour")).toBe("a red barn, golden hour");
  });

  it("does not duplicate a fragment already present", () => {
    // The picker stays open for multi-select, so a double click has to be harmless.
    const once = appendPrompt("a red barn", "golden hour");
    expect(appendPrompt(once, "golden hour")).toBe(once);
  });

  it("matches an existing fragment regardless of case or padding", () => {
    expect(appendPrompt("a barn,  GOLDEN HOUR ", "golden hour")).toBe("a barn,  GOLDEN HOUR");
  });

  it("still appends a fragment that merely contains an existing one", () => {
    // Substring is not equality: "wide shot" must not suppress "wide establishing shot".
    const result = appendPrompt("wide shot", "wide establishing shot, 24mm");
    expect(result).toBe("wide shot, wide establishing shot, 24mm");
  });

  it("trims surrounding whitespace from the base text", () => {
    expect(appendPrompt("  a barn  ", "neon")).toBe("a barn, neon");
  });
});

describe("prompt library data", () => {
  it("has unique ids", () => {
    const ids = PROMPT_LIBRARY.map((entry) => entry.id);
    expect(ids.length).toBe(new Set(ids).size);
  });

  it("has unique insert text, so selections cannot collide", () => {
    const texts = PROMPT_LIBRARY.map((entry) => entry.text);
    expect(texts.length).toBe(new Set(texts).size);
  });

  it("labels every category it uses", () => {
    for (const entry of PROMPT_LIBRARY) {
      expect(PROMPT_CATEGORY_LABELS[entry.category]).toBeDefined();
    }
  });

  it("groups every entry exactly once", () => {
    const grouped = promptsByCategory().flatMap(([, entries]) => entries);
    expect(grouped).toHaveLength(PROMPT_LIBRARY.length);
  });

  it("inserts English text, which is what models are trained on", () => {
    // Labels are localised; the inserted fragment is not.
    for (const entry of PROMPT_LIBRARY) {
      expect(entry.text).toMatch(/^[\x20-\x7E]+$/);
    }
  });
});
