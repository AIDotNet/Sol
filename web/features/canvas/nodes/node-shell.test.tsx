import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { NodeExecution } from "@/features/canvas/types";

vi.mock("@xyflow/react", () => ({
  Handle: () => null,
  Position: { Left: "left", Right: "right" },
  useNodeId: () => null,
}));

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => (key: string) => key,
}));

const { NodeShell } = await import("@/features/canvas/nodes/node-shell");

function renderShell(progress?: number) {
  const execution: NodeExecution = { runId: "run-1", status: "running", progress };

  return render(
    <NodeShell title="Video" execution={execution}>
      <div>Video content</div>
    </NodeShell>,
  );
}

describe("NodeShell generation progress", () => {
  it("shows the reported percentage and exposes matching progress semantics", () => {
    renderShell(0.42);

    expect(screen.getByText("42%")).toBeInTheDocument();

    const progressbar = screen.getByRole("progressbar", { name: "node.generating" });
    expect(progressbar).toHaveAttribute("aria-valuemin", "0");
    expect(progressbar).toHaveAttribute("aria-valuemax", "100");
    expect(progressbar).toHaveAttribute("aria-valuenow", "42");
    expect(progressbar.firstElementChild).toHaveStyle({ width: "42%" });
  });

  it("keeps an indeterminate state when no real progress was reported", () => {
    renderShell();

    expect(screen.getByText("node.generating")).toBeInTheDocument();
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
    expect(screen.queryByText(/%$/)).not.toBeInTheDocument();
  });

  it.each([
    [-0.2, 0],
    [1.4, 100],
  ])("clamps a progress value of %s to %s%%", (value, expected) => {
    renderShell(value);

    const progressbar = screen.getByRole("progressbar");
    expect(screen.getByText(`${expected}%`)).toBeInTheDocument();
    expect(progressbar).toHaveAttribute("aria-valuenow", String(expected));
    expect(progressbar.firstElementChild).toHaveStyle({ width: `${expected}%` });
  });

  it.each([Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY])(
    "ignores a non-finite progress value of %s",
    (value) => {
      renderShell(value);

      expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
      expect(screen.queryByText(/%$/)).not.toBeInTheDocument();
    },
  );
});
