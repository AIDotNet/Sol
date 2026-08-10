export type VideoResolution = "480p" | "720p" | "1080p";

/** Routin SD model ids encode their only supported output resolution. */
export function fixedVideoResolution(modelKey: string): VideoResolution | null {
  const match = /^sd-(?:mini|fast|2\.0)-(480p|720p|1080p)$/i.exec(modelKey);
  return (match?.[1]?.toLowerCase() as VideoResolution | undefined) ?? null;
}
