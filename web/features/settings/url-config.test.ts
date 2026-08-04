import { describe, expect, it } from "vitest";
import {
  buildShareLink,
  maskApiKey,
  parseQuickConfig,
  parseQuickConfigFromLocation,
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
    // Models default to an empty list rather than being required.
    expect(result.config.providers[0].models).toEqual([]);
  });

  it("accepts base64 payloads carrying non-ASCII names", () => {
    const json = JSON.stringify({ providers: [{ name: "火山方舟", apiKey: "k" }] });
    const bytes = new TextEncoder().encode(json);
    const base64 = btoa(String.fromCharCode(...bytes));

    const result = parseQuickConfig(new URLSearchParams({ settings: `base64:${base64}` }));

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].name).toBe("火山方舟");
  });

  it("rejects an unknown protocol rather than passing it through", () => {
    const result = parseQuickConfig(
      query({ providers: [{ builtinId: "openai", type: "evil-protocol" }] }),
    );
    expect(result).toMatchObject({ ok: false, reason: "malformed" });
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
    expect(result.config.providers[0].models[0]).toMatchObject({
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
    expect(payload.name).toBe("openai");
  });
});

describe("maskApiKey", () => {
  it("never reveals the middle of a key", () => {
    expect(maskApiKey("sk-1234567890abcdef")).toBe("sk-••••cdef");
    expect(maskApiKey("short")).toBe("••••");
    expect(maskApiKey(undefined)).toBe("");
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

    expect(link).toContain("#settings=base64:");
    expect(link.split("#")[0]).not.toContain("settings");
    // The raw key must not appear before the fragment, which is the part sent to the server.
    expect(link.split("#")[0]).not.toContain("sk-share-me");
  });

  it("round-trips through the parser", () => {
    const link = buildShareLink("https://sol.test", "/canvas", {
      providers: [{ name: "火山方舟", apiKey: "sk-1", models: [] }],
    });

    const hash = new URL(link).hash;
    const result = parseQuickConfigFromLocation({ search: "", hash });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.config.providers[0].name).toBe("火山方舟");
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
