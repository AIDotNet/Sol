const BASE = "/api/v1/skills";

export type SkillRiskLevel = "safe" | "warning" | "danger";

export interface SkillRiskFinding {
  code: string;
  severity: SkillRiskLevel;
  path: string;
  line: number | null;
  message: string;
}

export interface SkillScan {
  name: string;
  slug: string;
  description: string;
  hasScripts: boolean;
  maxRisk: SkillRiskLevel;
  risks: SkillRiskFinding[];
  files: string[];
  byteSize: number;
}

export interface Skill extends Omit<SkillScan, "files"> {
  id: string;
  enabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export class SkillApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "SkillApiError";
  }
}

async function read<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  if (!response.ok) {
    let detail = `Request failed with status ${response.status}`;
    try {
      const body = (await response.json()) as { error?: string; details?: string[] };
      detail = body.details?.[0] ?? body.error ?? detail;
    } catch {
      // Keep the status-derived detail when a proxy returned a non-JSON response.
    }
    throw new SkillApiError(detail, response.status);
  }
  return (await response.json()) as T;
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${BASE}${path}`, {
      ...init,
      credentials: "include",
      headers: { Accept: "application/json", ...init?.headers },
    });
  } catch {
    throw new SkillApiError("Failed to reach the API", 0);
  }
  return read<T>(response);
}

export interface SkillList {
  sandboxEnabled: boolean;
  skills: Skill[];
}

export function listSkills(): Promise<SkillList> {
  return request("/");
}

export function scanSkill(file: File): Promise<SkillScan> {
  const form = new FormData();
  form.append("file", file);
  return request("/scan", { method: "POST", body: form });
}

export function installSkill(file: File, confirmDanger: boolean): Promise<Skill> {
  const form = new FormData();
  form.append("file", file);
  return request(`/?confirmDanger=${confirmDanger ? "true" : "false"}`, {
    method: "POST",
    body: form,
  });
}

export function deleteSkill(id: string): Promise<void> {
  return request(`/${id}`, { method: "DELETE" });
}
