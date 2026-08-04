import type { ProviderType } from "@/features/ai/types";

/**
 * What the backend can actually do, per protocol.
 *
 * Single source of truth for the "not supported yet" markers. The settings UI lets a user
 * configure any protocol, but only some have a client behind them — without this, someone could
 * add a provider, enable its models, and only discover at generation time that nothing can
 * call it.
 *
 * Every protocol currently has a client. The mechanism is kept because the list will grow
 * again: flip an entry to `"planned"` when a protocol is added ahead of its client, and the UI
 * marks it with no further changes.
 */
export type SupportLevel = "ready" | "planned";

export const PROTOCOL_SUPPORT: Record<ProviderType, SupportLevel> = {
  // Image generation.
  "openai-images": "ready",

  // Text and image both — Gemini serves them from one endpoint.
  gemini: "ready",

  // Video generation.
  "seedance-video": "ready",
  "openai-video": "ready",
  "xai-video": "ready",

  // Text generation.
  "openai-chat": "ready",
  "openai-responses": "ready",
  anthropic: "ready",
};

export function isProtocolReady(protocol: ProviderType): boolean {
  return PROTOCOL_SUPPORT[protocol] === "ready";
}

/**
 * Whether MCP servers do anything at runtime.
 *
 * Configuration is stored and returned faithfully, but nothing connects to a server or invokes
 * its tools. Kept here rather than inline so the MCP panel's banner disappears from one place
 * once a client exists.
 */
export const MCP_RUNTIME_SUPPORTED = false;
