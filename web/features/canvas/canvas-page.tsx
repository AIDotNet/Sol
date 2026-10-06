"use client";

import {
  Background,
  BackgroundVariant,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  type Connection,
  useReactFlow,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import {
  BookOpen,
  Bot,
  Check,
  Clapperboard,
  CloudAlert,
  Copy,
  Download,
  Film,
  Grid3x3,
  ImageIcon,
  Images,
  LayoutGrid,
  Loader2,
  Maximize,
  Redo2,
  Settings,
  SquareDashed,
  Trash2,
  Type,
  Undo2,
  Upload as UploadIcon,
  Wand2,
  Workflow,
} from "lucide-react";
import { useTheme } from "next-themes";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type Dispatch,
  type SetStateAction,
} from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipTrigger } from "@/components/ui/tooltip";
import { AgentPanel } from "@/features/agent/agent-panel";
import { AgentRuntime } from "@/features/agent/agent-runtime";
import { registerAgentCanvasControls } from "@/features/agent/canvas-control";
import { useAgentStore } from "@/features/agent/store";
import { StoryDialog } from "@/features/agent/story-dialog";
import { useAiStore } from "@/features/ai/store";
import {
  centredAt,
  downloadJson,
  useImageDrop,
  usePasteImages,
} from "@/features/canvas/canvas-interactions";
import { resumeVideoJobs } from "@/features/canvas/execution";
import {
  connectColor,
  LightningConnectionLine,
  useConnectPulseStore,
} from "@/features/canvas/connection-effects";
import { ImageGenNode } from "@/features/canvas/nodes/image-gen-node";
import { AssetLibraryDialog } from "@/features/canvas/asset-library-dialog";
import { PromptLibraryDialog } from "@/features/canvas/prompt-library-dialog";
import { ImageNode } from "@/features/canvas/nodes/image-node";
import { TextNode } from "@/features/canvas/nodes/text-node";
import { VideoGenNode } from "@/features/canvas/nodes/video-gen-node";
import { VideoNode } from "@/features/canvas/nodes/video-node";
import { WorkflowTemplateDialog } from "@/features/canvas/workflow-template-dialog";
import { exportCanvas, importCanvas } from "@/features/canvas/persistence";
import { useCanvasStore } from "@/features/canvas/store";
import { createCanvas, listCanvases, summarize, type CanvasSummary } from "@/features/canvas/api";
import { ProjectMenu } from "@/features/canvas/project-menu";
import {
  lastOpenedCanvas,
  migrateLegacyCanvas,
  persistCanvas,
  rememberCurrentCanvas,
  resolveCanvas,
  type SaveState,
} from "@/features/canvas/sync";
import {
  CANVAS_TEMPLATES,
  instantiateCanvasTemplate,
  type CanvasTemplateDefinition,
} from "@/features/canvas/templates";
import { NODE_DEFAULT_SIZE, type NodeKind } from "@/features/canvas/types";
import { wouldCreateCycle } from "@/features/canvas/upstream";
import { SettingsDialog } from "@/features/settings/settings-dialog";
import { AuthMenu } from "@/features/auth/auth-menu";
import { UrlConfigPrompt } from "@/features/settings/url-config-prompt";
import { handshake, storedDeviceId } from "@/lib/device";
import { cn } from "@/lib/utils";

/**
 * The node renderers, keyed by kind.
 *
 * Declared at module scope because React Flow rebuilds its internal node registry whenever this
 * object changes identity — an inline literal would do that on every render.
 *
 * Deliberately not wrapped in `memo`, despite the usual advice. React Flow v12 subscribes each
 * node wrapper to its own slice of the internal store, so dragging one node out of twenty was
 * measured re-rendering two node components, not twenty. `memo` on top of that buys a shallow
 * prop comparison per node per commit and nothing else.
 */
const NODE_TYPES = {
  text: TextNode,
  image: ImageNode,
  video: VideoNode,
  imageGen: ImageGenNode,
  videoGen: VideoGenNode,
};

/**
 * Props React Flow only reads, hoisted out of the render.
 *
 * `nodes` changes identity on every frame of a drag, so the component around it re-renders at
 * pointer rate. Anything declared inline in that JSX — an object literal, an arrow function —
 * is rebuilt each of those frames and looks like a changed prop to React Flow. Out here they
 * are allocated once.
 */
const PRO_OPTIONS = { hideAttribution: true };
// Capped: fitView scales to fill the viewport, so one or two small nodes would otherwise zoom
// to 3x and fill the screen with a single text box.
const FIT_VIEW_OPTIONS = { maxZoom: 1, padding: 0.2 };
const DELETE_KEY_CODES = ["Backspace", "Delete"];
// How close (flow units) a drag must come to a handle before it snaps on. The lightning arc lives
// in that gap, so React Flow's default of 20 would hide it under the cursor; this lets it strike
// from about a node-row away, as in the reference.
const CONNECTION_RADIUS = 90;
const FIRST_VISIT_TEMPLATE = CANVAS_TEMPLATES.find((template) => template.id === "text-to-image")!;

function newCanvasGraphId(): string {
  return crypto.randomUUID();
}

/** Debounce for autosave. Long enough that a drag or a burst of typing is one write. */
const AUTOSAVE_DELAY_MS = 400;

/** How long a freshly opened project suppresses per-node entrance animations. */
const HYDRATION_SETTLE_MS = 250;

/**
 * The canvas, mounted at the site root.
 *
 * Which document to open is resolved here rather than carried in the URL: there is one canvas
 * in view at a time, and putting an id in the path would mean the front door redirects before
 * it can render anything.
 */
export function CanvasPage() {
  const [canvasId, setCanvasId] = useState<string | null>(null);
  const [projects, setProjects] = useState<CanvasSummary[]>([]);
  const [resolveError, setResolveError] = useState<string | null>(null);
  const t = useT();

  useEffect(() => {
    let cancelled = false;

    void (async () => {
      try {
        // Canvas storage is device-scoped, so an identity has to exist first.
        if (!storedDeviceId()) {
          await handshake();
        }

        // A canvas from before multi-document support is adopted rather than abandoned. It is
        // on the server by the time the list is fetched, so it shows up there too.
        const migrated = await migrateLegacyCanvas(t("canvas.defaultProject"));

        let { canvases } = await listCanvases();

        // First visit on this device. Something to draw on has to exist before the editor
        // mounts, and a named project is more use than an implicit unnamed one — it gives the
        // project menu a starting entry instead of an empty list.
        if (canvases.length === 0) {
          canvases = [
            summarize(
              await createCanvas(
                t("canvas.defaultProject"),
                instantiateCanvasTemplate(FIRST_VISIT_TEMPLATE, newCanvasGraphId),
              ),
            ),
          ];
        }

        const remembered = lastOpenedCanvas();
        const target =
          migrated ??
          canvases.find((canvas) => canvas.id === remembered)?.id ??
          canvases[0].id;

        if (!cancelled) {
          setProjects(canvases);
          setCanvasId(target);
        }
      } catch (error) {
        if (!cancelled) {
          setResolveError(error instanceof Error ? error.message : t("errors.unknown"));
        }
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [t]);

  if (resolveError) {
    return (
      <div className="flex h-full w-full flex-col items-center justify-center gap-3">
        <p className="text-sm text-destructive">{resolveError}</p>
        <Button size="sm" variant="outline" onPress={() => window.location.reload()}>
          {t("common.retry")}
        </Button>
      </div>
    );
  }

  if (!canvasId) {
    return (
      <div className="flex h-full w-full flex-col items-center justify-center gap-3">
        <Loader2 className="size-5 animate-spin text-muted-foreground" aria-hidden />
        <p className="text-xs text-muted-foreground">{t("common.loading")}</p>
      </div>
    );
  }

  return (
    <ReactFlowProvider>
      {/* Keyed so switching documents remounts the editor with clean history. */}
      <CanvasInner
        key={canvasId}
        canvasId={canvasId}
        projects={projects}
        onProjectsChange={setProjects}
        onSwitch={setCanvasId}
      />
    </ReactFlowProvider>
  );
}

function CanvasInner({
  canvasId,
  projects,
  onProjectsChange,
  onSwitch,
}: {
  canvasId: string;
  projects: CanvasSummary[];
  onProjectsChange: Dispatch<SetStateAction<CanvasSummary[]>>;
  onSwitch: (id: string) => void;
}) {
  const t = useT();
  const { resolvedTheme } = useTheme();
  const { screenToFlowPosition, getNodes, getEdges, fitView, zoomIn, zoomOut } = useReactFlow();

  const nodes = useCanvasStore((state) => state.nodes);
  const edges = useCanvasStore((state) => state.edges);
  const revision = useCanvasStore((state) => state.revision);
  const onNodesChange = useCanvasStore((state) => state.onNodesChange);
  const onEdgesChange = useCanvasStore((state) => state.onEdgesChange);
  const onConnect = useCanvasStore((state) => state.onConnect);
  const addNode = useCanvasStore((state) => state.addNode);
  const addTemplate = useCanvasStore((state) => state.addTemplate);
  const duplicateNode = useCanvasStore((state) => state.duplicateNode);
  const autoLayout = useCanvasStore((state) => state.autoLayout);
  const removeNodes = useCanvasStore((state) => state.removeNodes);
  const selectAll = useCanvasStore((state) => state.selectAll);
  const undo = useCanvasStore((state) => state.undo);
  const redo = useCanvasStore((state) => state.redo);
  const load = useCanvasStore((state) => state.load);
  const pulses = useConnectPulseStore((state) => state.pulses);
  const firePulse = useConnectPulseStore((state) => state.fire);

  const loadAi = useAiStore((state) => state.load);
  const agentOpen = useAgentStore((state) => state.open);
  const setAgentOpen = useAgentStore((state) => state.setOpen);

  const [settingsOpen, setSettingsOpen] = useState(false);
  const [promptsOpen, setPromptsOpen] = useState(false);
  const [assetsOpen, setAssetsOpen] = useState(false);
  const [templatesOpen, setTemplatesOpen] = useState(false);
  const [storyOpen, setStoryOpen] = useState(false);
  const [background, setBackground] = useState<BackgroundVariant | "none">(BackgroundVariant.Dots);
  const [menu, setMenu] = useState<{
    x: number;
    y: number;
    nodeId: string | null;
    edgeId: string | null;
  } | null>(null);
  const [saveState, setSaveState] = useState<SaveState>("idle");
  const wrapperRef = useRef<HTMLDivElement>(null);
  const importRef = useRef<HTMLInputElement>(null);
  const hydrated = useRef(false);
  const autosaveTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const autosavePending = useRef(false);
  const positionGestures = useRef(new Set<string>());

  const clearAutosaveTimer = useCallback(() => {
    clearTimeout(autosaveTimer.current);
    autosaveTimer.current = undefined;
  }, []);

  const scheduleAutosave = useCallback(() => {
    clearAutosaveTimer();
    if (!autosavePending.current || positionGestures.current.size > 0) return;

    autosaveTimer.current = setTimeout(() => {
      autosaveTimer.current = undefined;

      // Drag start normally clears this timer. Recheck here as well so callback ordering can never
      // make persistence read an intermediate node position.
      if (positionGestures.current.size > 0) return;

      autosavePending.current = false;
      const state = useCanvasStore.getState();
      void persistCanvas(canvasId, state.nodes, state.edges).then(setSaveState);
    }, AUTOSAVE_DELAY_MS);
  }, [canvasId, clearAutosaveTimer]);

  useEffect(
    () =>
      registerAgentCanvasControls({
        fitView: () => fitView({ duration: 200, maxZoom: 1 }),
        zoomIn: () => zoomIn({ duration: 150 }),
        zoomOut: () => zoomOut({ duration: 150 }),
      }),
    [fitView, zoomIn, zoomOut],
  );

  // Restore before the first autosave can run, so an empty initial store never overwrites a
  // saved canvas. The local copy paints instantly; the server copy wins if it is newer.
  useEffect(() => {
    let cancelled = false;
    let settle: ReturnType<typeof setTimeout> | undefined;
    hydrated.current = false;
    wrapperRef.current?.setAttribute("data-hydrating", "true");

    async function hydrate() {
      const resolved = await resolveCanvas(canvasId);
      if (cancelled) return;

      load({ nodes: resolved.nodes, edges: resolved.edges });
      rememberCurrentCanvas(canvasId);

      // Video jobs keep running server-side across a reload. Re-attach their pollers, or the
      // restored nodes spin forever with nothing left to resolve them.
      resumeVideoJobs(resolved.nodes);
      hydrated.current = true;

      // Restored nodes all mount on this one commit. Letting each play its entrance turns
      // opening a project into a screenful of popping rectangles, so the animation stays
      // suppressed until they have painted — released here rather than on a timer started
      // alongside the fetch, which would expire while the fetch was still in flight and let
      // the whole canvas animate in after all.
      settle = setTimeout(
        () => wrapperRef.current?.removeAttribute("data-hydrating"),
        HYDRATION_SETTLE_MS,
      );
    }

    void hydrate();
    void loadAi();

    return () => {
      cancelled = true;
      clearTimeout(settle);
    };
  }, [canvasId, load, loadAi]);

  /**
   * Autosave.
   *
   * Live node positions still update on every pointer frame, but their revision is deferred until
   * release. The drag callbacks below also suspend any timer armed by an earlier edit, ensuring its
   * callback cannot read and persist an intermediate position.
   */
  useEffect(() => {
    if (!hydrated.current) return;

    autosavePending.current = true;
    setSaveState("saving");
    scheduleAutosave();
  }, [revision, scheduleAutosave]);

  useEffect(() => clearAutosaveTimer, [clearAutosaveTimer]);

  // Keyboard commands. A global listener is safe as long as it ignores events from inside text
  // fields, where these chords mean their normal editing thing.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      if (target && /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
      if (target?.isContentEditable) return;

      const mod = event.metaKey || event.ctrlKey;
      if (!mod) return;

      switch (event.key.toLowerCase()) {
        case "z":
          event.preventDefault();
          if (event.shiftKey) redo();
          else undo();
          break;

        case "a":
          event.preventDefault();
          selectAll();
          break;

        case "d": {
          // Duplicating the selection, matching the node context menu.
          const selected = useCanvasStore.getState().nodes.filter((node) => node.selected);
          if (selected.length > 0) {
            event.preventDefault();
            selected.forEach((node) => duplicateNode(node.id));
          }
          break;
        }

        default:
          break;
      }
    }

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [undo, redo, selectAll, duplicateNode]);

  /** Viewport centre in flow coordinates, used as the landing point for pasted images. */
  const viewportCentre = useCallback(() => {
    const bounds = wrapperRef.current?.getBoundingClientRect();
    return screenToFlowPosition({
      x: (bounds?.left ?? 0) + (bounds?.width ?? 800) / 2,
      y: (bounds?.top ?? 0) + (bounds?.height ?? 600) / 2,
    });
  }, [screenToFlowPosition]);

  const dropImages = useImageDrop();
  usePasteImages(
    useCallback(
      (files, at) => void dropImages(files, centredAt(at, "image")),
      [dropImages],
    ),
    viewportCentre,
  );

  const addAt = useCallback(
    (kind: NodeKind, client?: { x: number; y: number }) => {
      const bounds = wrapperRef.current?.getBoundingClientRect();

      if (client) {
        addNode(kind, screenToFlowPosition(client));
        setMenu(null);
        return;
      }

      // Toolbar adds go to the right of everything already placed. The graph reads
      // left-to-right (upstream inputs on the left), so appending follows the direction the
      // user is already building in — and never lands on top of an existing node.
      const existing = getNodes();
      const size = NODE_DEFAULT_SIZE[kind];

      if (existing.length > 0) {
        let rightmost = existing[0];
        for (const node of existing) {
          const edge = node.position.x + (node.width ?? 0);
          if (edge > rightmost.position.x + (rightmost.width ?? 0)) rightmost = node;
        }

        addNode(kind, {
          x: rightmost.position.x + (rightmost.width ?? size.width) + 60,
          y: rightmost.position.y,
        });
        setMenu(null);
        return;
      }

      const center = {
        x: (bounds?.left ?? 0) + (bounds?.width ?? 800) / 2,
        y: (bounds?.top ?? 0) + (bounds?.height ?? 600) / 2,
      };
      const position = screenToFlowPosition(center);

      addNode(kind, {
        x: position.x - size.width / 2,
        y: position.y - size.height / 2,
      });
      setMenu(null);
    },
    [addNode, screenToFlowPosition, getNodes],
  );

  const addTemplateAt = useCallback(
    (template: CanvasTemplateDefinition) => {
      const existing = getNodes();
      let origin: { x: number; y: number };

      if (existing.length > 0) {
        let rightmost = existing[0];
        for (const node of existing) {
          const nodeWidth = node.width ?? NODE_DEFAULT_SIZE[(node.type ?? "text") as NodeKind].width;
          const rightmostWidth =
            rightmost.width ?? NODE_DEFAULT_SIZE[(rightmost.type ?? "text") as NodeKind].width;
          if (node.position.x + nodeWidth > rightmost.position.x + rightmostWidth) {
            rightmost = node;
          }
        }

        origin = {
          x:
            rightmost.position.x +
            (rightmost.width ?? NODE_DEFAULT_SIZE[(rightmost.type ?? "text") as NodeKind].width) +
            80,
          y: rightmost.position.y,
        };
      } else {
        const width = Math.max(
          ...template.nodes.map(
            (node) => node.position.x + NODE_DEFAULT_SIZE[node.kind].width,
          ),
        );
        const height = Math.max(
          ...template.nodes.map(
            (node) => node.position.y + NODE_DEFAULT_SIZE[node.kind].height,
          ),
        );
        const centre = viewportCentre();
        origin = { x: centre.x - width / 2, y: centre.y - height / 2 };
      }

      const created = addTemplate(template, origin);
      setMenu(null);

      // The template may be taller than the current view. Fit only the inserted graph after
      // React Flow receives the new controlled nodes, keeping the example immediately visible.
      window.requestAnimationFrame(() => {
        void fitView({
          nodes: created.map((id) => ({ id })),
          duration: 250,
          maxZoom: 1,
          padding: 0.2,
        });
      });
    },
    [addTemplate, fitView, getNodes, viewportCentre],
  );

  const runAutoLayout = useCallback(() => {
    autoLayout();
    setMenu(null);

    // React Flow receives controlled node positions on the next render. Fit after that commit
    // so the newly arranged graph is immediately visible even when it is larger than the viewport.
    window.requestAnimationFrame(() => {
      void fitView({ duration: 250, maxZoom: 1, padding: 0.2 });
    });
  }, [autoLayout, fitView]);

  /**
   * Rejects a connection that would close a loop.
   *
   * `collectUpstream` tolerates cycles, but a looping graph is almost always a mis-drag and
   * would silently truncate the prompt it gathers.
   */
  const isValidConnection = useCallback(
    (connection: Connection | { source: string; target: string }) =>
      !wouldCreateCycle(connection.source, connection.target, getEdges()),
    [getEdges],
  );

  /**
   * Adds the edge, then plays the connection burst on its target.
   *
   * The burst is skipped for a connection that already exists — `addEdge` ignores duplicates, and
   * celebrating an edge that did not change would be misleading.
   */
  const handleConnect = useCallback(
    (connection: Connection) => {
      const duplicate = getEdges().some(
        (edge) =>
          edge.source === connection.source &&
          edge.target === connection.target &&
          (edge.sourceHandle ?? null) === (connection.sourceHandle ?? null) &&
          (edge.targetHandle ?? null) === (connection.targetHandle ?? null),
      );

      onConnect(connection);
      if (duplicate) return;

      const source = getNodes().find((node) => node.id === connection.source);
      firePulse(connection.source, connection.target, connectColor(source?.type));
    },
    [firePulse, getEdges, getNodes, onConnect],
  );

  /**
   * Marks the wrapper while a gesture is in flight, so CSS can stand down the expensive effects.
   *
   * Written straight to the DOM rather than held in state: a re-render of this component is
   * exactly the cost the flag exists to avoid, and re-rendering to announce "a drag started"
   * would spend it at the worst possible moment.
   *
   * Keyed by gesture rather than counted, because pan and node-drag can overlap and a counter
   * that misses one `end` — a pointer released outside the window, a gesture interrupted by a
   * re-render — stays stuck above zero and leaves the blur off for good.
   */
  const gestures = useRef(new Set<string>());
  const setGesture = useCallback((name: string, active: boolean) => {
    const live = gestures.current;
    if (active) live.add(name);
    else live.delete(name);

    const wrapper = wrapperRef.current;
    if (!wrapper) return;

    if (live.size > 0) wrapper.setAttribute("data-interacting", "true");
    else wrapper.removeAttribute("data-interacting");
  }, []);

  const onMoveStart = useCallback(() => setGesture("move", true), [setGesture]);
  const onMoveEnd = useCallback(() => setGesture("move", false), [setGesture]);

  const setPositionGesture = useCallback(
    (name: string, active: boolean) => {
      setGesture(name, active);

      if (active) {
        positionGestures.current.add(name);
        clearAutosaveTimer();
        return;
      }

      positionGestures.current.delete(name);
      if (positionGestures.current.size === 0) scheduleAutosave();
    },
    [clearAutosaveTimer, scheduleAutosave, setGesture],
  );

  const onNodeDragStart = useCallback(
    () => setPositionGesture("node", true),
    [setPositionGesture],
  );
  const onNodeDragStop = useCallback(
    () => setPositionGesture("node", false),
    [setPositionGesture],
  );
  const onSelectionDragStart = useCallback(
    () => setPositionGesture("selection", true),
    [setPositionGesture],
  );
  const onSelectionDragStop = useCallback(
    () => setPositionGesture("selection", false),
    [setPositionGesture],
  );

  const closeMenu = useCallback(() => setMenu(null), []);

  const onPaneContextMenu = useCallback((event: React.MouseEvent | MouseEvent) => {
    event.preventDefault();
    setMenu({ x: event.clientX, y: event.clientY, nodeId: null, edgeId: null });
  }, []);

  const onNodeContextMenu = useCallback((event: React.MouseEvent, node: { id: string }) => {
    event.preventDefault();
    setMenu({ x: event.clientX, y: event.clientY, nodeId: node.id, edgeId: null });
  }, []);

  const onEdgeContextMenu = useCallback((event: React.MouseEvent, edge: { id: string }) => {
    event.preventDefault();
    event.stopPropagation();
    setMenu({ x: event.clientX, y: event.clientY, nodeId: null, edgeId: edge.id });
  }, []);

  const onDragOver = useCallback((event: React.DragEvent) => {
    // Required for a drop to fire at all.
    event.preventDefault();
    event.dataTransfer.dropEffect = "copy";
  }, []);

  const onDrop = useCallback(
    (event: React.DragEvent) => {
      const files = [...event.dataTransfer.files].filter((file) =>
        file.type.startsWith("image/"),
      );
      if (files.length === 0) return;

      event.preventDefault();
      const at = screenToFlowPosition({ x: event.clientX, y: event.clientY });
      void dropImages(files, centredAt(at, "image"));
    },
    [dropImages, screenToFlowPosition],
  );

  /**
   * Edges feeding a node with a run in flight are drawn as marching dashes, so a generation
   * reads as something travelling along the graph rather than a spinner sitting in a box.
   *
   * Keyed on a joined list of busy ids instead of on `nodes`, which changes identity every drag
   * frame. With nothing running — the overwhelmingly common case — this hands back the original
   * array untouched and costs one pass to discover that.
   */
  const busyKey = nodes
    .filter((node) => {
      const status = (node.data as { execution?: { status?: string } }).execution?.status;
      return status === "queued" || status === "running";
    })
    .map((node) => node.id)
    .join(",");

  const flowEdges = useMemo(() => {
    const flashing = Object.values(pulses);
    if (!busyKey && flashing.length === 0) return edges;
    const busy = new Set(busyKey ? busyKey.split(",") : []);
    return edges.map((edge) => {
      let next = busy.has(edge.target) ? { ...edge, animated: true } : edge;

      // The edge that just landed briefly takes the colour of the burst on its target.
      const pulse = flashing.find(
        (candidate) => candidate.source === edge.source && candidate.target === edge.target,
      );
      if (pulse) {
        next = {
          ...next,
          className: cn(next.className, "canvas-edge-flash"),
          style: { ...next.style, "--burst": pulse.color } as CSSProperties,
        };
      }
      return next;
    });
  }, [edges, busyKey, pulses]);

  return (
    <div ref={wrapperRef} className="relative size-full">
      <ReactFlow
        nodes={nodes}
        edges={flowEdges}
        nodeTypes={NODE_TYPES}
        // React Flow styles its own chrome — controls, minimap, edges — and defaults to light
        // regardless of the page theme. Without this the zoom buttons render as white icons on
        // a white background in dark mode, i.e. invisible.
        colorMode={resolvedTheme === "dark" ? "dark" : resolvedTheme === "light" ? "light" : "system"}
        onNodesChange={onNodesChange}
        onEdgesChange={onEdgesChange}
        onConnect={handleConnect}
        isValidConnection={isValidConnection}
        connectionLineComponent={LightningConnectionLine}
        connectionRadius={CONNECTION_RADIUS}
        onMoveStart={onMoveStart}
        onMoveEnd={onMoveEnd}
        onNodeDragStart={onNodeDragStart}
        onNodeDragStop={onNodeDragStop}
        onSelectionDragStart={onSelectionDragStart}
        onSelectionDragStop={onSelectionDragStop}
        onPaneContextMenu={onPaneContextMenu}
        onNodeContextMenu={onNodeContextMenu}
        onEdgeContextMenu={onEdgeContextMenu}
        onPaneClick={closeMenu}
        onDragOver={onDragOver}
        onDrop={onDrop}
        proOptions={PRO_OPTIONS}
        fitView
        fitViewOptions={FIT_VIEW_OPTIONS}
        minZoom={0.15}
        maxZoom={3}
        deleteKeyCode={DELETE_KEY_CODES}
        className="bg-background"
      >
        {background !== "none" && <Background variant={background} gap={16} size={1} />}
        <Controls className="!bottom-4 !left-4" />
        {/* Colours come from colorMode; overriding them here fought its dark palette and
            left a light ring around the mask. */}
        <MiniMap
          className={cn(
          "!bottom-16 !rounded-lg !border !border-border",
            agentOpen ? "!right-[25rem] max-lg:!hidden" : "!right-4",
          )}
          pannable
          zoomable
          nodeColor="var(--color-muted-foreground)"
        />
      </ReactFlow>

      <a
        href="https://github.com/AIDotNet/Sol"
        target="_blank"
        rel="noreferrer"
        aria-label="GitHub"
        title="GitHub"
        className={cn(
          "canvas-panel absolute bottom-4 right-4 z-10 flex size-9 items-center justify-center rounded-lg border shadow-md transition-colors hover:bg-accent hover:text-accent-foreground",
          agentOpen && "right-[25rem] max-lg:right-4",
        )}
      >
        <span
          className="size-4 bg-current [mask-position:center] [mask-repeat:no-repeat] [mask-size:contain]"
          style={{ maskImage: "url('/github.svg')" }}
          aria-hidden
        />
      </a>

      {/* Top-left: the open project, with the add-node rail beneath it. */}
      <div className="absolute top-4 left-4 z-10 flex flex-col items-start gap-2">
        {/* Fixed width so a long project name truncates instead of pushing the pill across
            the canvas. `z-20` is load-bearing: `backdrop-blur` makes this box a stacking
            context, which traps the open menu's own z-index inside it — without a lift here the
            add-node rail below, being later in the DOM, paints straight through the menu. */}
        <div className="canvas-panel relative z-20 w-52 rounded-xl border p-1 shadow-md">
          <ProjectMenu
            currentId={canvasId}
            projects={projects}
            onProjectsChange={onProjectsChange}
            onSwitch={onSwitch}
          />
        </div>

        <div className="canvas-panel flex flex-col gap-1 rounded-xl border p-1 shadow-md [animation-delay:60ms]">
          <ToolButton icon={Type} label={t("canvas.nodeText")} onPress={() => addAt("text")} />
          <ToolButton icon={ImageIcon} label={t("canvas.nodeImage")} onPress={() => addAt("image")} />
          <div className="my-0.5 h-px bg-border" />
          <ToolButton icon={Wand2} label={t("canvas.nodeImageGen")} onPress={() => addAt("imageGen")} />
          <ToolButton icon={Film} label={t("canvas.nodeVideoGen")} onPress={() => addAt("videoGen")} />
          <div className="my-0.5 h-px bg-border" />
          <ToolButton
            icon={Workflow}
            label={t("canvas.workflowTemplates")}
            onPress={() => setTemplatesOpen(true)}
          />
          <ToolButton
            icon={Clapperboard}
            label={t("story.title")}
            onPress={() => setStoryOpen(true)}
          />
          <ToolButton
            icon={BookOpen}
            label={t("canvas.promptLibrary")}
            onPress={() => setPromptsOpen(true)}
          />
          <ToolButton
            icon={Images}
            label={t("canvas.assetLibrary")}
            onPress={() => setAssetsOpen(true)}
          />
        </div>
      </div>

      {/* Top-right controls */}
      <div className="canvas-panel absolute top-4 right-4 z-10 flex items-center gap-1 rounded-xl border p-1 shadow-md [animation-delay:120ms]">
        <AuthMenu />
        <div className="mx-0.5 h-5 w-px bg-border" />
        <SaveIndicator state={saveState} />
        <div className="mx-0.5 h-5 w-px bg-border" />
        <ToolButton icon={Undo2} label={t("canvas.undo")} onPress={undo} />
        <ToolButton icon={Redo2} label={t("canvas.redo")} onPress={redo} />
        <ToolButton icon={LayoutGrid} label={t("canvas.autoArrange")} onPress={runAutoLayout} />
        <ToolButton
          icon={Grid3x3}
          label={t("canvas.background")}
          onPress={() =>
            setBackground((current) =>
              current === BackgroundVariant.Dots
                ? BackgroundVariant.Lines
                : current === BackgroundVariant.Lines
                  ? "none"
                  : BackgroundVariant.Dots,
            )
          }
        />
        <div className="mx-0.5 h-5 w-px bg-border" />
        <ToolButton
          icon={Download}
          label={t("canvas.exportCanvas")}
          onPress={() =>
            downloadJson(
              `sol-canvas-${canvasId.slice(0, 8)}.json`,
              exportCanvas(getNodes(), getEdges()),
            )
          }
        />
        <ToolButton
          icon={UploadIcon}
          label={t("canvas.importCanvas")}
          onPress={() => importRef.current?.click()}
        />
        <div className="mx-0.5 h-5 w-px bg-border" />
        <ToolButton
          icon={Bot}
          label={t("agent.open")}
          onPress={() => setAgentOpen(!agentOpen)}
        />
        <ToolButton
          icon={Settings}
          label={t("canvas.settings")}
          onPress={() => setSettingsOpen(true)}
        />
      </div>

      <AgentRuntime canvasId={canvasId} />
      {agentOpen && <AgentPanel />}

      {menu && (
        <>
          {/* Click-away layer, so the menu closes even over a node. */}
          <div className="fixed inset-0 z-20" onClick={closeMenu} />
          <div
            className="canvas-menu fixed z-30 flex min-w-40 origin-top-left flex-col rounded-lg border bg-popover p-1 shadow-lg"
            style={{ left: menu.x, top: menu.y }}
          >
            {menu.nodeId ? (
              <>
                <MenuItem
                  icon={Copy}
                  label={t("canvas.duplicateNode")}
                  onSelect={() => {
                    duplicateNode(menu.nodeId!);
                    setMenu(null);
                  }}
                />
                <MenuItem
                  icon={Maximize}
                  label={t("canvas.fitView")}
                  onSelect={() => {
                    void fitView({ nodes: [{ id: menu.nodeId! }], duration: 200, maxZoom: 1.2 });
                    setMenu(null);
                  }}
                />
                <div className="my-1 h-px bg-border" />
                <MenuItem
                  icon={Trash2}
                  label={t("canvas.deleteNode")}
                  destructive
                  onSelect={() => {
                    removeNodes([menu.nodeId!]);
                    setMenu(null);
                  }}
                />
              </>
            ) : menu.edgeId ? (
              <MenuItem
                icon={Trash2}
                label={t("canvas.deleteConnection")}
                destructive
                onSelect={() => {
                  onEdgesChange([{ type: "remove", id: menu.edgeId! }]);
                  setMenu(null);
                }}
              />
            ) : (
              <>
                {(
                  [
                    ["text", t("canvas.nodeText"), Type],
                    ["image", t("canvas.nodeImage"), ImageIcon],
                    ["imageGen", t("canvas.nodeImageGen"), Wand2],
                    ["videoGen", t("canvas.nodeVideoGen"), Film],
                  ] as const
                ).map(([kind, label, Icon]) => (
                  <MenuItem
                    key={kind}
                    icon={Icon}
                    label={label}
                    onSelect={() => addAt(kind, { x: menu.x, y: menu.y })}
                  />
                ))}
                <div className="my-1 h-px bg-border" />
                <MenuItem
                  icon={Workflow}
                  label={t("canvas.workflowTemplates")}
                  onSelect={() => {
                    setTemplatesOpen(true);
                    setMenu(null);
                  }}
                />
                <div className="my-1 h-px bg-border" />
                <MenuItem
                  icon={SquareDashed}
                  label={t("canvas.selectAll")}
                  onSelect={() => {
                    selectAll();
                    setMenu(null);
                  }}
                />
                <MenuItem
                  icon={LayoutGrid}
                  label={t("canvas.autoArrange")}
                  onSelect={runAutoLayout}
                />
                <MenuItem
                  icon={Maximize}
                  label={t("canvas.fitView")}
                  onSelect={() => {
                    void fitView({ duration: 200, maxZoom: 1 });
                    setMenu(null);
                  }}
                />
              </>
            )}
          </div>
        </>
      )}

      {/* Import target for the toolbar button. */}
      <input
        ref={importRef}
        type="file"
        accept="application/json,.json"
        className="hidden"
        onChange={async (event) => {
          const file = event.target.files?.[0];
          // Reset first, so re-importing the same file fires a change event again.
          event.target.value = "";
          if (!file) return;

          const result = importCanvas(await file.text());
          if (!result.ok) {
            window.alert(`${t("canvas.importCanvas")}: ${result.detail}`);
            return;
          }

          load({ nodes: result.snapshot.nodes, edges: result.snapshot.edges });
          void fitView({ duration: 200, maxZoom: 1 });
        }}
      />

      <PromptLibraryDialog
        isOpen={promptsOpen}
        onOpenChange={setPromptsOpen}
        // Inserts into the selected text node, or creates one when nothing is selected.
        targetNodeId={
          nodes.find((node) => node.selected && node.type === "text")?.id ?? null
        }
      />

      <WorkflowTemplateDialog
        isOpen={templatesOpen}
        onOpenChange={setTemplatesOpen}
        onSelect={addTemplateAt}
      />

      <StoryDialog isOpen={storyOpen} onOpenChange={setStoryOpen} />

      {/* Mounted only while open, so it refetches each time and its selection resets. */}
      {assetsOpen && <AssetLibraryDialog onOpenChange={setAssetsOpen} at={viewportCentre} />}

      <SettingsDialog isOpen={settingsOpen} onOpenChange={setSettingsOpen} />
      <UrlConfigPrompt />
    </div>
  );
}

function ToolButton({
  icon: Icon,
  label,
  onPress,
}: {
  icon: typeof Type;
  label: string;
  onPress: () => void;
}) {
  return (
    <TooltipTrigger>
      <Button
        size="icon-sm"
        variant="ghost"
        onPress={onPress}
        aria-label={label}
        className={cn("size-7")}
      >
        <Icon className="size-3.5" aria-hidden />
      </Button>
      <Tooltip>{label}</Tooltip>
    </TooltipTrigger>
  );
}

function MenuItem({
  icon: Icon,
  label,
  onSelect,
  destructive,
}: {
  icon: typeof Type;
  label: string;
  onSelect: () => void;
  destructive?: boolean;
}) {
  return (
    <button
      type="button"
      onClick={onSelect}
      className={cn(
        "group/item flex items-center gap-2 rounded-md px-2 py-1.5 text-left text-xs transition-colors hover:bg-accent",
        destructive && "text-destructive hover:bg-destructive/10",
      )}
    >
      <Icon
        className={cn(
          "size-3.5 transition-transform duration-150 group-hover/item:scale-110",
          destructive ? "text-destructive" : "text-muted-foreground",
        )}
        aria-hidden
      />
      {label}
    </button>
  );
}

/**
 * Reports whether edits have reached the server.
 *
 * `idle` renders nothing — an indicator that is always present is noise. A failure stays
 * visible, because a silent save failure is how a user loses work without knowing.
 */
function SaveIndicator({ state }: { state: SaveState }) {
  const t = useT();

  if (state === "idle") return null;

  if (state === "error") {
    return (
      <span className="canvas-fade-in flex items-center gap-1 px-1.5 text-[0.6875rem] text-destructive">
        <CloudAlert className="size-3.5" aria-hidden />
        {t("canvas.saveFailed")}
      </span>
    );
  }

  return (
    <span className="canvas-fade-in flex items-center gap-1 px-1.5 text-[0.6875rem] text-muted-foreground">
      {state === "saving" ? (
        <Loader2 className="size-3 animate-spin" aria-hidden />
      ) : (
        <Check className="size-3" aria-hidden />
      )}
      {state === "saving" ? t("canvas.saving") : t("canvas.saved")}
    </span>
  );
}
