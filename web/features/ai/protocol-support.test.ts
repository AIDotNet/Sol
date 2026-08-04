import { describe, expect, it } from "vitest";
import { isProtocolReady, PROTOCOL_SUPPORT } from "@/features/ai/protocol-support";
import { PROTOCOLS_BY_CATEGORY, PROVIDER_TYPES, resolveProtocol } from "@/features/ai/types";

describe("protocol support matrix", () => {
  it("covers every declared protocol", () => {
    // A protocol added without an entry would read as `undefined` and silently count as
    // unsupported — or worse, as supported, depending on how it is consumed.
    for (const protocol of PROVIDER_TYPES) {
      expect(PROTOCOL_SUPPORT[protocol]).toBeDefined();
    }
  });

  it("marks every protocol a category can resolve to", () => {
    // Every protocol a node picker can produce must have a support answer, otherwise a user
    // could select a model that dispatches to nothing.
    for (const protocols of Object.values(PROTOCOLS_BY_CATEGORY)) {
      for (const protocol of protocols) {
        expect(PROTOCOL_SUPPORT[protocol]).toBeDefined();
      }
    }
  });

  it("reports readiness for a protocol that has a client", () => {
    expect(isProtocolReady("openai-images")).toBe(true);
    expect(isProtocolReady("anthropic")).toBe(true);
  });

  it("agrees with the protocol a model resolves to", () => {
    // The marker is computed per model from the resolved protocol, not the provider's, so an
    // override must be what decides.
    const provider = { type: "openai-chat" } as const;

    expect(resolveProtocol(provider, { type: null, category: "image" })).toBe("openai-images");
    expect(resolveProtocol(provider, { type: "anthropic", category: "chat" })).toBe("anthropic");

    expect(isProtocolReady(resolveProtocol(provider, { type: null, category: "image" }))).toBe(
      true,
    );
  });

  it("only uses known support levels", () => {
    for (const level of Object.values(PROTOCOL_SUPPORT)) {
      expect(["ready", "planned"]).toContain(level);
    }
  });
});
