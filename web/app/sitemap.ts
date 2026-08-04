import type { MetadataRoute } from "next";
import { env } from "@/lib/env";

// "" is the canvas, and it is the whole app. Its contents are device-scoped and unreachable
// without the owner's cookie, so there is nothing deeper worth listing.
const ROUTES = [""];

export default function sitemap(): MetadataRoute.Sitemap {
  return ROUTES.map((route) => ({
    url: `${env.NEXT_PUBLIC_SITE_URL}${route}`,
    lastModified: new Date(),
  }));
}
