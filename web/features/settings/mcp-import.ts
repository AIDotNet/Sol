import { z } from "zod";
import type { CreateMcpServerInput, McpTransport } from "@/features/ai/api";

/**
 * Parses a Claude Desktop-style MCP configuration blob.
 *
 * Accepts both the wrapped `{ "mcpServers": { … } }` form people copy out of Claude Desktop and
 * a bare map of the same shape, because both circulate in READMEs.
 */

const entrySchema = z.object({
  command: z.string().trim().min(1).optional(),
  args: z.array(z.string()).optional(),
  env: z.record(z.string(), z.string()).optional(),
  cwd: z.string().optional(),
  url: z.string().trim().min(1).optional(),
  headers: z.record(z.string(), z.string()).optional(),
  transport: z.enum(["stdio", "sse", "streamable-http"]).optional(),
  description: z.string().optional(),
  disabled: z.boolean().optional(),
});

const configSchema = z.union([
  z.object({ mcpServers: z.record(z.string(), entrySchema) }),
  z.record(z.string(), entrySchema),
]);

export type McpImportResult =
  | { ok: true; servers: CreateMcpServerInput[] }
  | { ok: false; detail: string };

/**
 * Infers the transport when the config does not state one.
 *
 * Claude Desktop's format has no `transport` key — it is implied by which fields are present.
 * A `/sse` path is the conventional marker for the legacy SSE transport; any other URL is
 * treated as Streamable HTTP, which is what current servers use.
 */
function inferTransport(entry: z.infer<typeof entrySchema>): McpTransport {
  if (entry.transport) return entry.transport;
  if (!entry.url) return "stdio";
  return entry.url.includes("/sse") ? "sse" : "streamable-http";
}

export function parseMcpConfig(json: string): McpImportResult {
  let parsed: unknown;

  try {
    parsed = JSON.parse(json);
  } catch {
    return { ok: false, detail: "invalid JSON" };
  }

  const result = configSchema.safeParse(parsed);
  if (!result.success) {
    return { ok: false, detail: result.error.issues[0]?.message ?? "schema mismatch" };
  }

  const entries =
    "mcpServers" in result.data && typeof result.data.mcpServers === "object"
      ? result.data.mcpServers
      : (result.data as Record<string, z.infer<typeof entrySchema>>);

  const servers: CreateMcpServerInput[] = [];

  for (const [name, entry] of Object.entries(entries)) {
    const transport = inferTransport(entry);

    // The server rejects these too, but failing here names the offending entry, which a
    // generic 400 on a twenty-server paste would not.
    if (transport === "stdio" && !entry.command) {
      return { ok: false, detail: `"${name}" has no command` };
    }
    if (transport !== "stdio" && !entry.url) {
      return { ok: false, detail: `"${name}" has no url` };
    }

    servers.push({
      name,
      description: entry.description ?? null,
      transport,
      command: entry.command ?? null,
      args: entry.args ?? [],
      env: Object.entries(entry.env ?? {}).map(([key, value]) => ({ key, value: String(value) })),
      cwd: entry.cwd ?? null,
      url: entry.url ?? null,
      headers: Object.entries(entry.headers ?? {}).map(([key, value]) => ({
        key,
        value: String(value),
      })),
      autoFallback: transport === "streamable-http",
      enabled: entry.disabled !== true,
    });
  }

  if (servers.length === 0) {
    return { ok: false, detail: "no servers found" };
  }

  return { ok: true, servers };
}
