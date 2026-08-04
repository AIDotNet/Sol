"use client";

import {
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
} from "@xyflow/react";
import { create } from "zustand";
import {
  type CanvasNodeData,
  type ExecutionStatus,
  isTerminal,
  NODE_DEFAULT_SIZE,
  type NodeExecution,
  type NodeKind,
} from "@/features/canvas/types";

/**
 * A canvas node.
 *
 * `type` stays optional/`string` rather than being narrowed to `NodeKind`, because
 * `useReactFlow().getNodes()` returns the library's own `Node` and narrowing here would force a
 * cast at every call site. Consumers narrow with `(node.type ?? "text") as NodeKind`.
 */
export type CanvasNode = Node<CanvasNodeData & Record<string, unknown>>;
export type CanvasEdge = Edge;

/** Undo depth. Snapshots share references with live state, so this is cheap. */
const HISTORY_LIMIT = 60;

/**
 * Bounds for collapsing a run of edits into a single undo entry.
 *
 * Typing used to take no undo slot at all, which meant Ctrl+Z jumped back to the last structural
 * change and discarded the whole paragraph. Taking a snapshot per keystroke is the other failure:
 * sixty presses of Ctrl+Z to undo one sentence. So consecutive edits to the same field of the same
 * node collapse — until the user pauses (idle) or the burst simply runs long (max).
 */
const HISTORY_COALESCE_IDLE_MS = 600;
const HISTORY_COALESCE_MAX_MS = 3000;

interface Snapshot {
  nodes: CanvasNode[];
  edges: CanvasEdge[];
}

interface CanvasState {
  nodes: CanvasNode[];
  edges: CanvasEdge[];
  past: Snapshot[];
  future: Snapshot[];
  /** Bumped on every persistable change; the autosave effect watches this. */
  revision: number;

  onNodesChange: (changes: NodeChange<CanvasNode>[]) => void;
  onEdgesChange: (changes: EdgeChange[]) => void;
  onConnect: (connection: Connection) => void;

  addNode: (kind: NodeKind, position: { x: number; y: number }, data?: Partial<CanvasNodeData>) => string;
  /**
   * Patches a node's data.
   *
   * Records an undo step by default, coalescing a burst of edits to the same field. Pass
   * `history: false` for bookkeeping a user did not perform — a poller writing back a finished
   * asset URL should not occupy an undo slot, nor merge into the edit that happened to precede it.
   */
  updateNodeData: (
    id: string,
    patch: Partial<CanvasNodeData>,
    options?: { history?: boolean },
  ) => void;
  /**
   * Deletes nodes, and any edge touching one of them.
   *
   * Records an undo step by default. Pass `history: false` for cleanup the user did not ask for:
   * a placeholder discarded because its run failed should not come back on Ctrl+Z, since nothing
   * would ever fill it in.
   */
  removeNodes: (ids: string[], options?: { history?: boolean }) => void;
  duplicateNode: (id: string) => void;
  selectAll: () => void;

  setExecution: (id: string, execution: NodeExecution | undefined) => void;
  patchExecution: (id: string, status: ExecutionStatus, patch?: Partial<NodeExecution>) => void;

  /** Creates an output node to the right of `sourceId` and wires it up. */
  spawnOutput: (
    sourceId: string,
    kind: NodeKind,
    data?: Partial<CanvasNodeData>,
    /** Row index when one run produces several outputs; stacks them downward. */
    stackIndex?: number,
  ) => string | null;

  undo: () => void;
  redo: () => void;
  load: (snapshot: Snapshot) => void;
  reset: () => void;
}

let nodeCounter = 0;

/**
 * Generates a node id.
 *
 * `crypto.randomUUID` needs a secure context, which excludes plain-HTTP LAN testing, so there is
 * a counter fallback. Ids only need to be unique within one canvas.
 */
function newId(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  nodeCounter += 1;
  return `n${Date.now().toString(36)}-${nodeCounter}`;
}

export function newRunId(): string {
  return newId();
}

export const useCanvasStore = create<CanvasState>((set, get) => {
  /** The edit burst currently being collapsed into one undo entry, if any. */
  let coalescing: { key: string; startedAt: number; lastAt: number } | null = null;

  /**
   * Records the current state for undo.
   *
   * Snapshots share node references with live state rather than deep-cloning. Every mutation
   * below is immutable, so sharing is safe — and cloning would copy the whole graph on every
   * drag frame.
   *
   * A `coalesceKey` identifies what is being edited (node id plus the fields in the patch). While
   * the same key keeps arriving, the snapshot taken when the burst started already represents the
   * pre-edit state, so no further snapshot is needed.
   */
  function pushHistory(coalesceKey?: string) {
    const now = Date.now();

    if (
      coalesceKey &&
      coalescing?.key === coalesceKey &&
      now - coalescing.lastAt < HISTORY_COALESCE_IDLE_MS &&
      now - coalescing.startedAt < HISTORY_COALESCE_MAX_MS
    ) {
      coalescing.lastAt = now;
      return;
    }

    coalescing = coalesceKey ? { key: coalesceKey, startedAt: now, lastAt: now } : null;

    const { nodes, edges, past } = get();
    const next = [...past, { nodes, edges }];
    set({
      past: next.length > HISTORY_LIMIT ? next.slice(-HISTORY_LIMIT) : next,
      future: [],
    });
  }

  function bump() {
    set((state) => ({ revision: state.revision + 1 }));
  }

  return {
    nodes: [],
    edges: [],
    past: [],
    future: [],
    revision: 0,

    onNodesChange: (changes) => {
      // Position changes stream once per pointer frame. History is recorded when the drag ends
      // (dragging === false), so one gesture is one undo step rather than hundreds.
      const endsDrag = changes.some(
        (change) => change.type === "position" && change.dragging === false,
      );
      const structural = changes.some(
        (change) => change.type === "remove" || change.type === "add",
      );

      if (endsDrag || structural) {
        pushHistory();
      }

      set((state) => ({ nodes: applyNodeChanges(changes, state.nodes) }));

      // Selection is transient UI state, not something worth persisting or undoing.
      if (changes.some((change) => change.type !== "select")) {
        bump();
      }
    },

    onEdgesChange: (changes) => {
      if (changes.some((change) => change.type === "remove")) {
        pushHistory();
      }
      set((state) => ({ edges: applyEdgeChanges(changes, state.edges) }));
      if (changes.some((change) => change.type !== "select")) {
        bump();
      }
    },

    onConnect: (connection) => {
      pushHistory();
      set((state) => ({ edges: addEdge({ ...connection, type: "default" }, state.edges) }));
      bump();
    },

    addNode: (kind, position, data) => {
      pushHistory();

      const id = newId();
      const size = NODE_DEFAULT_SIZE[kind];

      set((state) => ({
        nodes: [
          ...state.nodes,
          {
            id,
            type: kind,
            position,
            data: (data ?? {}) as CanvasNode["data"],
            width: size.width,
            height: size.height,
          },
        ],
      }));

      bump();
      return id;
    },

    updateNodeData: (id, patch, options) => {
      if (options?.history === false) {
        // Not a user edit, so it neither takes an undo slot nor extends whatever burst was in
        // progress — the next keystroke should start a fresh entry rather than merge into one
        // that now sits on the far side of a background write.
        coalescing = null;
      } else {
        // Keyed by field as well as node: switching model and then typing are separate steps.
        pushHistory(`${id}:${Object.keys(patch).sort().join(",")}`);
      }

      set((state) => ({
        nodes: state.nodes.map((node) =>
          node.id === id ? { ...node, data: { ...node.data, ...patch } } : node,
        ),
      }));
      bump();
    },

    removeNodes: (ids, options) => {
      if (ids.length === 0) return;

      if (options?.history === false) {
        // Same reasoning as `updateNodeData`: a background write must not extend the burst a
        // user edit was building, or the next keystroke merges across it.
        coalescing = null;
      } else {
        pushHistory();
      }

      const removed = new Set(ids);
      set((state) => ({
        nodes: state.nodes.filter((node) => !removed.has(node.id)),
        // Edges to a deleted node would otherwise render as dangling lines.
        edges: state.edges.filter(
          (edge) => !removed.has(edge.source) && !removed.has(edge.target),
        ),
      }));
      bump();
    },

    duplicateNode: (id) => {
      const node = get().nodes.find((candidate) => candidate.id === id);
      if (!node) return;

      pushHistory();
      set((state) => ({
        nodes: [
          ...state.nodes,
          {
            ...node,
            id: newId(),
            position: { x: node.position.x + 32, y: node.position.y + 32 },
            selected: false,
            // A copy has produced nothing yet; carrying the execution over would show a stale
            // "succeeded" on a node with no output.
            data: { ...node.data, execution: undefined },
          },
        ],
      }));
      bump();
    },

    /** Selection is view state, so this deliberately skips history and autosave. */
    selectAll: () => {
      set((state) => ({ nodes: state.nodes.map((node) => ({ ...node, selected: true })) }));
    },

    setExecution: (id, execution) => {
      set((state) => ({
        nodes: state.nodes.map((node) =>
          node.id === id ? { ...node, data: { ...node.data, execution } } : node,
        ),
      }));
    },

    patchExecution: (id, status, patch) => {
      set((state) => ({
        nodes: state.nodes.map((node) => {
          if (node.id !== id) return node;

          const current = (node.data as { execution?: NodeExecution }).execution;
          const execution: NodeExecution = {
            runId: current?.runId ?? newRunId(),
            ...current,
            ...patch,
            status,
            ...(status === "running" && !current?.startedAt ? { startedAt: Date.now() } : {}),
            ...(isTerminal(status) ? { finishedAt: Date.now() } : {}),
          };

          return { ...node, data: { ...node.data, execution } };
        }),
      }));

      // Terminal outcomes are worth persisting; progress ticks are not — bumping on every frame
      // of a video poll would rewrite storage every few seconds for nothing.
      if (isTerminal(status)) {
        bump();
      }
    },

    spawnOutput: (sourceId, kind, data, stackIndex = 0) => {
      const source = get().nodes.find((node) => node.id === sourceId);
      if (!source) return null;

      const id = newId();
      const size = NODE_DEFAULT_SIZE[kind];
      const sourceWidth = source.width ?? NODE_DEFAULT_SIZE[(source.type ?? "text") as NodeKind].width;

      // Placed to the right of its source and auto-connected, so a run visibly extends the
      // graph rather than silently mutating the node that started it. Several outputs from one
      // run stack downward — the offset is applied here rather than patched in afterwards, so
      // the placement is part of the same history entry and triggers one autosave.
      set((state) => ({
        nodes: [
          ...state.nodes,
          {
            id,
            type: kind,
            position: {
              x: source.position.x + sourceWidth + 60,
              y: source.position.y + stackIndex * (size.height + 24),
            },
            data: (data ?? {}) as CanvasNode["data"],
            width: size.width,
            height: size.height,
          },
        ],
        edges: [...state.edges, { id: `e${sourceId}-${id}`, source: sourceId, target: id }],
      }));

      bump();
      return id;
    },

    undo: () => {
      const { past, nodes, edges } = get();
      const previous = past.at(-1);
      if (!previous) return;

      // An edit that resumes after an undo must not merge into the entry that was just popped.
      coalescing = null;

      set((state) => ({
        nodes: previous.nodes,
        edges: previous.edges,
        past: past.slice(0, -1),
        future: [{ nodes, edges }, ...state.future].slice(0, HISTORY_LIMIT),
      }));
      bump();
    },

    redo: () => {
      const { future, nodes, edges } = get();
      const next = future[0];
      if (!next) return;

      coalescing = null;

      set((state) => ({
        nodes: next.nodes,
        edges: next.edges,
        past: [...state.past, { nodes, edges }].slice(-HISTORY_LIMIT),
        future: future.slice(1),
      }));
      bump();
    },

    load: (snapshot) => {
      coalescing = null;
      set({ nodes: snapshot.nodes, edges: snapshot.edges, past: [], future: [], revision: 0 });
    },

    reset: () => {
      coalescing = null;
      set({ nodes: [], edges: [], past: [], future: [], revision: 0 });
    },
  };
});
