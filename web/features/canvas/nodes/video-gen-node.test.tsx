import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";

vi.mock("@xyflow/react", () => ({
  NodeResizer: () => null,
  Handle: () => null,
  Position: { Left: "left", Right: "right" },
  useReactFlow: () => ({ getNodes: () => [], getEdges: () => [] }),
}));

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => (key: string) => key,
}));

vi.mock("@/features/canvas/nodes/model-picker", () => ({
  ChipRow: () => null,
  ModelPicker: () => null,
  useSelection: () => ({ protocol: "seedance-video", provider: {}, model: {} }),
}));

const { useCanvasStore } = await import("@/features/canvas/store");
const { VideoGenNode } = await import("@/features/canvas/nodes/video-gen-node");

function nodeProps(duration?: number) {
  return {
    id: "g",
    data: { providerId: "p1", modelId: "m1", duration },
    type: "videoGen",
    selected: false,
    isConnectable: true,
    dragging: false,
    draggable: true,
    selectable: true,
    deletable: true,
    zIndex: 0,
    positionAbsoluteX: 0,
    positionAbsoluteY: 0,
  };
}

function renderNode(duration?: number) {
  useCanvasStore.getState().load({
    nodes: [
      {
        id: "g",
        type: "videoGen",
        position: { x: 0, y: 0 },
        data: { providerId: "p1", modelId: "m1", duration },
      },
    ],
    edges: [],
  });

  const view = render(<VideoGenNode {...nodeProps(duration)} />);
  const input = screen.getByRole("spinbutton", {
    name: "node.durationSeconds",
  }) as HTMLInputElement;
  const generate = screen.getByRole("button", { name: "node.generate" });

  return { view, input, generate };
}

function storedDuration(): number | undefined {
  const node = useCanvasStore.getState().nodes.find((candidate) => candidate.id === "g");
  return (node?.data as { duration?: number }).duration;
}

function storedInputMode(): string | undefined {
  const node = useCanvasStore.getState().nodes.find((candidate) => candidate.id === "g");
  return (node?.data as { seedanceInputMode?: string }).seedanceInputMode;
}

beforeEach(() => {
  useCanvasStore.getState().reset();
});

describe("VideoGenNode duration", () => {
  it("shows the five-second default and the allowed integer range", () => {
    const { input, generate } = renderNode();

    expect(input.value).toBe("5");
    expect(input.min).toBe("1");
    expect(input.max).toBe("60");
    expect(input.step).toBe("1");
    expect(generate).toBeEnabled();
  });

  it("commits an arbitrary whole number in range", () => {
    const { input } = renderNode();

    fireEvent.change(input, { target: { value: "37" } });

    expect(input.value).toBe("37");
    expect(storedDuration()).toBe(37);
  });

  it("keeps a temporary blank draft without replacing the last valid value", () => {
    const { input, generate } = renderNode(10);

    fireEvent.change(input, { target: { value: "" } });

    expect(input.value).toBe("");
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(storedDuration()).toBe(10);
    expect(generate).toBeDisabled();
  });

  it.each(["0", "61", "3.5"])("rejects invalid duration %s", (value) => {
    const { input, generate } = renderNode(5);

    fireEvent.change(input, { target: { value } });

    expect(input.value).toBe(value);
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(storedDuration()).toBe(5);
    expect(generate).toBeDisabled();
  });

  it("syncs when undo or loading changes the stored value", () => {
    const { view, input } = renderNode(3);
    expect(input.value).toBe("3");

    view.rerender(<VideoGenNode {...nodeProps(10)} />);

    expect(input.value).toBe("10");
  });
});

describe("Seedance video input mode", () => {
  it("defaults to reference media and switches to first/last frames", () => {
    const { view } = renderNode();

    const reference = screen.getByRole("button", { name: "node.videoInputReference" });
    const firstLast = screen.getByRole("button", { name: "node.videoInputFirstLast" });

    expect(reference).toHaveAttribute("aria-pressed", "true");
    expect(firstLast).toHaveAttribute("aria-pressed", "false");

    fireEvent.click(firstLast);
    const stored = useCanvasStore.getState().nodes.find((node) => node.id === "g");
    view.rerender(<VideoGenNode {...nodeProps()} data={stored?.data ?? {}} />);

    expect(screen.getByRole("button", { name: "node.videoInputFirstLast" }))
      .toHaveAttribute("aria-pressed", "true");
    expect(storedInputMode()).toBe("first-last");
  });
});
