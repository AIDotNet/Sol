import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => (key: string, values?: Record<string, string | number>) =>
    values
      ? key.replace(/\{(\w+)\}/g, (_, name: string) => String(values[name] ?? `{${name}}`))
      : key,
}));

vi.mock("@/components/ui/dialog", () => ({
  Dialog: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  DialogDescription: ({ children }: { children: ReactNode }) => <p>{children}</p>,
  DialogFooter: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  DialogHeader: ({ children }: { children: ReactNode }) => <div>{children}</div>,
  DialogTitle: ({ children }: { children: ReactNode }) => <h2>{children}</h2>,
}));

vi.mock("@/components/ui/switch", () => ({
  Switch: ({
    children,
    isDisabled,
    isSelected,
    onChange,
  }: {
    children: ReactNode;
    isDisabled?: boolean;
    isSelected: boolean;
    onChange: (selected: boolean) => void;
  }) => (
    <button
      type="button"
      role="switch"
      aria-checked={isSelected}
      disabled={isDisabled}
      onClick={() => onChange(!isSelected)}
    >
      {children}
    </button>
  ),
}));

class FakeImage {
  naturalWidth = 120;
  naturalHeight = 80;
  crossOrigin = "";
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;

  set src(_value: string) {
    queueMicrotask(() => this.onload?.());
  }
}

const { ResizeImageDialog } = await import("@/features/canvas/nodes/resize-image-dialog");

beforeEach(() => {
  vi.stubGlobal("Image", FakeImage);
});

describe("ResizeImageDialog", () => {
  it("loads the original size and lets users set independent dimensions", async () => {
    const onResize = vi.fn().mockResolvedValue(true);
    const onOpenChange = vi.fn();

    render(
      <ResizeImageDialog
        sourceUrl="/api/v1/canvas/assets/image"
        error={null}
        onResize={onResize}
        onOpenChange={onOpenChange}
      />,
    );

    const width = await screen.findByRole("spinbutton", { name: "node.width" });
    const height = screen.getByRole("spinbutton", { name: "node.height" });
    await waitFor(() => {
      expect(width).toHaveValue(120);
      expect(height).toHaveValue(80);
    });

    fireEvent.change(width, { target: { value: "240" } });
    expect(height).toHaveValue(160);

    fireEvent.click(screen.getByRole("switch"));
    fireEvent.change(height, { target: { value: "300" } });
    expect(width).toHaveValue(240);

    fireEvent.click(screen.getByRole("button", { name: "node.applyResize" }));
    await waitFor(() => expect(onResize).toHaveBeenCalledWith(240, 300));
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });
});
