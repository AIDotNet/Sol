/**
 * Device identity handshake.
 *
 * Two layers, matching the server:
 *  - the server sets an HttpOnly cookie, which JavaScript cannot read;
 *  - the response body repeats the device id so it can be mirrored into localStorage and
 *    replayed if the cookie is ever cleared.
 *
 * Signals are split into `stable` (properties of the hardware/OS, which have some chance of
 * matching across browsers) and `volatile` (browser-dependent, high entropy but useless across
 * browsers). Everything is optional — a privacy-hardened browser withholds several of these,
 * and the server normalizes what is missing rather than rejecting the request.
 */

const STORAGE_KEY = "sol.device_id";

export interface DeviceIdentity {
  deviceId: string;
  visitorId: string;
  isNewDevice: boolean;
  /** 1.0 cookie, 0.95 localStorage restore, 0.6 cross-browser guess. */
  confidence: number;
  method: "Deterministic" | "ProbabilisticCoarse" | "Manual";
}

function collectStable() {
  const nav = navigator as Navigator & { deviceMemory?: number };

  return {
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone ?? null,
    platform: nav.platform ?? null,
    hardwareConcurrency: nav.hardwareConcurrency ?? null,
    deviceMemoryGb: nav.deviceMemory ?? null,
    screen: {
      w: window.screen?.width ?? null,
      h: window.screen?.height ?? null,
      colorDepth: window.screen?.colorDepth ?? null,
      dpr: window.devicePixelRatio ?? null,
    },
    gpu: readGpu(),
    primaryLanguage: navigator.language ?? null,
  };
}

function collectVolatile() {
  return {
    userAgent: navigator.userAgent ?? null,
    languages: navigator.languages ? [...navigator.languages] : null,
    canvasHash: null,
    audioHash: null,
    fontsHash: null,
  };
}

/**
 * WEBGL_debug_renderer_info is the single most useful hardware signal, and it is being
 * withdrawn — Firefox is deprecating it and Safari restricts it. Treat absence as normal.
 */
function readGpu(): { vendor: string | null; renderer: string | null } {
  try {
    const canvas = document.createElement("canvas");
    const gl = canvas.getContext("webgl") as WebGLRenderingContext | null;
    if (!gl) return { vendor: null, renderer: null };

    const ext = gl.getExtension("WEBGL_debug_renderer_info");
    if (!ext) return { vendor: null, renderer: null };

    return {
      vendor: gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) ?? null,
      renderer: gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) ?? null,
    };
  } catch {
    return { vendor: null, renderer: null };
  }
}

/**
 * Resolves this browser's identity. Idempotent and safe to call on every page load — a caller
 * that already holds a cookie is recognised without any fingerprinting.
 */
export async function handshake(): Promise<DeviceIdentity> {
  const response = await fetch("/api/v1/device/handshake", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    // Required for the cookie to be sent and stored, even same-origin via the rewrite proxy.
    credentials: "include",
    body: JSON.stringify({
      v: 1,
      stable: collectStable(),
      volatile: collectVolatile(),
      clientStoredId: localStorage.getItem(STORAGE_KEY),
    }),
  });

  if (!response.ok) {
    throw new Error(`Device handshake failed: ${response.status}`);
  }

  const identity: DeviceIdentity = await response.json();

  // The cookie is HttpOnly, so this mirror is the only copy JavaScript can read — and the only
  // way to recover the identity if the cookie is cleared.
  localStorage.setItem(STORAGE_KEY, identity.deviceId);

  return identity;
}

/** Reads the mirrored id without contacting the server. */
export function storedDeviceId(): string | null {
  return localStorage.getItem(STORAGE_KEY);
}
