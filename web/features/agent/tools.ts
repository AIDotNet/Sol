"use client";

import type { NodeChange } from "@xyflow/react";
import { useAiStore } from "@/features/ai/store";
import { getAgentCanvasControls } from "@/features/agent/canvas-control";
import {
  cancelRun as cancelCanvasRun,
  retryRun,
  runImageNode,
  runVideoNode,
} from "@/features/canvas/execution";
import { applyImageEdit } from "@/features/canvas/image-edit";
import {
  buildInpaintMask,
  cropImage,
  expandCanvas,
  flipImage,
  rotateImage,
  upscaleImage,
} from "@/features/canvas/image-ops";
import { useCanvasStore, type CanvasNode } from "@/features/canvas/store";
import {
  ASPECT_RATIOS,
  IMAGE_OUTPUT_FORMATS,
  IMAGE_RESPONSE_FORMATS,
  NODE_DEFAULT_SIZE,
  type CanvasNodeData,
  type NodeKind,
} from "@/features/canvas/types";
import { wouldCreateCycle } from "@/features/canvas/upstream";
import type { AgentToolCallEnvelope } from "@/features/agent/types";

export const AGENT_CANVAS_TOOL_NAMES = [
  "read_canvas",
  "get_node_status",
  "subscribe_node",
  "wait_for_node_event",
  "create_node",
  "update_node",
  "connect_nodes",
  "select_nodes",
  "duplicate_nodes",
  "delete_nodes",
  "disconnect_nodes",
  "move_nodes",
  "resize_node",
  "run_node",
  "retry_node",
  "cancel_node",
  "manage_canvas",
  "edit_image",
] as const;

const NODE_KINDS = new Set<NodeKind>(["text", "image", "video", "imageGen", "videoGen"]);

const DATA_FIELDS: Record<NodeKind, ReadonlySet<string>> = {
  text: new Set(["text", "fontScale"]),
  image: new Set(["assetUrl", "mediaType", "prompt", "providerId", "modelId"]),
  video: new Set(["assetUrl", "posterUrl", "mediaType", "prompt", "providerId", "modelId", "jobId"]),
  imageGen: new Set([
    "providerId", "modelId", "aspect", "size", "quality", "outputFormat", "responseFormat", "count", "seed",
  ]),
  videoGen: new Set([
    "providerId", "modelId", "aspect", "resolution", "duration", "fps", "seed", "watermark", "generateAudio", "cameraFixed", "seedanceInputMode",
  ]),
};

export interface CanvasToolExecutionResult {
  json: string;
  isError: boolean;
}

export async function executeCanvasTool(
  call: AgentToolCallEnvelope,
): Promise<CanvasToolExecutionResult> {
  try {
    const input = parseObject(call.inputJson);
    const result = await execute(call.toolName, input);
    const json = JSON.stringify({ status: "ok", ...result });
    if (new TextEncoder().encode(json).byteLength > 256 * 1024) {
      throw new Error("canvas_tool_result_too_large");
    }
    return { json, isError: false };
  } catch (error) {
    const detail = error instanceof Error ? error.message : "canvas_tool_failed";
    return {
      json: JSON.stringify({ status: "error", error: detail }),
      isError: true,
    };
  }
}

async function execute(
  toolName: string,
  input: Record<string, unknown>,
): Promise<object> {
  const state = useCanvasStore.getState();

  switch (toolName) {
    case "read_canvas":
      return {
        revision: state.revision,
        nodes: state.nodes.map((node) => ({
          id: node.id,
          type: node.type,
          position: node.position,
          width: node.width,
          height: node.height,
          selected: Boolean(node.selected),
          data: node.data,
        })),
        edges: state.edges.map((edge) => ({
          id: edge.id,
          source: edge.source,
          target: edge.target,
        })),
      };

    case "get_node_status": {
      const ids = stringArray(input.nodeIds, "nodeIds", 100);
      return {
        nodes: ids.map((id) => nodeStatus(id)),
      };
    }

    case "subscribe_node": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      return { subscriptionId: `node:${nodeId}`, node: nodeStatus(nodeId) };
    }

    case "wait_for_node_event": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      const statuses = input.statuses === undefined
        ? ["succeeded", "failed", "cancelled", "interrupted"]
        : stringArray(input.statuses, "statuses", 7);
      const timeoutMs = typeof input.timeoutMs === "number"
        ? Math.max(1_000, Math.min(600_000, Math.floor(input.timeoutMs)))
        : 600_000;
      return await waitForNode(nodeId, new Set(statuses), timeoutMs);
    }

    case "create_node": {
      const kind = requiredString(input.kind, "kind") as NodeKind;
      if (!NODE_KINDS.has(kind)) throw new Error("invalid_node_kind");
      const position = readPosition(input.position, state.nodes, kind);
      const data = validateData(kind, input.data ?? (kind === "text" ? { text: "" } : {}));
      const nodeId = state.addNode(kind, position, data);
      return { nodeId };
    }

    case "update_node": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      const node = state.nodes.find((candidate) => candidate.id === nodeId);
      if (!node || !NODE_KINDS.has(node.type as NodeKind)) throw new Error("node_not_found");
      const data = validateData(node.type as NodeKind, input.data);
      if (Object.keys(data).length === 0) throw new Error("empty_node_patch");
      state.updateNodeData(nodeId, data);
      return { nodeId };
    }

    case "connect_nodes": {
      const sourceId = requiredString(input.sourceId, "sourceId");
      const targetId = requiredString(input.targetId, "targetId");
      const ids = new Set(state.nodes.map((node) => node.id));
      if (!ids.has(sourceId) || !ids.has(targetId)) throw new Error("node_not_found");
      if (state.edges.some((edge) => edge.source === sourceId && edge.target === targetId)) {
        throw new Error("edge_already_exists");
      }
      if (wouldCreateCycle(sourceId, targetId, state.edges)) throw new Error("connection_would_create_cycle");
      state.onConnect({
        source: sourceId,
        target: targetId,
        sourceHandle: null,
        targetHandle: null,
      });
      const edge = useCanvasStore.getState().edges.find(
        (candidate) => candidate.source === sourceId && candidate.target === targetId,
      );
      return { edgeId: edge?.id ?? null, sourceId, targetId };
    }

    case "select_nodes": {
      const nodeIds = stringArray(input.nodeIds, "nodeIds", 500);
      const selected = new Set(nodeIds);
      const known = new Set(state.nodes.map((node) => node.id));
      if (nodeIds.some((id) => !known.has(id))) throw new Error("node_not_found");
      const changes: NodeChange<CanvasNode>[] = state.nodes.map((node) => ({
        type: "select",
        id: node.id,
        selected: selected.has(node.id),
      }));
      state.onNodesChange(changes);
      return { nodeIds };
    }

    case "duplicate_nodes": {
      const nodeIds = stringArray(input.nodeIds, "nodeIds", 100);
      const known = new Set(state.nodes.map((node) => node.id));
      if (nodeIds.some((id) => !known.has(id))) throw new Error("node_not_found");
      const before = new Set(state.nodes.map((node) => node.id));
      nodeIds.forEach((id) => useCanvasStore.getState().duplicateNode(id));
      const createdNodeIds = useCanvasStore
        .getState()
        .nodes.filter((node) => !before.has(node.id))
        .map((node) => node.id);
      return { createdNodeIds };
    }

    case "delete_nodes": {
      const nodeIds = stringArray(input.nodeIds, "nodeIds", 100);
      const known = new Set(state.nodes.map((node) => node.id));
      if (nodeIds.some((id) => !known.has(id))) throw new Error("node_not_found");
      state.removeNodes(nodeIds);
      return { deletedNodeIds: nodeIds };
    }

    case "disconnect_nodes": {
      const edgeIds = input.edgeIds === undefined ? [] : stringArray(input.edgeIds, "edgeIds", 100);
      const sourceId = input.sourceId === undefined ? null : requiredString(input.sourceId, "sourceId");
      const targetId = input.targetId === undefined ? null : requiredString(input.targetId, "targetId");
      const selected = state.edges.filter(
        (edge) => edgeIds.includes(edge.id) || (sourceId && targetId && edge.source === sourceId && edge.target === targetId),
      );
      if (selected.length === 0) throw new Error("edge_not_found");
      state.onEdgesChange(selected.map((edge) => ({ type: "remove", id: edge.id })));
      return { disconnectedEdgeIds: selected.map((edge) => edge.id) };
    }

    case "move_nodes": {
      if (!Array.isArray(input.moves) || input.moves.length === 0 || input.moves.length > 100) {
        throw new Error("invalid_moves");
      }
      const known = new Set(state.nodes.map((node) => node.id));
      const movedNodeIds: string[] = [];
      const changes: NodeChange<CanvasNode>[] = input.moves.map((move) => {
        if (!isObject(move)) throw new Error("invalid_move");
        const nodeId = requiredString(move.nodeId, "nodeId");
        if (!known.has(nodeId) || typeof move.x !== "number" || typeof move.y !== "number") {
          throw new Error("invalid_move");
        }
        movedNodeIds.push(nodeId);
        return {
          type: "position",
          id: nodeId,
          position: { x: move.x, y: move.y },
          dragging: false,
        };
      });
      state.onNodesChange(changes);
      return { movedNodeIds };
    }

    case "resize_node": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      if (!state.nodes.some((node) => node.id === nodeId)) throw new Error("node_not_found");
      if (typeof input.width !== "number" || typeof input.height !== "number") {
        throw new Error("invalid_size");
      }
      const width = Math.max(160, Math.min(4096, input.width));
      const height = Math.max(100, Math.min(4096, input.height));
      state.onNodesChange([
        { type: "dimensions", id: nodeId, dimensions: { width, height }, resizing: false },
      ]);
      return { nodeId, width, height };
    }

    case "run_node": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      const node = state.nodes.find((candidate) => candidate.id === nodeId);
      if (!node || (node.type !== "imageGen" && node.type !== "videoGen")) {
        throw new Error("generation_node_not_found");
      }
      const before = new Set(state.nodes.map((candidate) => candidate.id));
      if (node.type === "imageGen") await runImageNode(nodeId, state.nodes, state.edges);
      else await runVideoNode(nodeId, state.nodes, state.edges);
      return generationOutputs(before);
    }

    case "retry_node": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      if (!state.nodes.some((node) => node.id === nodeId)) throw new Error("node_not_found");
      const before = new Set(state.nodes.map((node) => node.id));
      before.delete(nodeId);
      await retryRun(nodeId, state.nodes, state.edges);
      return generationOutputs(before);
    }

    case "cancel_node": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      const node = state.nodes.find((candidate) => candidate.id === nodeId);
      const status = (node?.data as { execution?: { status?: string } } | undefined)?.execution?.status;
      if (!node || (status !== "queued" && status !== "running")) throw new Error("node_not_running");
      cancelCanvasRun(nodeId);
      return { nodeId, status: "cancelled" };
    }

    case "manage_canvas": {
      const action = requiredString(input.action, "action");
      switch (action) {
        case "undo":
          state.undo();
          return { action };
        case "redo":
          state.redo();
          return { action };
        case "clear": {
          const nodeIds = state.nodes.map((node) => node.id);
          state.removeNodes(nodeIds);
          return { action, deletedNodeIds: nodeIds };
        }
        case "auto_layout": {
          state.autoLayout();
          return { action, nodeCount: useCanvasStore.getState().nodes.length };
        }
        case "fit_view":
        case "zoom_in":
        case "zoom_out": {
          const controls = getAgentCanvasControls();
          if (!controls) throw new Error("canvas_viewport_unavailable");
          const ok = action === "fit_view"
            ? await controls.fitView()
            : action === "zoom_in"
              ? await controls.zoomIn()
              : await controls.zoomOut();
          return { action, ok };
        }
        default:
          throw new Error("invalid_canvas_action");
      }
    }

    case "edit_image": {
      const nodeId = requiredString(input.nodeId, "nodeId");
      const node = state.nodes.find((candidate) => candidate.id === nodeId);
      const sourceUrl = (node?.data as { assetUrl?: unknown } | undefined)?.assetUrl;
      if (!node || node.type !== "image" || typeof sourceUrl !== "string" || !sourceUrl) {
        throw new Error("editable_image_not_found");
      }
      if (/^(?:data|blob):/i.test(sourceUrl)) throw new Error("ephemeral_image_not_editable");
      const operation = requiredString(input.operation, "operation");

      switch (operation) {
        case "rotate": {
          if (!Number.isInteger(input.quarterTurns)) throw new Error("invalid_quarter_turns");
          return await applyImageEdit(
            nodeId, sourceUrl,
            (url) => rotateImage(url, Number(input.quarterTurns)),
            "rotated",
          );
        }
        case "flip": {
          if (input.axis !== "horizontal" && input.axis !== "vertical") throw new Error("invalid_axis");
          return await applyImageEdit(nodeId, sourceUrl, (url) => flipImage(url, input.axis as "horizontal" | "vertical"), "flipped");
        }
        case "upscale": {
          if (typeof input.factor !== "number" || input.factor < 1 || input.factor > 8) {
            throw new Error("invalid_upscale_factor");
          }
          return await applyImageEdit(nodeId, sourceUrl, (url) => upscaleImage(url, input.factor as number), "upscaled");
        }
        case "crop": {
          const crop = normalizedRect(input.crop, "crop");
          if (crop.x + crop.width > 1 || crop.y + crop.height > 1) throw new Error("crop_out_of_bounds");
          return await applyImageEdit(nodeId, sourceUrl, (url) => cropImage(url, crop), "cropped");
        }
        case "expand": {
          const insets = nonNegativeInsets(input.insets);
          return await applyImageEdit(nodeId, sourceUrl, (url) => expandCanvas(url, insets), "expanded");
        }
        case "mask": {
          const strokes = inpaintStrokes(input.strokes);
          return await applyImageEdit(
            nodeId,
            sourceUrl,
            (url) => buildInpaintMask(url, strokes),
            "inpaint-mask",
            { role: "inpaint-mask" },
          );
        }
        case "variation":
          return await runModelImageEdit(nodeId, input, []);
        case "outpaint": {
          const insets = nonNegativeInsets(input.insets);
          const expanded = await applyImageEdit(
            nodeId, sourceUrl, (url) => expandCanvas(url, insets), "outpaint-source",
          );
          return await runModelImageEdit(expanded.nodeId, input, []);
        }
        case "inpaint": {
          const strokes = inpaintStrokes(input.strokes);
          const mask = await applyImageEdit(
            nodeId,
            sourceUrl,
            (url) => buildInpaintMask(url, strokes),
            "inpaint-mask",
            { role: "inpaint-mask" },
          );
          return await runModelImageEdit(nodeId, input, [mask.nodeId]);
        }
        default:
          throw new Error("invalid_image_edit_operation");
      }
    }

    default:
      throw new Error(`unimplemented_client_tool:${toolName}`);
  }
}

function nodeStatus(nodeId: string): object {
  const node = useCanvasStore.getState().nodes.find((candidate) => candidate.id === nodeId);
  if (!node) return { nodeId, exists: false };
  const data = node.data as Record<string, unknown>;
  return {
    nodeId,
    exists: true,
    type: node.type,
    execution: data.execution ?? null,
    assetUrl: data.assetUrl ?? null,
    jobId: data.jobId ?? null,
  };
}

function waitForNode(nodeId: string, statuses: Set<string>, timeoutMs: number): Promise<object> {
  return new Promise((resolve) => {
    let lastExecution = JSON.stringify(
      (useCanvasStore.getState().nodes.find((node) => node.id === nodeId)?.data as {
        execution?: unknown;
      } | undefined)?.execution ?? null,
    );
    let settled = false;
    const finish = (value: object) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timeout);
      unsubscribe();
      resolve(value);
    };
    const unsubscribe = useCanvasStore.subscribe((state) => {
      const node = state.nodes.find((candidate) => candidate.id === nodeId);
      if (!node) {
        finish({ event: "removed", node: nodeStatus(nodeId) });
        return;
      }
      const execution = (node.data as { execution?: { status?: string } }).execution;
      const serialized = JSON.stringify(execution ?? null);
      if (execution?.status && statuses.has(execution.status)) {
        finish({ event: "status", status: execution.status, node: nodeStatus(nodeId) });
      } else if (serialized !== lastExecution) {
        lastExecution = serialized;
        finish({ event: "changed", node: nodeStatus(nodeId) });
      }
    });
    const current = useCanvasStore.getState().nodes.find((node) => node.id === nodeId);
    const currentStatus = (current?.data as { execution?: { status?: string } } | undefined)
      ?.execution?.status;
    const timeout = window.setTimeout(
      () => finish({ event: "timeout", node: nodeStatus(nodeId) }),
      timeoutMs,
    );
    if (!current) finish({ event: "removed", node: nodeStatus(nodeId) });
    else if (currentStatus && statuses.has(currentStatus)) {
      finish({ event: "status", status: currentStatus, node: nodeStatus(nodeId) });
    }
  });
}

function generationOutputs(before: Set<string>): object {
  const outputs = useCanvasStore
    .getState()
    .nodes.filter((node) => !before.has(node.id) && (node.type === "image" || node.type === "video"))
    .map((node) => ({
      nodeId: node.id,
      type: node.type,
      assetUrl: (node.data as { assetUrl?: string }).assetUrl ?? null,
      jobId: (node.data as { jobId?: string }).jobId ?? null,
      execution: (node.data as { execution?: unknown }).execution ?? null,
    }));
  return { outputs };
}

async function runModelImageEdit(
  sourceNodeId: string,
  input: Record<string, unknown>,
  additionalImageNodeIds: string[],
): Promise<object> {
  const providerId = requiredString(input.providerId, "providerId");
  const modelId = requiredString(input.modelId, "modelId");
  const prompt = requiredString(input.prompt, "prompt");
  const provider = useAiStore.getState().providers.find((candidate) => candidate.id === providerId);
  const model = provider?.models.find((candidate) => candidate.id === modelId);
  if (!provider?.enabled || !model?.enabled || model.category !== "image") {
    throw new Error("image_model_unavailable");
  }

  const store = useCanvasStore.getState();
  const source = store.nodes.find((node) => node.id === sourceNodeId);
  if (!source) throw new Error("node_not_found");
  const sourceWidth = source.width ?? NODE_DEFAULT_SIZE.image.width;
  const promptNodeId = store.addNode(
    "text",
    { x: source.position.x, y: source.position.y + (source.height ?? 320) + 40 },
    { text: prompt },
  );
  const generationNodeId = store.addNode(
    "imageGen",
    { x: source.position.x + sourceWidth + 80, y: source.position.y },
    { providerId, modelId, count: 1 },
  );
  for (const inputNodeId of [sourceNodeId, ...additionalImageNodeIds, promptNodeId]) {
    useCanvasStore.getState().onConnect({
      source: inputNodeId,
      target: generationNodeId,
      sourceHandle: null,
      targetHandle: null,
    });
  }

  const before = new Set(useCanvasStore.getState().nodes.map((node) => node.id));
  const snapshot = useCanvasStore.getState();
  await runImageNode(generationNodeId, snapshot.nodes, snapshot.edges);

  const outputs = useCanvasStore
    .getState()
    .nodes.filter((node) => !before.has(node.id) && node.type === "image")
    .map((node) => ({
      nodeId: node.id,
      assetUrl: (node.data as { assetUrl?: string }).assetUrl ?? null,
      error: (node.data as { execution?: { error?: string } }).execution?.error ?? null,
    }));
  if (outputs.length === 0 || outputs.every((output) => !output.assetUrl)) {
    throw new Error(outputs.find((output) => output.error)?.error ?? "image_edit_generation_failed");
  }
  return { promptNodeId, generationNodeId, outputs };
}

function parseObject(json: string): Record<string, unknown> {
  const value: unknown = JSON.parse(json);
  if (!isObject(value)) throw new Error("tool_input_must_be_an_object");
  return value;
}

function validateData(kind: NodeKind, value: unknown): Partial<CanvasNodeData> {
  if (!isObject(value)) throw new Error("node_data_must_be_an_object");
  const allowed = DATA_FIELDS[kind];
  for (const key of Object.keys(value)) {
    if (!allowed.has(key)) throw new Error(`field_not_allowed_for_${kind}:${key}`);
  }

  const data = { ...value };
  for (const key of ["assetUrl", "posterUrl"] as const) {
    if (key in data && typeof data[key] === "string" && /^(?:data|blob):/i.test(data[key])) {
      throw new Error(`ephemeral_url_not_allowed:${key}`);
    }
  }
  if ("aspect" in data && !ASPECT_RATIOS.includes(data.aspect as never)) throw new Error("invalid_aspect");
  if ("outputFormat" in data && !IMAGE_OUTPUT_FORMATS.includes(data.outputFormat as never)) {
    throw new Error("invalid_output_format");
  }
  if ("responseFormat" in data && !IMAGE_RESPONSE_FORMATS.includes(data.responseFormat as never)) {
    throw new Error("invalid_response_format");
  }
  if ("count" in data && (!Number.isInteger(data.count) || Number(data.count) < 1 || Number(data.count) > 4)) {
    throw new Error("invalid_count");
  }
  if ("fontScale" in data && (typeof data.fontScale !== "number" || data.fontScale <= 0 || data.fontScale > 4)) {
    throw new Error("invalid_font_scale");
  }

  return data as Partial<CanvasNodeData>;
}

function readPosition(
  value: unknown,
  nodes: CanvasNode[],
  kind: NodeKind,
): { x: number; y: number } {
  if (value !== undefined) {
    if (!isObject(value) || typeof value.x !== "number" || typeof value.y !== "number") {
      throw new Error("invalid_position");
    }
    return { x: value.x, y: value.y };
  }

  if (nodes.length === 0) return { x: 0, y: 0 };
  const rightmost = nodes.reduce((current, node) =>
    node.position.x + (node.width ?? 0) > current.position.x + (current.width ?? 0) ? node : current,
  );
  return {
    x: rightmost.position.x + (rightmost.width ?? NODE_DEFAULT_SIZE[(rightmost.type ?? "text") as NodeKind].width) + 60,
    y: rightmost.position.y + Math.max(0, (rightmost.height ?? 0) - NODE_DEFAULT_SIZE[kind].height) / 2,
  };
}

function normalizedRect(
  value: unknown,
  field: string,
): { x: number; y: number; width: number; height: number } {
  if (!isObject(value)) throw new Error(`invalid_${field}`);
  const values = [value.x, value.y, value.width, value.height];
  if (values.some((item) => typeof item !== "number" || item < 0 || item > 1)
      || Number(value.width) === 0 || Number(value.height) === 0) {
    throw new Error(`invalid_${field}`);
  }
  return {
    x: value.x as number,
    y: value.y as number,
    width: value.width as number,
    height: value.height as number,
  };
}

function nonNegativeInsets(
  value: unknown,
): { top: number; right: number; bottom: number; left: number } {
  if (!isObject(value)) throw new Error("invalid_insets");
  const values = [value.top, value.right, value.bottom, value.left];
  if (values.some((item) => typeof item !== "number" || item < 0 || item > 2)) {
    throw new Error("invalid_insets");
  }
  return {
    top: value.top as number,
    right: value.right as number,
    bottom: value.bottom as number,
    left: value.left as number,
  };
}

function inpaintStrokes(value: unknown): Array<{
  points: Array<{ x: number; y: number }>;
  radius: number;
}> {
  if (!Array.isArray(value) || value.length === 0 || value.length > 100) {
    throw new Error("invalid_strokes");
  }
  return value.map((stroke) => {
    if (!isObject(stroke)
        || typeof stroke.radius !== "number"
        || stroke.radius <= 0
        || stroke.radius > 0.5
        || !Array.isArray(stroke.points)
        || stroke.points.length === 0
        || stroke.points.length > 2_000) {
      throw new Error("invalid_stroke");
    }
    const points = stroke.points.map((point) => {
      if (!isObject(point)
          || typeof point.x !== "number"
          || typeof point.y !== "number"
          || point.x < 0 || point.x > 1
          || point.y < 0 || point.y > 1) {
        throw new Error("invalid_stroke_point");
      }
      return { x: point.x, y: point.y };
    });
    return { points, radius: stroke.radius };
  });
}

function requiredString(value: unknown, field: string): string {
  if (typeof value !== "string" || value.trim().length === 0) throw new Error(`invalid_${field}`);
  return value;
}

function stringArray(value: unknown, field: string, max: number): string[] {
  if (!Array.isArray(value) || value.length > max || value.some((item) => typeof item !== "string")) {
    throw new Error(`invalid_${field}`);
  }
  return value as string[];
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
