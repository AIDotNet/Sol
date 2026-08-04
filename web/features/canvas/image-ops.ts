"use client";

/**
 * Local image operations.
 *
 * Crop, rotate, flip and upscale run entirely in a `<canvas>` — no model, no network, no cost.
 * OpenCowork does the same, and it matters: sending a 90° rotation to an image model would be
 * slow, expensive, and would not actually preserve the picture.
 *
 * Every operation returns a `Blob` so the caller can upload it through the normal asset path
 * and get a durable URL, rather than holding pixels in the graph.
 */

export type FlipAxis = "horizontal" | "vertical";

export interface CropRect {
  /** Fractions of the source dimensions, 0-1, so a rect survives a resize of the preview. */
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Loads an image for canvas work.
 *
 * `crossOrigin` is set because the asset route is same-origin but served through a rewrite in
 * development; without it a later `toBlob` would throw a tainted-canvas SecurityError.
 */
async function load(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.crossOrigin = "anonymous";
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error("The image could not be loaded."));
    image.src = url;
  });
}

function toBlob(canvas: HTMLCanvasElement, mediaType: string): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new Error("The canvas produced no data."))),
      mediaType,
      // Only consulted for lossy types. High enough that repeated edits do not visibly degrade.
      0.95,
    );
  });
}

function context(width: number, height: number): [HTMLCanvasElement, CanvasRenderingContext2D] {
  const canvas = document.createElement("canvas");
  canvas.width = Math.max(1, Math.round(width));
  canvas.height = Math.max(1, Math.round(height));

  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("A 2D canvas context is unavailable.");

  // Matters most for upscaling, where the default would be visibly blocky.
  ctx.imageSmoothingEnabled = true;
  ctx.imageSmoothingQuality = "high";

  return [canvas, ctx];
}

export async function cropImage(
  url: string,
  rect: CropRect,
  mediaType = "image/png",
): Promise<Blob> {
  const image = await load(url);

  const sx = Math.round(rect.x * image.naturalWidth);
  const sy = Math.round(rect.y * image.naturalHeight);
  const sw = Math.max(1, Math.round(rect.width * image.naturalWidth));
  const sh = Math.max(1, Math.round(rect.height * image.naturalHeight));

  const [canvas, ctx] = context(sw, sh);
  ctx.drawImage(image, sx, sy, sw, sh, 0, 0, sw, sh);

  return toBlob(canvas, mediaType);
}

/** Rotates by a quarter turn. Only multiples of 90° so the result stays rectangular. */
export async function rotateImage(
  url: string,
  quarterTurns: number,
  mediaType = "image/png",
): Promise<Blob> {
  const image = await load(url);
  const turns = ((quarterTurns % 4) + 4) % 4;

  const swapped = turns === 1 || turns === 3;
  const width = swapped ? image.naturalHeight : image.naturalWidth;
  const height = swapped ? image.naturalWidth : image.naturalHeight;

  const [canvas, ctx] = context(width, height);

  ctx.translate(width / 2, height / 2);
  ctx.rotate((turns * Math.PI) / 2);
  ctx.drawImage(image, -image.naturalWidth / 2, -image.naturalHeight / 2);

  return toBlob(canvas, mediaType);
}

export async function flipImage(
  url: string,
  axis: FlipAxis,
  mediaType = "image/png",
): Promise<Blob> {
  const image = await load(url);
  const [canvas, ctx] = context(image.naturalWidth, image.naturalHeight);

  if (axis === "horizontal") {
    ctx.translate(image.naturalWidth, 0);
    ctx.scale(-1, 1);
  } else {
    ctx.translate(0, image.naturalHeight);
    ctx.scale(1, -1);
  }

  ctx.drawImage(image, 0, 0);

  return toBlob(canvas, mediaType);
}

/** Largest edge an upscale may produce, so a 4× on a big image cannot exhaust memory. */
const MAX_UPSCALE_EDGE = 8192;

export async function upscaleImage(
  url: string,
  factor: number,
  mediaType = "image/png",
): Promise<Blob> {
  const image = await load(url);

  const clamped = Math.min(
    factor,
    MAX_UPSCALE_EDGE / Math.max(image.naturalWidth, image.naturalHeight),
  );
  const scale = Math.max(1, clamped);

  const [canvas, ctx] = context(image.naturalWidth * scale, image.naturalHeight * scale);
  ctx.drawImage(image, 0, 0, canvas.width, canvas.height);

  return toBlob(canvas, mediaType);
}

/**
 * Extends the canvas around an image, leaving the new area transparent.
 *
 * This is the input an outpaint expects: the model fills whatever is transparent. Insets are
 * fractions of the source dimensions.
 *
 * Always produces PNG — the transparency is the payload, and a lossy format has no alpha
 * channel, so there is no media type to choose.
 */
export async function expandCanvas(
  url: string,
  insets: { top: number; right: number; bottom: number; left: number },
): Promise<Blob> {
  const image = await load(url);

  const left = Math.round(insets.left * image.naturalWidth);
  const right = Math.round(insets.right * image.naturalWidth);
  const top = Math.round(insets.top * image.naturalHeight);
  const bottom = Math.round(insets.bottom * image.naturalHeight);

  const [canvas, ctx] = context(
    image.naturalWidth + left + right,
    image.naturalHeight + top + bottom,
  );

  // No fill: the untouched area stays transparent, which is what marks it as "generate here".
  ctx.drawImage(image, left, top);

  return toBlob(canvas, "image/png");
}

/**
 * Builds the mask an inpaint expects.
 *
 * OpenAI's images/edits convention is the counter-intuitive part: **painted pixels are punched
 * transparent**, and the model regenerates exactly those. A mask that filled them opaque would
 * regenerate everything else instead.
 */
export async function buildInpaintMask(
  url: string,
  strokes: Array<{ points: Array<{ x: number; y: number }>; radius: number }>,
): Promise<Blob> {
  const image = await load(url);
  const [canvas, ctx] = context(image.naturalWidth, image.naturalHeight);

  // Start from the original so unpainted areas carry real pixels, then cut holes in it.
  ctx.drawImage(image, 0, 0);
  ctx.globalCompositeOperation = "destination-out";
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  ctx.strokeStyle = "rgba(0,0,0,1)";

  for (const stroke of strokes) {
    if (stroke.points.length === 0) continue;

    ctx.lineWidth = stroke.radius * 2 * image.naturalWidth;
    ctx.beginPath();

    const [first, ...rest] = stroke.points;
    ctx.moveTo(first.x * image.naturalWidth, first.y * image.naturalHeight);

    if (rest.length === 0) {
      // A single tap still has to erase something.
      ctx.lineTo(first.x * image.naturalWidth + 0.01, first.y * image.naturalHeight);
    } else {
      for (const point of rest) {
        ctx.lineTo(point.x * image.naturalWidth, point.y * image.naturalHeight);
      }
    }

    ctx.stroke();
  }

  // PNG only: the transparency is the payload.
  return toBlob(canvas, "image/png");
}

/** Wraps a produced blob as a File so it can go through the normal upload endpoint. */
export function toUploadFile(blob: Blob, name = "edited"): File {
  const extension = blob.type === "image/jpeg" ? "jpg" : blob.type.split("/")[1] || "png";
  return new File([blob], `${name}.${extension}`, { type: blob.type });
}
