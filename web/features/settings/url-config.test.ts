import { describe, expect, it } from "vitest";
import {
  buildShareLink,
  maskApiKey,
  parseQuickConfig,
  parseQuickConfigFromLocation,
  resolveQuickConfig,
  toImportPayload,
  urlWithoutQuickConfig,
} from "@/features/settings/url-config";

function query(value: unknown): string {
  return new URLSearchParams({ settings: JSON.stringify(value) }).toString();
}

describe("parseQuickConfig", () => {
  it("reports absence distinctly from malformed input", () => {
    expect(parseQuickConfig("")).toEqual({ ok: false, reason: "absent" });
    expect(parseQuickConfig("other=1")).toEqual({ ok: false, reason: "absent" });
    expect(parseQuickConfig("settings=")).toEqual({ ok: false, reason: "absent" });

    // Present but unusable — the caller shows an error rather than staying silent.
    const result = parseQuickConfig("settings=not-json");
    expect(result).toMatchObject({ ok: false, reason: "malformed" });
  });

  it("accepts the LobeChat-style raw JSON shape", () => {
    const result = parseQuickConfig(
      query({
        providers: [
          { builtinId: "openai", apiKey: "sk-test-key", baseUrl: "https://proxy.example.com/v1" },
        ],
      }),
    );

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0]).toMatchObject({
      builtinId: "openai",
      apiKey: "sk-test-key",
    });
    // Models are optional in the URL; the resolver supplies built-in defaults later.
    expect(result.config.providers[0].models).toBeUndefined();
  });

  it("accepts base64 payloads carrying non-ASCII names", () => {
    const json = JSON.stringify({
      providers: [
        {
          name: "火山方舟",
          apiKey: "k",
          type: "openai-chat",
          baseUrl: "https://api.example.com/v1",
        },
      ],
    });
    const bytes = new TextEncoder().encode(json);
    const base64 = btoa(String.fromCharCode(...bytes));

    const result = parseQuickConfig(new URLSearchParams({ settings: `base64:${base64}` }));

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].name).toBe("火山方舟");
  });

  it("repairs plus signs from an unescaped legacy standard-base64 query", () => {
    // `+` becomes a space when URLSearchParams parses an old, unescaped query parameter.
    const legacy =
      "eyJwcm92aWRlcnMiOlt7Im5hbWUiOiJ+IiwidHlwZSI6Im9wZW5haS1jaGF0IiwiYmFzZVVybCI6Imh0dHBzOi8vcmVsYXkuZXhhbXBsZS5jb20vdjEiLCJhcGlLZXkiOiJrIn1dfQ==";
    const result = parseQuickConfig(`?settings=base64:${legacy}`);

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].name).toBe("~");
  });

  it("rejects an unknown protocol rather than passing it through", () => {
    const result = parseQuickConfig(
      query({ providers: [{ builtinId: "openai", type: "evil-protocol" }] }),
    );
    expect(result).toMatchObject({ ok: false, reason: "malformed" });
  });

  it("rejects an unknown built-in provider id", () => {
    const result = parseQuickConfig(query({ providers: [{ builtinId: "not-a-preset" }] }));
    expect(result).toMatchObject({ ok: false, reason: "malformed" });
  });

  it("requires the functional fields for a custom provider", () => {
    const result = parseQuickConfig(query({ providers: [{ name: "relay" }] }));
    expect(result).toMatchObject({ ok: false, reason: "malformed" });
  });

  it("rejects unsafe or malformed base URLs", () => {
    expect(
      parseQuickConfig(
        query({
          providers: [
            {
              name: "relay",
              type: "openai-chat",
              baseUrl: "https://user:password@example.com/v1?token=secret",
            },
          ],
        }),
      ),
    ).toMatchObject({ ok: false, reason: "malformed" });
  });

  it("rejects a provider that has neither builtinId nor name", () => {
    const result = parseQuickConfig(query({ providers: [{ apiKey: "sk-orphan" }] }));
    expect(result).toMatchObject({ ok: false, reason: "malformed" });
  });

  it("rejects payloads that would flood the device with providers", () => {
    const providers = Array.from({ length: 51 }, (_, i) => ({ name: `p${i}` }));
    expect(parseQuickConfig(query({ providers }))).toMatchObject({ ok: false });

    expect(parseQuickConfig(query({ providers: [] }))).toMatchObject({ ok: false });
  });

  it("keeps a model's protocol override and category", () => {
    const result = parseQuickConfig(
      query({
        providers: [
          {
            builtinId: "openai",
            models: [{ id: "gpt-image-1", category: "image", type: "openai-images" }],
          },
        ],
      }),
    );

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].models?.[0]).toMatchObject({
      id: "gpt-image-1",
      category: "image",
      type: "openai-images",
    });
  });
});

describe("toImportPayload", () => {
  it("normalizes an Anthropic base URL so the protocol client can append its own path", () => {
    const payload = toImportPayload(
      { name: "c", baseUrl: "https://api.anthropic.com/v1", type: "anthropic", models: [] },
      "openai-chat",
    );
    expect(payload.baseUrl).toBe("https://api.anthropic.com");
  });

  it("falls back to the preset protocol when the payload omits one", () => {
    const payload = toImportPayload({ builtinId: "openai", models: [] }, "openai-chat");
    expect(payload.type).toBe("openai-chat");
    expect(payload.name).toBe("OpenAI");
  });

  it("uses the caller fallback protocol for legacy custom entries", () => {
    const payload = toImportPayload(
      { name: "legacy relay", baseUrl: "https://relay.example.com/v1", models: [] },
      "anthropic",
    );

    expect(payload.type).toBe("anthropic");
    expect(payload.baseUrl).toBe("https://relay.example.com");
  });

  it("expands a minimal built-in entry with preset defaults", () => {
    const result = resolveQuickConfig({ providers: [{ builtinId: "openai", apiKey: "sk-test" }] });

    expect(result.providers[0]).toMatchObject({
      builtinId: "openai",
      name: "OpenAI",
      type: "openai-chat",
      baseUrl: "https://api.openai.com/v1",
      apiKey: "sk-test",
    });
    expect(result.providers[0].models.length).toBeGreaterThan(0);
    expect(result.requiresConfirmation).toBe(true);
  });

  it("allows a custom provider without models", () => {
    const result = resolveQuickConfig({
      providers: [
        {
          name: "Relay",
          type: "openai-chat",
          baseUrl: "https://relay.example.com/v1",
        },
      ],
    });

    expect(result.providers[0].models).toEqual([]);
    expect(result.requiresConfirmation).toBe(false);
  });

  it("preserves an existing built-in proxy when a minimal link only supplies a key", () => {
    const result = resolveQuickConfig(
      { providers: [{ builtinId: "openai", apiKey: "sk-new" }] },
      [
        {
          id: "provider-1",
          builtinId: "openai",
          name: "OpenAI",
          type: "openai-chat",
          baseUrl: "https://proxy.example.com/v1",
          enabled: true,
          hasApiKey: true,
          models: [{
            id: "model-1",
            modelKey: "gpt-4o",
            name: "GPT-4o",
            enabled: true,
            category: "chat",
          }],
          sortOrder: 0,
          createdAt: "",
          updatedAt: "",
        },
      ],
    );

    expect(result.providers[0].baseUrl).toBe("https://proxy.example.com/v1");
    expect(result.providers[0].models.length).toBeGreaterThan(0);
  });
});

describe("maskApiKey", () => {
  it("never reveals the middle of a key", () => {
    expect(maskApiKey("sk-1234567890abcdef")).toBe("sk-••••cdef");
    expect(maskApiKey("short")).toBe("••••");
    expect(maskApiKey(undefined)).toBe("");
  });
});

describe("urlWithoutQuickConfig", () => {
  it("removes the config from both query and hash while preserving other parameters", () => {
    expect(urlWithoutQuickConfig("https://sol.example/settings?tab=ai&settings=query#view=all&settings=hash"))
      .toBe("/settings?tab=ai#view=all");
  });
});

describe("parseQuickConfigFromLocation", () => {
  const payload = JSON.stringify({ providers: [{ builtinId: "openai", apiKey: "sk-hash" }] });

  it("reads the config from the hash fragment", () => {
    const result = parseQuickConfigFromLocation({
      search: "",
      hash: `#settings=${encodeURIComponent(payload)}`,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].apiKey).toBe("sk-hash");
  });

  it("prefers the hash over the query string", () => {
    // The hash never reaches the server, so when both are present it wins.
    const query = JSON.stringify({ providers: [{ builtinId: "openai", apiKey: "sk-query" }] });

    const result = parseQuickConfigFromLocation({
      search: `?settings=${encodeURIComponent(query)}`,
      hash: `#settings=${encodeURIComponent(payload)}`,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].apiKey).toBe("sk-hash");
  });

  it("still accepts a query-only link for LobeChat compatibility", () => {
    const result = parseQuickConfigFromLocation({
      search: `?settings=${encodeURIComponent(payload)}`,
      hash: "",
    });

    expect(result.ok).toBe(true);
  });

  it("reports absence when neither carries a config", () => {
    expect(parseQuickConfigFromLocation({ search: "?a=1", hash: "#b=2" })).toEqual({
      ok: false,
      reason: "absent",
    });
  });
});

describe("buildShareLink", () => {
  it("puts the payload in the fragment, not the query", () => {
    const link = buildShareLink("https://sol.test", "/canvas", {
      providers: [{ builtinId: "openai", apiKey: "sk-share-me", models: [] }],
    });

    expect(link).toContain("#settings=base64url:");
    expect(link.split("#")[0]).not.toContain("settings");
    // The raw key must not appear before the fragment, which is the part sent to the server.
    expect(link.split("#")[0]).not.toContain("sk-share-me");
  });

  it("round-trips through the parser", () => {
    const link = buildShareLink("https://sol.test", "/canvas", {
      providers: [
        {
          name: "火山方舟",
          apiKey: "sk-1",
          type: "openai-chat",
          baseUrl: "https://api.example.com/v1",
          models: [],
        },
      ],
    });

    const hash = new URL(link).hash;
    const result = parseQuickConfigFromLocation({ search: "", hash });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].name).toBe("火山方舟");
  });

  it("supports autoApply only as a payload property; keys still require confirmation", () => {
    const noKey = resolveQuickConfig({ autoApply: true, providers: [{ builtinId: "openai" }] });
    const withKey = resolveQuickConfig({
      autoApply: true,
      providers: [{ builtinId: "openai", apiKey: "sk-secret" }],
    });

    expect(noKey.autoApply).toBe(true);
    expect(noKey.requiresConfirmation).toBe(false);
    expect(withKey.autoApply).toBe(true);
    expect(withKey.requiresConfirmation).toBe(true);
  });
});

describe("urlWithoutQuickConfig", () => {
  it("removes only the settings parameter", () => {
    expect(urlWithoutQuickConfig("https://sol.test/canvas?settings=%7B%7D&keep=1")).toBe(
      "/canvas?keep=1",
    );
    expect(urlWithoutQuickConfig("https://sol.test/canvas?settings=%7B%7D")).toBe("/canvas");
  });

  it("clears the hash form too", () => {
    expect(urlWithoutQuickConfig("https://sol.test/canvas#settings=base64:abc")).toBe("/canvas");
    expect(urlWithoutQuickConfig("https://sol.test/canvas#settings=x&tab=providers")).toBe(
      "/canvas#tab=providers",
    );
  });
});
