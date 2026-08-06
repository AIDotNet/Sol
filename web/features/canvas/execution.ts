"use client";

import type { CanvasEdge, CanvasNode } from "@/features/canvas/store";
import { newRunId, useCanvasStore } from "@/features/canvas/store";
import { collectUpstream } from "@/features/canvas/upstream";
import {
  ASPECT_TO_SIZE,
  DEFAULT_SEEDANCE_INPUT_MODE,
  DEFAULT_IMAGE_OUTPUT_FORMAT,
  DEFAULT_IMAGE_RESPONSE_FORMAT,
  resolveVideoDuration,
  type ImageGenNodeData,
  type NodeExecution,
  type VideoGenNodeData,
} from "@/features/canvas/types";
import { useAiStore } from "@/features/ai/store";

/**
 * Runs a generation node.
 *
 * Orchestration only: it gathers upstream inputs, calls the backend, and writes results back
 * onto the graph. Provider protocols live server-side — the browser never sees an API key and
 * never talks to a vendor directly.
 *
 * **A run belongs to the nodes it creates, not to the node that started it.** Every click on
 * Generate spawns its own output nodes and its own run id, and the spinner, the progress, the
 * Stop button and the cancellation all live on those nodes. The config node keeps no run state,
 * which is what lets a user fire a second generation while the first is still rendering — the
 * two never contend over one status field, and a slow first result cannot be dropped because a
 * later run replaced the id it was checked against.
 *
 * The config node still shows one thing: the error from a run that never got far enough to
 * produce a node (nothing connected upstream, model gone, request refused).
 */

interface GenerationRequest {
  providerId: string;
  modelKey: string;
  prompt: string;
  /** Reference image URLs, resolved server-side. */
  images: string[];
}

async function postJson<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal,
  });

  if (!response.ok) {
    let detail = `Request failed (${response.status})`;
    try {
      const parsed = (await response.json()) as { error?: string; details?: string[] };
      detail = parsed.details?.[0] ?? parsed.error ?? detail;
    } catch {
      // Keep the status-derived message.
    }
    throw new Error(detail);
  }

  return (await response.json()) as T;
}

/**
 * Abort controllers for in-flight runs, keyed by run id.
 *
 * Keyed by run rather than by node because one config node can have several runs going at once —
 * keying by node meant a second Generate overwrote the first entry, and the first request could
 * then never be aborted.
 *
 * Held outside the store deliberately: an AbortController is not serializable, so persisting it
 * would break the save path, and it has no meaning after a reload anyway.
 */
const inFlight = new Map<string, AbortController>();

function executionOf(node: CanvasNode | undefined): NodeExecution | undefined {
  return (node?.data as { execution?: NodeExecution } | undefined)?.execution;
}

/** The nodes carrying this run, in canvas order. Empty once the user has deleted them all. */
function runNodes(runId: string): CanvasNode[] {
  return useCanvasStore
    .getState()
    .nodes.filter((node) => executionOf(node)?.runId === runId);
}

/**
 * True while the run still has somewhere to put a result.
 *
 * Replaces the old check against the config node's run id. A result is dropped when the user
 * stopped the run or deleted the nodes waiting for it — not because they started another one.
 */
function runAlive(runId: string): boolean {
  return runNodes(runId).some((node) => executionOf(node)?.status !== "cancelled");
}

/**
 * Drops output nodes a run left empty.
 *
 * A placeholder earns its place on the canvas by becoming an image or a video. One that reaches
 * the end of its run with neither an asset nor a server job is noise — worse than noise, since
 * its spinner has nothing left to resolve it.
 *
 * Not an undo step: the user did not create these and would not expect Ctrl+Z to bring back a
 * box that can never fill in.
 */
function discardPlaceholders(ids: string[]): void {
  const store = useCanvasStore.getState();

  const empty = ids.filter((id) => {
    const node = store.nodes.find((candidate) => candidate.id === id);
    if (!node) return false;

    const data = node.data as { assetUrl?: string; jobId?: string };
    return !data.assetUrl && !data.jobId;
  });

  if (empty.length > 0) {
    store.removeNodes(empty, { history: false });
  }
}

/**
 * Stops the run a node belongs to.
 *
 * Called from the node that is generating, which is where the Stop button lives — the whole run
 * goes, not just the node that was clicked, because one request produces all of a run's images.
 *
 * For images this aborts the request. For video the work is server-side, so the local poller is
 * detached and the job is asked to stop; the node stops claiming to be running either way, which
 * is the part the user cares about.
 */
export function cancelRun(nodeId: string): void {
  const store = useCanvasStore.getState();

  const runId = executionOf(store.nodes.find((candidate) => candidate.id === nodeId))?.runId;
  if (!runId) return;

  inFlight.get(runId)?.abort();
  inFlight.delete(runId);

  const members = runNodes(runId);

  for (const member of members) {
    const jobId = (member.data as { jobId?: string }).jobId;

    if (jobId) {
      // Best effort: the poller already stopped, so a failure here only leaves an orphan job
      // upstream, which the stale sweep will retire.
      void fetch(`/api/v1/ai/videos/${jobId}`, { method: "DELETE", credentials: "include" }).catch(
        () => {},
      );
    }

    // Left alone the node spins forever, because the poller that would have resolved it just
    // detached.
    store.patchExecution(member.id, "cancelled");
  }

  // Stopping a run that had produced nothing should leave the canvas as it was, rather than
  // littering it with empty boxes wearing a "stopped" banner. A video whose job was accepted
  // keeps its node: something did happen upstream, and that is worth showing.
  discardPlaceholders(members.map((member) => member.id));
}

function resolveModelKey(providerId: string, modelId: string): string | null {
  const provider = useAiStore
    .getState()
    .providers.find((candidate) => candidate.id === providerId);

  return provider?.models.find((candidate) => candidate.id === modelId)?.modelKey ?? null;
}

/**
 * Where the next run's output should start stacking.
 *
 * `spawnOutput` places a node relative to its source, so without an offset a second run lands at
 * exactly the coordinates the first one occupies and hides it. Counting what the config node
 * already feeds puts each new run below the last, and self-corrects: delete the outputs and the
 * next run starts back at the top.
 */
function stackBase(nodeId: string, edges: CanvasEdge[]): number {
  return edges.filter((edge) => edge.source === nodeId).length;
}

/** Reports a failure that happened before the run had a node of its own to report it on. */
function failConfigNode(nodeId: string, error: string): void {
  useCanvasStore.getState().setExecution(nodeId, {
    runId: newRunId(),
    status: "failed",
    error,
    finishedAt: Date.now(),
  });
}

/**
 * Reports a failed request on the nodes the run created.
 *
 * The error belongs where the user is looking: the box that was going to hold the image, not the
 * config node one step upstream — which, with two runs going at once, cannot even say which of
 * them failed.
 *
 * One request produces a whole run, so it gets one report. The first node keeps it and the
 * spares go, rather than lining up four identical red boxes for a single refusal.
 *
 * Falls back to the config node only when the run never got a node at all.
 */
function reportRunFailure(placeholders: string[], configNodeId: string, error: string): void {
  const store = useCanvasStore.getState();

  const [keep, ...spares] = placeholders.filter((id) =>
    store.nodes.some((candidate) => candidate.id === id),
  );

  if (!keep) {
    failConfigNode(configNodeId, error);
    return;
  }

  store.patchExecution(keep, "failed", { error });
  discardPlaceholders(spares);
}

/**
 * Re-runs the generation behind a node, and takes the failed node with it.
 *
 * Reached from the Retry button on a node that failed or was interrupted. The node it starts
 * from is whichever generation node feeds this one, so a retry means the same settings, not a
 * remembered copy of them — the user may well have changed something before pressing it.
 */
export async function retryRun(
  nodeId: string,
  nodes: CanvasNode[],
  edges: CanvasEdge[],
): Promise<void> {
  const config = edges
    .filter((edge) => edge.target === nodeId)
    .map((edge) => nodes.find((candidate) => candidate.id === edge.source))
    .find((candidate) => candidate?.type === "imageGen" || candidate?.type === "videoGen");

  if (!config) return;

  // Removed first, so the replacement takes its place in the stack rather than landing below a
  // node that is on its way out.
  useCanvasStore.getState().removeNodes([nodeId], { history: false });

  const { nodes: fresh, edges: freshEdges } = useCanvasStore.getState();

  if (config.type === "imageGen") {
    await runImageNode(config.id, fresh, freshEdges);
  } else {
    await runVideoNode(config.id, fresh, freshEdges);
  }
}

export async function runImageNode(
  nodeId: string,
  nodes: CanvasNode[],
  edges: CanvasEdge[],
): Promise<void> {
  const store = useCanvasStore.getState();
  const node = nodes.find((candidate) => candidate.id === nodeId);
  if (!node) return;

  const data = node.data as ImageGenNodeData;
  if (!data.providerId || !data.modelId) return;

  // Clears the banner from whatever failed last, so a fresh attempt does not start out wearing
  // an old error.
  store.setExecution(nodeId, undefined);

  const { prompt, images, maskUrl } = collectUpstream(nodeId, nodes, edges);

  if (!prompt && images.length === 0) {
    failConfigNode(nodeId, "Connect a text or image node first.");
    return;
  }

  const modelKey = resolveModelKey(data.providerId, data.modelId);
  if (!modelKey) {
    failConfigNode(nodeId, "The selected model no longer exists.");
    return;
  }

  const runId = newRunId();
  const count = Math.min(4, Math.max(1, data.count ?? 1));
  const size = data.size ?? (data.aspect ? ASPECT_TO_SIZE[data.aspect] : undefined);

  // The output nodes go up before the request is sent, not after it resolves. An image takes
  // tens of seconds, and spawning only on success meant the canvas showed nothing at all for
  // that whole stretch — the click read as having done nothing.
  const base = stackBase(nodeId, edges);
  const placeholders = Array.from({ length: count }, (_, index) =>
    store.spawnOutput(
      nodeId,
      "image",
      {
        prompt,
        providerId: data.providerId,
        modelId: data.modelId,
        execution: { runId, status: "running", startedAt: Date.now() },
      },
      base + index,
    ),
  ).filter((id): id is string => id !== null);

  const controller = new AbortController();
  inFlight.set(runId, controller);

  try {
    const result = await postJson<{ assets: Array<{ url: string; mediaType: string }> }>(
      "/api/v1/ai/images",
      {
        providerId: data.providerId,
        modelKey,
        prompt,
        images: images.map((image) => image.url),
        maskUrl,
        size,
        quality: data.quality === "auto" ? undefined : data.quality,
        outputFormat: data.outputFormat ?? DEFAULT_IMAGE_OUTPUT_FORMAT,
        responseFormat: data.responseFormat ?? DEFAULT_IMAGE_RESPONSE_FORMAT,
        count,
        seed: data.seed,
      } satisfies GenerationRequest & Record<string, unknown>,
      controller.signal,
    );

    if (!runAlive(runId)) return;

    // Read fresh: the user may have deleted some of the placeholders while the request was out,
    // and writing to an id that is gone would drop the image on the floor.
    const live = new Set(useCanvasStore.getState().nodes.map((candidate) => candidate.id));

    result.assets.forEach((asset, index) => {
      const filled = {
        assetUrl: asset.url,
        mediaType: asset.mediaType,
        prompt,
        providerId: data.providerId,
        modelId: data.modelId,
        // Clears the spinner the placeholder was created with.
        execution: undefined,
      };

      const placeholder = placeholders[index];

      // Filling the placeholder in rather than replacing it means the result appears in the box
      // the user has been watching, and keeps whatever they moved or resized it to. `history:
      // false` for the same reason the video poller uses it: this is not an edit they made, and
      // merging it into one they did would make Ctrl+Z revert both.
      if (placeholder && live.has(placeholder)) {
        store.updateNodeData(placeholder, filled, { history: false });
        return;
      }

      // No box waiting for it: more assets came back than were asked for, or the one meant for
      // this asset was deleted mid-run. Either way it has been generated and paid for, so give
      // it somewhere to land rather than dropping it.
      store.spawnOutput(nodeId, "image", filled, base + index);
    });

    // Fewer assets than placeholders — a partial result, or a provider that quietly capped the
    // count. The spares have nothing coming.
    discardPlaceholders(placeholders.slice(result.assets.length));
  } catch (error) {
    // An abort is a user action, not a failure — cancelRun already cleared the placeholders.
    if (error instanceof DOMException && error.name === "AbortError") return;
    if (!runAlive(runId)) return;

    reportRunFailure(
      placeholders,
      nodeId,
      error instanceof Error ? error.message : "Generation failed",
    );
  } finally {
    inFlight.delete(runId);
  }
}

/** How often to poll a video job. Generation runs for minutes, so a tight loop buys nothing. */
const VIDEO_POLL_INTERVAL_MS = 4000;
/** Gives up after roughly 20 minutes so an abandoned job cannot poll forever. */
const VIDEO_POLL_LIMIT = 300;

export async function runVideoNode(
  nodeId: string,
  nodes: CanvasNode[],
  edges: CanvasEdge[],
): Promise<void> {
  const store = useCanvasStore.getState();
  const node = nodes.find((candidate) => candidate.id === nodeId);
  if (!node) return;

  const data = node.data as VideoGenNodeData;
  if (!data.providerId || !data.modelId) return;

  const duration = resolveVideoDuration(data.duration);
  if (duration === null) {
    failConfigNode(nodeId, "Duration must be a whole number from 1 to 60 seconds.");
    return;
  }

  store.setExecution(nodeId, undefined);

  const { prompt, images } = collectUpstream(nodeId, nodes, edges);

  if (!prompt && images.length === 0) {
    failConfigNode(nodeId, "Connect a text or image node first.");
    return;
  }

  const modelKey = resolveModelKey(data.providerId, data.modelId);
  if (!modelKey) {
    failConfigNode(nodeId, "The selected model no longer exists.");
    return;
  }

  const runId = newRunId();

  // The output node goes up before the request, not after acceptance. Waiting meant the canvas
  // stayed unchanged while the vendor took its time saying yes, which is exactly the stretch the
  // user is least sure anything happened. It picks up the job id below, which is what lets a
  // reload resume polling instead of losing a video that is still rendering server-side.
  const outputId = store.spawnOutput(
    nodeId,
    "video",
    {
      prompt,
      providerId: data.providerId,
      modelId: data.modelId,
      execution: { runId, status: "running", startedAt: Date.now() },
    },
    stackBase(nodeId, edges),
  );

  // Registered so Stop can abort the acceptance request itself. Once the vendor has taken the
  // job the controller is useless — from there cancellation means DELETEing the job id — so it
  // is dropped as soon as the POST settles rather than held for the whole poll.
  const controller = new AbortController();
  inFlight.set(runId, controller);

  let accepted: { jobId: string } | null = null;

  try {
    accepted = await postJson<{ jobId: string }>(
      "/api/v1/ai/videos",
      {
        providerId: data.providerId,
        modelKey,
        prompt,
        images: images.map((image) => image.url),
        inputMode: data.seedanceInputMode ?? DEFAULT_SEEDANCE_INPUT_MODE,
        aspect: data.aspect,
        resolution: data.resolution,
        duration,
        fps: data.fps,
        seed: data.seed,
        watermark: data.watermark,
        generateAudio: data.generateAudio,
      },
      controller.signal,
    );
  } catch (error) {
    // An abort is a user action, not a failure — cancelRun already cleared the placeholder.
    if (error instanceof DOMException && error.name === "AbortError") return;
    if (!runAlive(runId)) return;

    reportRunFailure(
      outputId ? [outputId] : [],
      nodeId,
      error instanceof Error ? error.message : "Generation failed",
    );
    return;
  } finally {
    inFlight.delete(runId);
  }

  if (!runAlive(runId)) return;

  // The job id is what Stop reaches through to cancel the work upstream, and what
  // `resumeVideoJobs` re-attaches a poller to after a reload. Not an undo step — the user did
  // not do this.
  if (outputId) {
    store.updateNodeData(outputId, { jobId: accepted.jobId }, { history: false });
  }

  await pollVideoJob(accepted.jobId, outputId, runId);
}

export async function pollVideoJob(
  jobId: string,
  outputNodeId: string | null,
  runId: string,
): Promise<void> {
  const store = useCanvasStore.getState();

  for (let attempt = 0; attempt < VIDEO_POLL_LIMIT; attempt += 1) {
    await new Promise((resolve) => setTimeout(resolve, VIDEO_POLL_INTERVAL_MS));

    // Stop if the run was cancelled or its node deleted while waiting.
    if (!runAlive(runId)) return;

    let status: {
      status: string;
      progress: number | null;
      assetUrl: string | null;
      error: string | null;
    };

    try {
      const response = await fetch(`/api/v1/ai/videos/${jobId}`, { credentials: "include" });

      // The job row is gone — cancelled from another tab, or swept. Nothing will ever resolve
      // it, so polling out the remaining attempts would only delay the message by 20 minutes.
      if (response.status === 404) {
        if (outputNodeId) {
          store.patchExecution(outputNodeId, "failed", {
            error: "The video job is no longer available.",
          });
        }
        return;
      }

      if (!response.ok) continue;
      status = await response.json();
    } catch {
      // A transient network failure should not kill a job that may still be running.
      continue;
    }

    if (status.status === "succeeded" && status.assetUrl) {
      if (outputNodeId) {
        // Not an undo step: the user did not do this, and merging it into whatever they were
        // editing when the render landed would make Ctrl+Z revert both.
        store.updateNodeData(
          outputNodeId,
          { assetUrl: status.assetUrl, execution: undefined },
          { history: false },
        );
      }
      return;
    }

    if (status.status === "failed" || status.status === "cancelled") {
      if (outputNodeId) {
        store.patchExecution(outputNodeId, "failed", {
          error: status.error ?? "Video generation failed",
        });
      }
      return;
    }

    if (status.progress !== null && outputNodeId) {
      store.patchExecution(outputNodeId, "running", { progress: status.progress });
    }
  }

  if (outputNodeId) {
    store.patchExecution(outputNodeId, "failed", {
      error: "Timed out waiting for the video job.",
    });
  }
}

/**
 * Re-attaches pollers to video jobs that outlived the page.
 *
 * `stripForStorage` deliberately keeps a video node `running` when it carries a `jobId`, because
 * the work continues server-side. Without this, that node came back spinning and nothing ever
 * resolved it — the resume path was designed and persisted for, but never connected.
 *
 * Call once after the canvas hydrates.
 */
export function resumeVideoJobs(nodes: CanvasNode[]): void {
  for (const node of nodes) {
    const data = node.data as { jobId?: string; execution?: NodeExecution };

    if (!data.jobId || data.execution?.status !== "running") continue;

    void pollVideoJob(data.jobId, node.id, data.execution.runId);
  }
}
