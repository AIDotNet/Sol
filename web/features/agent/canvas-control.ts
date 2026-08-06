"use client";

export interface AgentCanvasControls {
  fitView: () => Promise<boolean>;
  zoomIn: () => Promise<boolean>;
  zoomOut: () => Promise<boolean>;
}

let controls: AgentCanvasControls | null = null;

export function registerAgentCanvasControls(next: AgentCanvasControls): () => void {
  controls = next;
  return () => {
    if (controls === next) controls = null;
  };
}

export function getAgentCanvasControls(): AgentCanvasControls | null {
  return controls;
}
