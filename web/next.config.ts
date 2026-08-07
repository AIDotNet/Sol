import type { NextConfig } from "next";

const API_ORIGIN = process.env.SOL_API_ORIGIN ?? "http://localhost:5298";

const nextConfig: NextConfig = {
  // Self-contained server bundle (.next/standalone) for the Docker image, so the runtime stage
  // doesn't need node_modules or the full source tree copied in.
  output: "standalone",

  // The dev-only overlay badge sits in a corner, and the canvas uses all four: toolbars top,
  // zoom controls bottom-left, minimap bottom-right. It was covering the zoom controls.
  // Development-only — this changes nothing about a production build.
  devIndicators: false,

  experimental: {
    // The rewrite proxy defaults to a 30-second socket timeout, and it is an *inactivity*
    // timeout: image generation sends nothing over the wire while the model renders, so a run
    // that takes longer than 30s has its socket destroyed. The API then sees a client
    // disconnect, RequestAborted fires, and the upstream call dies with
    // TaskCanceledException — a cancellation that looks like a timeout but originates here,
    // not at the vendor.
    //
    // Twenty minutes matches Ai:RequestTimeoutSeconds so the vendor call is what decides the
    // outcome, rather than the proxy in front of it.
    proxyTimeout: 20 * 60 * 1000,
  },

  // Proxy the API through this origin so the browser only ever sees localhost:3000.
  //
  // Without it the device cookie would be third-party: SameSite=Lax is not sent cross-origin,
  // and SameSite=None requires Secure, which is rejected over plain HTTP in development.
  // In production put both behind one reverse-proxy origin and drop this.
  //
  // Note: Next.js proxies HTTP reliably but WebSocket upgrade through a rewrite is not
  // guaranteed. If /hubs misbehaves in dev, point the SignalR client straight at
  // SOL_API_ORIGIN, or let it fall back to Server-Sent Events / long polling.
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${API_ORIGIN}/api/:path*` },
      { source: "/hubs/:path*", destination: `${API_ORIGIN}/hubs/:path*` },
    ];
  },
};

export default nextConfig;
