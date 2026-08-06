import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useCanvasStore } from "@/features/canvas/store";
import type { TextNodeData } from "@/features/canvas/types";

function textOf(id: string): string | undefined {
  const node = useCanvasStore.getState().nodes.find((candidate) => candidate.id === id);
  return (node?.data as TextNodeData).text;
}

function edit(id: string, text: string) {
  useCanvasStore.getState().updateNodeData(id, { text });
}

beforeEach(() => {
  useCanvasStore.getState().reset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("node drag persistence revisions", () => {
  it("applies live positions without marking them persistable", () => {
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 });
    const revision = useCanvasStore.getState().revision;

    useCanvasStore.getState().onNodesChange([
      { id, type: "position", position: { x: 40, y: 24 }, dragging: true },
    ]);
    useCanvasStore.getState().onNodesChange([
      { id, type: "position", position: { x: 80, y: 48 }, dragging: true },
    ]);

    expect(useCanvasStore.getState().nodes.find((node) => node.id === id)?.position).toEqual({
      x: 80,
      y: 48,
    });
    expect(useCanvasStore.getState().revision).toBe(revision);
  });

  it("marks only the released position persistable", () => {
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 });
    const revision = useCanvasStore.getState().revision;

    useCanvasStore.getState().onNodesChange([
      { id, type: "position", position: { x: 40, y: 24 }, dragging: true },
    ]);
    useCanvasStore.getState().onNodesChange([
      { id, type: "position", position: { x: 80, y: 48 }, dragging: false },
    ]);

    expect(useCanvasStore.getState().nodes.find((node) => node.id === id)?.position).toEqual({
      x: 80,
      y: 48,
    });
    expect(useCanvasStore.getState().revision).toBe(revision + 1);
  });

  it("persists a multi-node release as one revision", () => {
    const first = useCanvasStore.getState().addNode("text", { x: 0, y: 0 });
    const second = useCanvasStore.getState().addNode("text", { x: 10, y: 10 });
    const revision = useCanvasStore.getState().revision;

    useCanvasStore.getState().onNodesChange([
      { id: first, type: "position", position: { x: 100, y: 50 }, dragging: false },
      { id: second, type: "position", position: { x: 140, y: 90 }, dragging: false },
    ]);

    const nodes = useCanvasStore.getState().nodes;
    expect(nodes.find((node) => node.id === first)?.position).toEqual({ x: 100, y: 50 });
    expect(nodes.find((node) => node.id === second)?.position).toEqual({ x: 140, y: 90 });
    expect(useCanvasStore.getState().revision).toBe(revision + 1);
  });

  it("keeps non-drag position changes persistable", () => {
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 });
    const revision = useCanvasStore.getState().revision;

    useCanvasStore.getState().onNodesChange([
      { id, type: "position", position: { x: 20, y: 30 } },
    ]);

    expect(useCanvasStore.getState().revision).toBe(revision + 1);
  });
});

describe("undo history for node edits", () => {
  it("restores the text a burst of typing replaced", () => {
    // Regression: `updateNodeData` recorded no history at all, so Ctrl+Z skipped past the typing
    // to the last structural change and discarded the whole paragraph.
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "before" });

    edit(id, "b");
    edit(id, "bl");
    edit(id, "blah");
    expect(textOf(id)).toBe("blah");

    useCanvasStore.getState().undo();

    expect(textOf(id)).toBe("before");
  });

  it("collapses a continuous burst into one undo step", () => {
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "" });
    const baseline = useCanvasStore.getState().past.length;

    edit(id, "o");
    edit(id, "on");
    edit(id, "one");

    // One entry for three keystrokes — otherwise undoing a sentence takes a press per character.
    expect(useCanvasStore.getState().past.length).toBe(baseline + 1);
  });

  it("starts a new undo step after the user pauses", () => {
    vi.useFakeTimers();
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "" });

    edit(id, "first");
    vi.advanceTimersByTime(1_000);
    edit(id, "first second");

    useCanvasStore.getState().undo();

    expect(textOf(id)).toBe("first");
  });

  it("breaks up a burst that runs long, so one undo is not the whole session", () => {
    vi.useFakeTimers();
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "" });

    // Never idle long enough to break the burst on its own, but well past the max duration.
    for (let step = 1; step <= 10; step += 1) {
      edit(id, "x".repeat(step));
      vi.advanceTimersByTime(400);
    }

    useCanvasStore.getState().undo();

    expect(textOf(id)).not.toBe("");
    expect(textOf(id)).not.toBe("xxxxxxxxxx");
  });

  it("keeps edits to different fields as separate steps", () => {
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "" });

    edit(id, "kept");
    useCanvasStore.getState().updateNodeData(id, { fontScale: 1.6 });
    useCanvasStore.getState().undo();

    // Undoing the font change must not take the text with it.
    expect(textOf(id)).toBe("kept");
  });

  it("does not let a background write take an undo slot", () => {
    // A video poller writing back a finished asset is not something the user did, and merging it
    // into the edit they happened to be making would make one Ctrl+Z revert both.
    const id = useCanvasStore.getState().addNode("video", { x: 0, y: 0 }, {});
    const baseline = useCanvasStore.getState().past.length;
    const revision = useCanvasStore.getState().revision;

    useCanvasStore
      .getState()
      .updateNodeData(id, { assetUrl: "/api/v1/canvas/assets/1" }, { history: false });

    expect(useCanvasStore.getState().past.length).toBe(baseline);
    // It must still mark the document dirty, or the finished video is never saved.
    expect(useCanvasStore.getState().revision).toBeGreaterThan(revision);
  });

  it("does not merge an edit made after an undo into the entry that was just popped", () => {
    vi.useFakeTimers();
    const id = useCanvasStore.getState().addNode("text", { x: 0, y: 0 }, { text: "" });

    edit(id, "typed");
    useCanvasStore.getState().undo();
    expect(textOf(id)).toBe("");

    edit(id, "retyped");
    useCanvasStore.getState().undo();

    expect(textOf(id)).toBe("");
  });
});
