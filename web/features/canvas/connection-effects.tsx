"use client";

import {
  getBezierPath,
  useInternalNode,
  useNodeId,
  useStore,
  type ConnectionLineComponentProps,
} from "@xyflow/react";
import { useEffect, useId, useState, type CSSProperties } from "react";
import { create } from "zustand";
import type { NodeKind } from "@/features/canvas/types";

/**
 * Connection feedback: a lightning arc while a drag is snapped onto a handle, and a burst on the
 * target node once the edge lands.
 *
 * Nodes here have one untyped input and one output, so the colour comes from what is flowing —
 * the kind of the node feeding the connection — rather than from a port type.
 */
const CONNECT_COLORS: Record<NodeKind, string> = {
  text: "#f5b81c",
  image: "#38bdf8",
  video: "#f472b6",
  imageGen: "#a78bfa",
  videoGen: "#fb923c",
};

export function connectColor(kind: string | undefined): string {
  return CONNECT_COLORS[(kind ?? "text") as NodeKind] ?? CONNECT_COLORS.text;
}

/** Long enough for the slowest piece of the burst (the border comets) to finish. */
const PULSE_MS = 1100;

export interface ConnectPulse {
  key: number;
  source: string;
  target: string;
  color: string;
}

interface ConnectPulseState {
  /** Keyed by target node id: one burst per node, a newer connection replaces the older one. */
  pulses: Record<string, ConnectPulse>;
  fire: (source: string, target: string, color: string) => void;
}

let pulseCounter = 0;

/**
 * Transient view state, kept out of the canvas store so it never reaches history or autosave.
 */
export const useConnectPulseStore = create<ConnectPulseState>((set, get) => ({
  pulses: {},
  fire: (source, target, color) => {
    pulseCounter += 1;
    const pulse = { key: pulseCounter, source, target, color };
    set((state) => ({ pulses: { ...state.pulses, [target]: pulse } }));

    setTimeout(() => {
      if (get().pulses[target]?.key !== pulse.key) return;
      set((state) => {
        const pulses = { ...state.pulses };
        delete pulses[target];
        return { pulses };
      });
    }, PULSE_MS);
  },
}));

type Point = [number, number];

/** mulberry32. Seeded so the bolt is a pure function of render inputs. */
function seededRandom(seed: number): () => number {
  let state = seed >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/**
 * Lightning by midpoint displacement: split every segment at its midpoint and push the midpoint
 * sideways by a random amount, shrinking the allowed push each generation (`BOLT_DECAY`). The
 * first generations lay down a few big, sharp kinks; the later ones only roughen the straight runs
 * between them — which is what separates a bolt from a uniform zigzag.
 */
function displace(
  from: Point,
  to: Point,
  random: () => number,
  generations: number,
  maxOffset: number,
  onMidpoint?: (start: Point, mid: Point, end: Point, generation: number) => void,
): Point[] {
  let points: Point[] = [from, to];
  let offset = maxOffset;

  for (let generation = 0; generation < generations; generation += 1) {
    const next: Point[] = [points[0]];
    for (let index = 0; index < points.length - 1; index += 1) {
      const a = points[index];
      const b = points[index + 1];
      const dx = b[0] - a[0];
      const dy = b[1] - a[1];
      const length = Math.hypot(dx, dy) || 1;
      // At least 40% of the allowed push, so no kink comes out nearly straight — the reference
      // is all sharp corners.
      const push = (random() < 0.5 ? -1 : 1) * (0.4 + random() * 0.6) * offset;
      const mid: Point = [(a[0] + b[0]) / 2 - (dy / length) * push, (a[1] + b[1]) / 2 + (dx / length) * push];
      onMidpoint?.(a, mid, b, generation);
      next.push(mid, b);
    }
    points = next;
    offset *= BOLT_DECAY;
  }

  return points;
}

function toPath(points: Point[]): string {
  return points
    .map(([x, y], index) => `${index === 0 ? "M" : "L"}${x.toFixed(2)} ${y.toFixed(2)}`)
    .join("");
}

/** Screen-pixel tuning, measured off the reference clip. */
const BOLT_ROUGHNESS = 0.26;
const BOLT_MAX_OFFSET_PX = 18;
const BOLT_GENERATIONS = 3;
/** How much of the push survives each generation. Above ½ keeps the mid-sized kinks visible. */
const BOLT_DECAY = 0.6;
const BRANCH_CHANCE = 0.15;
const MAX_BRANCHES = 1;

function bolt(
  from: Point,
  to: Point,
  seed: number,
  zoom: number,
): { trunk: string; branches: string[] } {
  const random = seededRandom(seed);
  const length = Math.hypot(to[0] - from[0], to[1] - from[1]);
  const branchSpecs: Array<[Point, Point]> = [];

  const trunk = displace(
    from,
    to,
    random,
    BOLT_GENERATIONS,
    Math.min(length * BOLT_ROUGHNESS, BOLT_MAX_OFFSET_PX / zoom),
    (start, mid, end, generation) => {
      // Forks come off the coarse kinks only, so they read as offshoots rather than fuzz.
      if (generation > 1 || branchSpecs.length >= MAX_BRANCHES || random() > BRANCH_CHANCE) return;
      const heading = Math.atan2(end[1] - start[1], end[0] - start[0]);
      const angle = heading + (random() < 0.5 ? -1 : 1) * (0.35 + random() * 0.45);
      const reach = Math.hypot(end[0] - start[0], end[1] - start[1]) * (0.3 + random() * 0.2);
      branchSpecs.push([mid, [mid[0] + Math.cos(angle) * reach, mid[1] + Math.sin(angle) * reach]]);
    },
  );

  const branches = branchSpecs.map(([start, end]) => {
    const reach = Math.hypot(end[0] - start[0], end[1] - start[1]);
    return toPath(displace(start, end, random, 3, reach * 0.2));
  });

  return { trunk: toPath(trunk), branches };
}

/** How often the bolt re-strikes. The reference changes shape on nearly every frame. */
const FLICKER_MS = 50;

/**
 * The line drawn while dragging a new connection.
 *
 * Unsnapped, it is the ordinary bezier. Once the drag comes within `connectionRadius` of a handle,
 * React Flow snaps the target to it; the bezier then stops at the pointer and a bolt bridges the
 * remaining gap — the "contact" before the connection lands.
 */
export function LightningConnectionLine({
  fromX,
  fromY,
  toX,
  toY,
  fromPosition,
  toPosition,
  fromNode,
  fromHandle,
  toNode,
  toHandle,
  connectionStatus,
  pointer,
}: ConnectionLineComponentProps) {
  const snapped = toHandle !== null && connectionStatus !== "invalid";
  const transform = useStore((state) => state.transform);
  const [seed, setSeed] = useState(1);
  // useId output can contain characters that break inside `url(#…)`.
  const glowId = `bolt-glow-${useId().replace(/[^\w-]/g, "")}`;

  useEffect(() => {
    if (!snapped) return;
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

    const timer = window.setInterval(() => setSeed((value) => value + 1), FLICKER_MS);
    return () => window.clearInterval(timer);
  }, [snapped]);

  const zoom = transform[2];
  // `pointer` is in container pixels; everything else here is in flow coordinates.
  const cursor: Point = snapped
    ? [(pointer.x - transform[0]) / zoom, (pointer.y - transform[1]) / zoom]
    : [toX, toY];

  const [curve] = getBezierPath({
    sourceX: fromX,
    sourceY: fromY,
    sourcePosition: fromPosition,
    targetX: cursor[0],
    targetY: cursor[1],
    targetPosition: toPosition,
  });

  if (!snapped) {
    return <path className="react-flow__connection-path" d={curve} fill="none" />;
  }

  // Coloured by whichever end is the output, regardless of which end the drag started from.
  const feeder = fromHandle.type === "source" ? fromNode : (toNode ?? fromNode);
  const color = connectColor(feeder.type);
  const target: Point = [toX, toY];
  const strike = bolt(cursor, target, seed, zoom);
  // The previous strike lingers faintly, the way an arc's afterimage does.
  const afterimage = bolt(cursor, target, seed - 1, zoom);
  // Sizes below are screen pixels.
  const px = 1 / zoom;
  // Brightness wobbles between strikes so the arc crackles rather than just changing shape.
  const intensity = 0.8 + seededRandom(seed * 7919)() * 0.2;

  // The blur needs an explicit user-space region: the default is relative to the bounding box,
  // which collapses to nothing for a near-horizontal bolt.
  const pad = 40 * px;
  const region = {
    x: Math.min(cursor[0], toX) - pad,
    y: Math.min(cursor[1], toY) - pad,
    width: Math.abs(toX - cursor[0]) + pad * 2,
    height: Math.abs(toY - cursor[1]) + pad * 2,
  };

  return (
    <g fill="none" strokeLinecap="round" strokeLinejoin="round">
      <defs>
        <filter id={glowId} filterUnits="userSpaceOnUse" {...region}>
          <feGaussianBlur stdDeviation={4 * px} />
        </filter>
      </defs>

      <path className="react-flow__connection-path" d={curve} />

      <path
        d={afterimage.trunk}
        stroke={color}
        strokeWidth={1 * px}
        strokeOpacity={0.25}
      />

      <g opacity={intensity}>
        {/* Bloom: a blurred copy of the bolt, the soft halo in the reference. */}
        <g filter={`url(#${glowId})`} stroke={color}>
          <path d={strike.trunk} strokeWidth={6 * px} />
          {strike.branches.map((branch) => (
            <path key={branch} d={branch} strokeWidth={3 * px} />
          ))}
        </g>

        <path d={strike.trunk} stroke={color} strokeWidth={2.75 * px} />
        <path d={strike.trunk} stroke="#fff" strokeWidth={1 * px} strokeOpacity={0.7} />
        {strike.branches.map((branch) => (
          <path key={branch} d={branch} stroke={color} strokeWidth={1 * px} strokeOpacity={0.85} />
        ))}
      </g>

      {/* Contact points: a hot spark where the arc leaves the cursor, a lit ring on the handle. */}
      <circle cx={cursor[0]} cy={cursor[1]} r={7 * px} fill={color} fillOpacity={0.35} filter={`url(#${glowId})`} />
      <circle cx={cursor[0]} cy={cursor[1]} r={3.5 * px} fill="#fff" />
      <circle cx={toX} cy={toY} r={5 * px} fill={color} />
      <circle cx={toX} cy={toY} r={11 * px} stroke={color} strokeWidth={1.75 * px} />
      <circle
        cx={toX}
        cy={toY}
        r={11 * px}
        stroke={color}
        strokeWidth={4 * px}
        strokeOpacity={0.6}
        filter={`url(#${glowId})`}
      />
    </g>
  );
}

/** Matches the node frame's `rounded-xl`. */
const NODE_RADIUS = 14;

/**
 * The burst on a node that just received a connection: the input handle flashes, rings ripple
 * out of it, and two comets run around the frame in opposite directions.
 *
 * Rendered beside the node frame rather than inside it, because the frame clips its overflow and
 * the glow is meant to spill past the border.
 */
export function ConnectBurst() {
  const id = useNodeId();
  const pulse = useConnectPulseStore((state) => (id ? state.pulses[id] : undefined));
  if (!id || !pulse) return null;

  // Keyed so a second connection while the first burst is playing restarts it.
  return <Burst key={pulse.key} nodeId={id} color={pulse.color} />;
}

function Burst({ nodeId, color }: { nodeId: string; color: string }) {
  const node = useInternalNode(nodeId);
  const width = node?.measured.width ?? node?.width ?? 0;
  const height = node?.measured.height ?? node?.height ?? 0;
  if (!width || !height) return null;

  const r = Math.min(NODE_RADIUS, width / 2, height / 2);
  const mid = height / 2;
  // Both start at the input handle (left edge, vertical middle) and meet on the far side.
  const clockwise =
    `M0 ${mid}V${r}A${r} ${r} 0 0 1 ${r} 0H${width - r}A${r} ${r} 0 0 1 ${width} ${r}` +
    `V${height - r}A${r} ${r} 0 0 1 ${width - r} ${height}H${r}A${r} ${r} 0 0 1 0 ${height - r}Z`;
  const counterClockwise =
    `M0 ${mid}V${height - r}A${r} ${r} 0 0 0 ${r} ${height}H${width - r}` +
    `A${r} ${r} 0 0 0 ${width} ${height - r}V${r}A${r} ${r} 0 0 0 ${width - r} 0H${r}` +
    `A${r} ${r} 0 0 0 0 ${r}Z`;

  return (
    <div
      aria-hidden
      className="canvas-connect-burst pointer-events-none absolute inset-0"
      style={{ "--burst": color } as CSSProperties}
    >
      <span className="canvas-connect-bloom" />
      <svg className="absolute left-0 top-0 overflow-visible" width={width} height={height}>
        {[clockwise, counterClockwise].map((d) => (
          <g key={d}>
            <path d={d} pathLength={1} className="canvas-connect-comet canvas-connect-comet-glow" />
            <path d={d} pathLength={1} className="canvas-connect-comet" />
            <path d={d} pathLength={1} className="canvas-connect-comet-head" />
          </g>
        ))}
      </svg>
      <span className="canvas-connect-ring" />
      <span className="canvas-connect-ring [animation-delay:90ms]" />
      <span className="canvas-connect-ring [animation-delay:180ms]" />
      <span className="canvas-connect-port" />
    </div>
  );
}
