import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CanvasAssetGroupSummary, CanvasAssetSummary } from "@/features/canvas/api";

const STRINGS: Record<string, string> = {
  "common.add": "Add",
  "common.cancel": "Cancel",
  "common.delete": "Delete",
  "canvas.assetLibrary": "Asset library",
  "canvas.assetLibraryHint": "Reusable assets.",
  "canvas.assetGroupDragHint": "Drop on a group.",
  "canvas.assetAll": "All assets",
  "canvas.assetUngrouped": "Ungrouped",
  "canvas.assetGroups": "Groups",
  "canvas.assetGroupCreate": "New group",
  "canvas.assetGroupNamePlaceholder": "Group name",
  "canvas.assetGroupNameRequired": "Enter a name",
  "canvas.assetGroupNameDuplicate": "Duplicate group",
  "canvas.assetGroupRename": "Rename group",
  "canvas.assetGroupDelete": "Delete group",
  "canvas.assetGroupDeleteConfirm": "Delete {name}?",
  "canvas.assetGroupEmpty": "No groups",
  "canvas.assetGroupEmptyHint": "Create one.",
  "canvas.assetGroupNoAssets": "No assets here",
  "canvas.assetGroupMove": "Move to group",
  "canvas.assetGroupMoveNone": "Remove from group",
  "canvas.assetGroupCount": "{count} assets",
  "canvas.assetSelect": "Select asset",
  "canvas.promptSelected": "{count} selected",
  "canvas.assetLibraryEmpty": "Empty",
  "canvas.assetLibraryEmptyHint": "Upload something.",
  "canvas.assetDeleteConfirm": "Delete asset?",
  "errors.unknown": "Unknown error",
};

const translate = (key: string, values?: Record<string, string | number>) => {
  const template = STRINGS[key] ?? key;
  return template.replace(/\{(\w+)\}/g, (match, name: string) =>
    values && name in values ? String(values[name]) : match,
  );
};

vi.mock("@/components/providers/i18n-provider", () => ({
  useT: () => translate,
}));

// The real dialog is a portal with focus management. These tests exercise the asset library's
// own state transitions, so a semantic wrapper keeps them focused and deterministic.
vi.mock("@/components/ui/dialog", () => ({
  Dialog: ({ children }: { children: React.ReactNode }) => <div role="dialog">{children}</div>,
  DialogHeader: ({ children }: { children: React.ReactNode }) => <header>{children}</header>,
  DialogTitle: ({ children }: { children: React.ReactNode }) => <h2>{children}</h2>,
  DialogDescription: ({ children }: { children: React.ReactNode }) => <p>{children}</p>,
  DialogFooter: ({ children }: { children: React.ReactNode }) => <footer>{children}</footer>,
}));

vi.mock("@/components/ui/confirm-dialog", () => ({
  ConfirmDialog: ({
    isOpen,
    title,
    onConfirm,
  }: {
    isOpen: boolean;
    title: string;
    onConfirm: () => void | Promise<void>;
  }) =>
    isOpen ? (
      <button type="button" onClick={() => void onConfirm()}>
        confirm:{title}
      </button>
    ) : null,
}));

vi.mock("@/features/canvas/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/features/canvas/api")>()),
  listAssets: vi.fn(),
  listAssetGroups: vi.fn(),
  createAssetGroup: vi.fn(),
  renameAssetGroup: vi.fn(),
  deleteAssetGroup: vi.fn(),
  assignAssetsToGroup: vi.fn(),
  deleteAsset: vi.fn(),
}));

const api = await import("@/features/canvas/api");
const { AssetLibraryDialog } = await import("@/features/canvas/asset-library-dialog");
const { useCanvasStore } = await import("@/features/canvas/store");

function asset(id: string, prompt: string, groupId: string | null): CanvasAssetSummary {
  return {
    id,
    url: `/api/v1/canvas/assets/${id}`,
    kind: "image",
    mediaType: "image/png",
    byteSize: 100,
    prompt,
    createdAt: "2026-08-06T00:00:00.000Z",
    groupId,
  };
}

function group(id: string, name: string, assetCount: number): CanvasAssetGroupSummary {
  return {
    id,
    name,
    assetCount,
    createdAt: "2026-08-06T00:00:00.000Z",
    updatedAt: "2026-08-06T00:00:00.000Z",
  };
}

function renderLibrary() {
  return render(
    <AssetLibraryDialog onOpenChange={vi.fn()} at={() => ({ x: 0, y: 0 })} />,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  useCanvasStore.getState().reset();

  vi.mocked(api.listAssets).mockResolvedValue({
    assets: [asset("a", "Alpha", "g1"), asset("b", "Beta", null)],
  });
  vi.mocked(api.listAssetGroups).mockResolvedValue({
    groups: [group("g1", "Mood", 1)],
  });
  vi.mocked(api.assignAssetsToGroup).mockResolvedValue({ updated: 1 });
  vi.mocked(api.deleteAssetGroup).mockResolvedValue(undefined);
});

describe("AssetLibraryDialog groups", () => {
  it("filters the library by group and by ungrouped assets", async () => {
    const user = userEvent.setup();
    renderLibrary();

    expect(await screen.findByRole("button", { name: "Alpha" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Beta" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /Mood/ }));
    expect(screen.getByRole("button", { name: "Alpha" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Beta" })).toBeNull();

    await user.click(screen.getByRole("button", { name: /Ungrouped/ }));
    expect(screen.queryByRole("button", { name: "Alpha" })).toBeNull();
    expect(screen.getByRole("button", { name: "Beta" })).toBeInTheDocument();
  });

  it("creates a named group from the navigation rail", async () => {
    const user = userEvent.setup();
    const created = group("g2", "Shots", 0);
    vi.mocked(api.createAssetGroup).mockResolvedValue(created);
    renderLibrary();

    await screen.findByRole("button", { name: "Alpha" });
    await user.click(screen.getByRole("button", { name: "New group" }));
    await user.type(screen.getByRole("textbox", { name: "Group name" }), "Shots{Enter}");

    await waitFor(() => expect(api.createAssetGroup).toHaveBeenCalledWith("Shots"));
    expect(screen.getByRole("button", { name: /Shots/ })).toBeInTheDocument();
  });

  it("moves a multi-selection with one bulk request", async () => {
    const user = userEvent.setup();
    renderLibrary();

    await user.click(await screen.findByRole("button", { name: "Alpha" }));
    await user.click(screen.getByRole("button", { name: "Beta" }));
    await user.selectOptions(screen.getByRole("combobox", { name: "Move to group" }), "g1");

    await waitFor(() =>
      expect(api.assignAssetsToGroup).toHaveBeenCalledWith(["a", "b"], "g1"),
    );

    await user.click(screen.getByRole("button", { name: /Mood/ }));
    expect(screen.getByRole("button", { name: "Alpha" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Beta" })).toBeInTheDocument();
  });

  it("renames a group once when Enter commits the inline field", async () => {
    const user = userEvent.setup();
    vi.mocked(api.renameAssetGroup).mockResolvedValue(group("g1", "Palette", 1));
    renderLibrary();

    await screen.findByRole("button", { name: "Alpha" });
    await user.click(screen.getByRole("button", { name: "Rename group" }));
    const input = screen.getByRole("textbox", { name: "Group name" });
    await user.clear(input);
    await user.type(input, "Palette{Enter}");

    await waitFor(() =>
      expect(api.renameAssetGroup).toHaveBeenCalledExactlyOnceWith("g1", "Palette"),
    );
    expect(screen.getByRole("button", { name: /Palette/ })).toBeInTheDocument();
  });

  it("deletes only the group and leaves its assets under Ungrouped", async () => {
    const user = userEvent.setup();
    renderLibrary();

    await screen.findByRole("button", { name: "Alpha" });
    await user.click(screen.getByRole("button", { name: "Delete group" }));
    await user.click(screen.getByText("confirm:Delete group"));

    await waitFor(() => expect(api.deleteAssetGroup).toHaveBeenCalledWith("g1"));
    expect(screen.queryByRole("button", { name: /Mood/ })).toBeNull();

    await user.click(screen.getByRole("button", { name: /Ungrouped/ }));
    expect(screen.getByRole("button", { name: "Alpha" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Beta" })).toBeInTheDocument();
  });
});
