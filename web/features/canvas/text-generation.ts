"use client";

import { resolveSlot, useAiStore } from "@/features/ai/store";
import { useCanvasStore } from "@/features/canvas/store";
import { collectUpstream } from "@/features/canvas/upstream";
import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";

/**
 * Text generation for the canvas.
 *
 * Two operations, both writing into a text node:
 *  - **write** turns the upstream context into a fresh image prompt;
 *  - **rewrite** improves the node's own text in place.
 *
 * Both use the configured chat model role, so the user picks a model once in settings rather
 * than on every node. Reference images are passed through when present, which is what lets a
 * prompt be written from a picture.
 */

const WRITE_SYSTEM_PROMPT = [
  "You write prompts for image generation models.",
  "Turn the user's material into one vivid, concrete prompt.",
  "Describe subject, composition, lighting and style. Prefer concrete nouns over adjectives.",
  "Reply with the prompt only — no preamble, no quotes, no explanation.",
].join(" ");

const REWRITE_SYSTEM_PROMPT = [
  "You improve prompts for image generation models.",
  "Keep the author's intent and subject; add concrete visual detail where it is vague.",
  "Match the language of the input.",
  "Reply with the improved prompt only — no preamble, no quotes, no explanation.",
].join(" ");

export type TextTask = "write" | "rewrite";

export interface TextGenerationOutcome {
  ok: boolean;
  text?: string;
  error?: string;
}

/**
 * Runs a text task against the configured chat model.
 *
 * Returns rather than mutating the graph: the text node owns its own busy state and decides
 * what to do with the result, which keeps this usable for a preview later.
 */
export async function generateText(
  nodeId: string,
  task: TextTask,
  nodes: CanvasNode[],
  edges: CanvasEdge[],
  signal?: AbortSignal,
): Promise<TextGenerationOutcome> {
  const { providers, slots } = useAiStore.getState();

  // The fast model is the better fit for a rewrite; fall back to the main chat slot.
  const slot = task === "rewrite" ? (slots.fast ?? slots.chat) : slots.chat;
  const resolved = resolveSlot(providers, slot) ?? resolveSlot(providers, slots.chat);

  if (!resolved) {
    return { ok: false, error: "no-chat-model" };
  }

  const node = nodes.find((candidate) => candidate.id === nodeId);
  const ownText = ((node?.data as { text?: string } | undefined)?.text ?? "").trim();
  const upstream = collectUpstream(nodeId, nodes, edges);

  const prompt =
    task === "rewrite"
      ? ownText
      : [upstream.prompt, ownText].filter(Boolean).join("\n\n");

  if (!prompt && upstream.images.length === 0) {
    return { ok: false, error: "empty" };
  }

  try {
    const response = await fetch("/api/v1/ai/text", {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      signal,
      body: JSON.stringify({
        providerId: resolved.provider.id,
        modelKey: resolved.model.modelKey,
        prompt: prompt || "Write an image prompt describing the attached image.",
        systemPrompt: task === "rewrite" ? REWRITE_SYSTEM_PROMPT : WRITE_SYSTEM_PROMPT,
        images: upstream.images.map((image) => image.url),
        temperature: 0.8,
      }),
    });

    if (!response.ok) {
      let detail = `Request failed (${response.status})`;
      try {
        const body = (await response.json()) as { error?: string; details?: string[] };
        detail = body.details?.[0] ?? body.error ?? detail;
      } catch {
        // Keep the status-derived message.
      }
      return { ok: false, error: detail };
    }

    const { text } = (await response.json()) as { text: string };
    return { ok: true, text: text.trim() };
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") {
      return { ok: false, error: "aborted" };
    }
    return { ok: false, error: error instanceof Error ? error.message : "failed" };
  }
}

/** Writes a generated prompt into a new text node to the right of its source. */
export function spawnTextResult(sourceId: string, text: string): string | null {
  return useCanvasStore.getState().spawnOutput(sourceId, "text", { text });
}
