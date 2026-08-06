/**
 * Canvas node model.
 *
 * Ported from OpenCowork's graph types. The rendering is @xyflow/react rather than a hand-written
 * canvas, but the data model and the execution semantics are kept: four node kinds, a shared
 * execution snapshot, and generation config that lives on its own node rather than inside the
 * image node.
 *
 * Why a separate config node: one config can feed several outputs, and the same prompt can be run
 * against two different models by wiring it to two config nodes. Folding the settings into the
 * image node would make both awkward.
 */

export type NodeKind = "text" | "image" | "video" | "imageGen" | "videoGen";

export type ExecutionStatus =
  | "idle"
  | "queued"
  | "running"
  | "succeeded"
  | "failed"
  | "cancelled"
  /** The tab closed mid-run. Distinct from failed: nothing is known about the outcome. */
  | "interrupted";

export interface NodeExecution {
  /**
   * Identifies one attempt.
   *
   * Shared by every node a single run produced, which is how cancelling from one of them stops
   * the whole run — and how a late response finds out the user has since thrown its nodes away.
   */
  runId: string;
  status: ExecutionStatus;
  startedAt?: number;
  finishedAt?: number;
  /** 0-1, only meaningful for video. */
  progress?: number;
  error?: string;
}

export interface TextNodeData {
  text: string;
  /** Multiplier applied to the base font size, adjustable per node. */
  fontScale?: number;
}

export interface ImageNodeData {
  /** Server asset URL. Never a data URL in persisted state — see persistence.ts. */
  assetUrl?: string;
  mediaType?: string;
  /** The prompt that produced this image, reused when it is fed back in as a reference. */
  prompt?: string;
  providerId?: string;
  modelId?: string;
  /** Marks this asset as an inpaint mask rather than an ordinary reference image. */
  role?: "reference" | "inpaint-mask";
  execution?: NodeExecution;
}

export interface VideoNodeData {
  assetUrl?: string;
  posterUrl?: string;
  mediaType?: string;
  prompt?: string;
  providerId?: string;
  modelId?: string;
  /** Server-side job, polled until it reaches a terminal state. */
  jobId?: string;
  execution?: NodeExecution;
}

export type AspectRatio = "1:1" | "3:2" | "2:3" | "16:9" | "9:16" | "4:3" | "3:4";

/** Encoding to ask the provider for. */
export type ImageOutputFormat = "png" | "jpeg" | "webp";

/**
 * How the provider should hand the bytes back. Only affects the upstream call — either way the
 * server stores the image and the canvas gets an asset URL.
 */
export type ImageResponseFormat = "url" | "base64";

export interface ImageGenNodeData {
  providerId?: string;
  modelId?: string;
  aspect?: AspectRatio;
  /** Explicit pixel size; overrides the aspect ratio when set. */
  size?: string;
  quality?: "auto" | "low" | "medium" | "high";
  outputFormat?: ImageOutputFormat;
  responseFormat?: ImageResponseFormat;
  /** How many images to request, 1-4. */
  count?: number;
  seed?: number;
  /**
   * A failure that never reached a node of its own — nothing connected upstream, model gone,
   * request refused. A run in progress lives on the image nodes it created, not here, so that
   * starting a second run never contends with the first.
   */
  execution?: NodeExecution;
}

export interface VideoGenNodeData {
  providerId?: string;
  modelId?: string;
  aspect?: AspectRatio;
  resolution?: "480p" | "720p" | "1080p";
  /** Seconds. */
  duration?: number;
  fps?: number;
  seed?: number;
  watermark?: boolean;
  generateAudio?: boolean;
  cameraFixed?: boolean;
  /** Seedance only: keep connected media as references, or map up to two images to first/last frames. */
  seedanceInputMode?: SeedanceVideoInputMode;
  /** As on `ImageGenNodeData`: only a failure that never reached a node of its own. */
  execution?: NodeExecution;
}

export type SeedanceVideoInputMode = "reference" | "first-last";

export const DEFAULT_SEEDANCE_INPUT_MODE: SeedanceVideoInputMode = "reference";

export type CanvasNodeData =
  | TextNodeData
  | ImageNodeData
  | VideoNodeData
  | ImageGenNodeData
  | VideoGenNodeData;

/** Default box size per kind, mirroring OpenCowork's proportions. */
export const NODE_DEFAULT_SIZE: Record<NodeKind, { width: number; height: number }> = {
  text: { width: 280, height: 160 },
  image: { width: 320, height: 320 },
  video: { width: 360, height: 240 },
  imageGen: { width: 300, height: 340 },
  videoGen: { width: 300, height: 380 },
};

export const NODE_MIN_SIZE = { width: 160, height: 100 };

export const ASPECT_RATIOS: readonly AspectRatio[] = [
  "1:1",
  "16:9",
  "9:16",
  "4:3",
  "3:4",
  "3:2",
  "2:3",
];

/** Doubles as the chip labels — the API's own spellings read fine untranslated. */
export const IMAGE_OUTPUT_FORMATS: readonly ImageOutputFormat[] = ["png", "jpeg", "webp"];

export const IMAGE_RESPONSE_FORMATS: readonly ImageResponseFormat[] = ["url", "base64"];

/** What the server falls back to, mirrored here so the chips show the value that will be sent. */
export const DEFAULT_IMAGE_OUTPUT_FORMAT: ImageOutputFormat = "png";

export const DEFAULT_IMAGE_RESPONSE_FORMAT: ImageResponseFormat = "url";

export const VIDEO_DURATION_MIN = 1;
export const VIDEO_DURATION_MAX = 60;
export const DEFAULT_VIDEO_DURATION = 5;

export function isValidVideoDuration(value: unknown): value is number {
  return (
    typeof value === "number" &&
    Number.isInteger(value) &&
    value >= VIDEO_DURATION_MIN &&
    value <= VIDEO_DURATION_MAX
  );
}

export function resolveVideoDuration(value: number | undefined): number | null {
  const duration = value ?? DEFAULT_VIDEO_DURATION;
  return isValidVideoDuration(duration) ? duration : null;
}

/**
 * Maps an aspect ratio to the pixel size OpenAI's image API accepts.
 *
 * That API takes discrete sizes rather than a ratio, and rejects anything else — so ratios that
 * have no exact match land on the nearest supported one.
 */
export const ASPECT_TO_SIZE: Record<AspectRatio, string> = {
  "1:1": "1024x1024",
  "3:2": "1536x1024",
  "2:3": "1024x1536",
  "16:9": "1536x1024",
  "9:16": "1024x1536",
  "4:3": "1536x1024",
  "3:4": "1024x1536",
};

export function isTerminal(status: ExecutionStatus): boolean {
  return (
    status === "succeeded" ||
    status === "failed" ||
    status === "cancelled" ||
    status === "interrupted"
  );
}
