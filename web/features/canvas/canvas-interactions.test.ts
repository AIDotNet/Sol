import { afterEach, describe, expect, it, vi } from "vitest";
import { copyImageToClipboard } from "@/features/canvas/canvas-interactions";

class TestClipboardItem {
  constructor(readonly representations: Record<string, Blob>) {}
}

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("copyImageToClipboard", () => {
  it("fetches the authenticated asset and writes its PNG bytes to the clipboard", async () => {
    const image = new Blob(["png-bytes"], { type: "image/png" });
    const response = {
      ok: true,
      blob: vi.fn().mockResolvedValue(image),
    } as unknown as Response;
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(response);
    const write = vi.fn().mockResolvedValue(undefined);

    vi.stubGlobal("ClipboardItem", TestClipboardItem);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { write },
    });

    await copyImageToClipboard("/api/v1/canvas/assets/image", "image/png");

    expect(fetchMock).toHaveBeenCalledWith("/api/v1/canvas/assets/image", {
      credentials: "include",
    });
    expect(write).toHaveBeenCalledTimes(1);

    const [item] = write.mock.calls[0][0] as [TestClipboardItem];
    expect(item.representations["image/png"]).toBe(image);
  });

  it("rejects when the asset cannot be loaded", async () => {
    const response = {
      ok: false,
      blob: vi.fn(),
    } as unknown as Response;
    vi.spyOn(globalThis, "fetch").mockResolvedValue(response);
    const write = vi.fn();

    vi.stubGlobal("ClipboardItem", TestClipboardItem);
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { write },
    });

    await expect(copyImageToClipboard("/missing.png")).rejects.toThrow(
      "The image could not be loaded.",
    );
    expect(write).not.toHaveBeenCalled();
  });
});
