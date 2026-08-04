import { beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
// Type-only: the runtime binding comes from the mocked dynamic import below, which cannot be
// used as a type namespace.
import type { CanvasDocument, CanvasSummary } from "@/features/canvas/api";

/**
 * Project menu behaviour.
 *
 * The cases here are the ones where getting it wrong loses work or strands the user: deleting
 * the project that is currently open, deleting the last one, and a rename that fires twice.
 */

const STRINGS: Record<string, string> = {
  "canvas.defaultProject": "我的画布",
  "canvas.newProject": "新建项目",
  "canvas.projectRename": "重命名",
  "canvas.projectDelete": "删除项目",
};

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => (key: string) => STRINGS[key] ?? key,
}));

// The real one is a react-aria modal rendered through a portal. Standing in for it keeps these
// tests on the menu's own delete logic instead of the dialog's rendering.
vi.mock("@/components/ui/confirm-dialog", () => ({
  ConfirmDialog: ({ isOpen, onConfirm }: { isOpen: boolean; onConfirm: () => void }) =>
    isOpen ? (
      <button type="button" onClick={() => void onConfirm()}>
        confirm
      </button>
    ) : null,
}));

// Only the network calls are stubbed; `summarize` stays real, since turning a created document
// into a list entry is part of what these tests are checking.
vi.mock("@/features/canvas/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/features/canvas/api")>()),
  listCanvases: vi.fn(),
  createCanvas: vi.fn(),
  renameCanvas: vi.fn(),
  deleteCanvas: vi.fn(),
}));

const api = await import("@/features/canvas/api");
const { ProjectMenu } = await import("@/features/canvas/project-menu");
const { loadLocal, saveLocal } = await import("@/features/canvas/persistence");

function summary(id: string, name: string, nodeCount = 0): CanvasSummary {
  return {
    id,
    name,
    nodeCount,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
}

function document(id: string, name: string): CanvasDocument {
  return {
    id,
    name,
    graph: { nodes: [], edges: [] },
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
}

const switched = vi.fn();

/** Holds the list and the open id the way the canvas page does, so the menu stays controlled. */
function Harness({ initial }: { initial: CanvasSummary[] }) {
  const [projects, setProjects] = useState(initial);
  const [currentId, setCurrentId] = useState(initial[0].id);

  return (
    <ProjectMenu
      currentId={currentId}
      projects={projects}
      onProjectsChange={setProjects}
      onSwitch={(id) => {
        setCurrentId(id);
        switched(id);
      }}
    />
  );
}

/** Renders the menu with the list already open, since every case acts on a row. */
async function openMenu(initial: CanvasSummary[]) {
  vi.mocked(api.listCanvases).mockResolvedValue({ canvases: initial });

  const user = userEvent.setup();
  render(<Harness initial={initial} />);

  await user.click(screen.getByRole("button", { expanded: false }));
  // The menu refreshes the list as it opens; wait for that to land before acting on a row.
  await waitFor(() => expect(api.listCanvases).toHaveBeenCalled());

  return user;
}

beforeEach(() => {
  vi.clearAllMocks();
  window.localStorage.clear();
});

describe("ProjectMenu", () => {
  it("opens the project that was clicked", async () => {
    const user = await openMenu([summary("a", "A"), summary("b", "B")]);

    await user.click(screen.getByText("B"));

    expect(switched).toHaveBeenCalledWith("b");
  });

  it("gives each new project a name that does not collide with an existing one", async () => {
    // Three clicks of "new project" used to produce three identically named entries, which this
    // very list then had no way to tell apart.
    const user = await openMenu([summary("a", "我的画布")]);
    vi.mocked(api.createCanvas).mockResolvedValue(document("b", "我的画布 2"));

    await user.click(screen.getByText("新建项目"));

    expect(api.createCanvas).toHaveBeenCalledWith("我的画布 2");
    await waitFor(() => expect(switched).toHaveBeenCalledWith("b"));
  });

  it("sends a rename once, not twice, when Enter commits the field", async () => {
    // Enter unmounts the input, and unmounting a focused input fires blur — which used to run
    // the commit a second time and send a second request.
    const user = await openMenu([summary("a", "A")]);
    vi.mocked(api.renameCanvas).mockResolvedValue(document("a", "renamed"));

    await user.click(screen.getByRole("button", { name: "重命名" }));
    await user.clear(screen.getByRole("textbox"));
    await user.type(screen.getByRole("textbox"), "renamed{Enter}");

    await waitFor(() => expect(api.renameCanvas).toHaveBeenCalledExactlyOnceWith("a", "renamed"));
    // Two: the row, and the trigger showing the open project's name.
    await waitFor(() => expect(screen.getAllByText("renamed")).toHaveLength(2));
  });

  it("keeps the old name when the rename is abandoned with Escape", async () => {
    const user = await openMenu([summary("a", "A")]);

    await user.click(screen.getByRole("button", { name: "重命名" }));
    await user.clear(screen.getByRole("textbox"));
    await user.type(screen.getByRole("textbox"), "discarded{Escape}");

    await waitFor(() => expect(screen.queryByRole("textbox")).toBeNull());
    expect(screen.getAllByText("A")).toHaveLength(2);
    expect(api.renameCanvas).not.toHaveBeenCalled();
  });

  it("stays on the open project when a different one is deleted", async () => {
    const user = await openMenu([summary("a", "A"), summary("b", "B")]);
    vi.mocked(api.deleteCanvas).mockResolvedValue(undefined);

    await user.click(screen.getAllByRole("button", { name: "删除项目" })[1]);
    await user.click(await screen.findByText("confirm"));

    await waitFor(() => expect(api.deleteCanvas).toHaveBeenCalledWith("b"));
    expect(switched).not.toHaveBeenCalled();
  });

  it("moves to a remaining project when the open one is deleted", async () => {
    const user = await openMenu([summary("a", "A"), summary("b", "B")]);
    vi.mocked(api.deleteCanvas).mockResolvedValue(undefined);

    await user.click(screen.getAllByRole("button", { name: "删除项目" })[0]);
    await user.click(await screen.findByText("confirm"));

    await waitFor(() => expect(switched).toHaveBeenCalledWith("b"));
    expect(api.createCanvas).not.toHaveBeenCalled();
  });

  it("creates a default project when the last one is deleted", async () => {
    // Otherwise the editor is left with nothing to open, and the bootstrap that would have
    // created a replacement only runs on load.
    const user = await openMenu([summary("a", "A")]);
    vi.mocked(api.deleteCanvas).mockResolvedValue(undefined);
    vi.mocked(api.createCanvas).mockResolvedValue(document("fresh", "我的画布"));

    await user.click(screen.getByRole("button", { name: "删除项目" }));
    await user.click(await screen.findByText("confirm"));

    await waitFor(() => expect(api.createCanvas).toHaveBeenCalledWith("我的画布"));
    expect(switched).toHaveBeenCalledWith("fresh");
  });

  it("drops the local cache of a deleted project", async () => {
    // The cached graph would otherwise outlive the server row, sitting in localStorage until
    // the browser is cleared.
    saveLocal("b", [{ id: "n", type: "text", position: { x: 0, y: 0 }, data: {} }], []);

    const user = await openMenu([summary("a", "A"), summary("b", "B")]);
    vi.mocked(api.deleteCanvas).mockResolvedValue(undefined);

    await user.click(screen.getAllByRole("button", { name: "删除项目" })[1]);
    await user.click(await screen.findByText("confirm"));

    await waitFor(() => expect(loadLocal("b")).toBeNull());
  });

  it("keeps the list usable when a delete fails", async () => {
    const user = await openMenu([summary("a", "A"), summary("b", "B")]);
    vi.mocked(api.deleteCanvas).mockRejectedValue(new Error("network is down"));

    await user.click(screen.getAllByRole("button", { name: "删除项目" })[1]);
    await user.click(await screen.findByText("confirm"));

    // The menu reopens to show the failure rather than dropping the row optimistically.
    await user.click(screen.getByRole("button", { expanded: false }));
    await waitFor(() => expect(screen.getByRole("alert")).toHaveTextContent("network is down"));
    expect(screen.getByText("B")).toBeInTheDocument();
  });
});
