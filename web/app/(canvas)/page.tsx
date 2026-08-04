import type { Metadata } from "next";
import { CanvasPage } from "@/features/canvas/canvas-page";

export const metadata: Metadata = {
  title: "画布",
};

/**
 * The canvas *is* the app, so it lives at the root.
 *
 * The route group gives it a full-bleed layout without appearing in the URL, and the canvas
 * resolves which document to open by itself — there is no id in the path to enter.
 */
export default function Page() {
  return <CanvasPage />;
}
