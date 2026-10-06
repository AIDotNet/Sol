import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen } from "@testing-library/react";

vi.mock("@xyflow/react", () => ({
  NodeResizer: () => null,
  Handle: () => null,
  Position: { Left: "left", Right: "right" },
  useReactFlow: () => ({ getNodes: () => [], getEdges: () => [] }),
  useNodeId: () => null,
}));

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => (key: string) => key,
}));

// No chat model configured, so the write/rewrite actions stay out of the tree.
vi.mock("@/features/ai/store", () => ({
  useAiStore: (select: (state: unknown) => unknown) =>
    select({ providers: [], slots: { chat: null, fast: null } }),
  resolveSlot: () => null,
}));

const { useCanvasStore } = await import("@/features/canvas/store");
const { TextNode } = await import("@/features/canvas/nodes/text-node");

function storedText(id: string): string | undefined {
  const node = useCanvasStore.getState().nodes.find((candidate) => candidate.id === id);
  return (node?.data as { text?: string }).text;
}

/** React Flow hands node components a large prop bag; only `id` and `data` matter here. */
function nodeProps(text: string) {
  return {
    id: "t",
    data: { text },
    type: "text",
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

function renderNode(text = "") {
  useCanvasStore.getState().load({
    nodes: [{ id: "t", type: "text", position: { x: 0, y: 0 }, data: { text } }],
    edges: [],
  });

  const view = render(<TextNode {...nodeProps(text)} />);

  return { view, textarea: screen.getByRole("textbox") as HTMLTextAreaElement };
}

beforeEach(() => {
  useCanvasStore.getState().reset();
});

describe("TextNode composition input", () => {
  it("does not commit the intermediate letters of an IME composition", () => {
    // Regression: the textarea took its value straight from the store, so React re-applied it to
    // the element mid-composition and destroyed the composing buffer — typing pinyin put the
    // latin letters on screen instead of the characters they were composing into.
    const { textarea } = renderNode();

    fireEvent.compositionStart(textarea);
    fireEvent.change(textarea, { target: { value: "ni" } });
    fireEvent.change(textarea, { target: { value: "nihao" } });

    // On screen the user sees their pinyin, but nothing has been committed to the document yet.
    expect(textarea.value).toBe("nihao");
    expect(storedText("t")).toBe("");

    fireEvent.compositionEnd(textarea, { target: { value: "你好" } });

    expect(storedText("t")).toBe("你好");
  });

  it("keeps one composition to a single undo step", () => {
    const { textarea } = renderNode();
    const baseline = useCanvasStore.getState().past.length;

    fireEvent.compositionStart(textarea);
    fireEvent.change(textarea, { target: { value: "n" } });
    fireEvent.change(textarea, { target: { value: "nihao" } });
    fireEvent.compositionEnd(textarea, { target: { value: "你好" } });

    expect(useCanvasStore.getState().past.length).toBe(baseline + 1);
  });

  it("still commits ordinary typing as it happens", () => {
    const { textarea } = renderNode();

    fireEvent.change(textarea, { target: { value: "plain" } });

    expect(storedText("t")).toBe("plain");
  });

  it("picks up a value changed from outside the node", () => {
    // An AI rewrite, an undo, or a prompt-library insert writes the text for the user.
    const { view, textarea } = renderNode("before");
    expect(textarea.value).toBe("before");

    view.rerender(<TextNode {...nodeProps("after")} />);

    expect((screen.getByRole("textbox") as HTMLTextAreaElement).value).toBe("after");
  });
});
