"use client";

import { FileJson, Plus, Trash2, TriangleAlert } from "lucide-react";
import { useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import {
  Dialog,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import type { CreateMcpServerInput, McpServer, McpTransport } from "@/features/ai/api";
import { MCP_RUNTIME_SUPPORTED } from "@/features/ai/protocol-support";
import { useAiStore } from "@/features/ai/store";
import { parseMcpConfig } from "@/features/settings/mcp-import";
import {
  FormField,
  MasterDetail,
  PanelEmpty,
} from "@/features/settings/panels/panel-shell";
import { cn } from "@/lib/utils";

const TRANSPORT_LABELS: Record<McpTransport, string> = {
  stdio: "stdio",
  sse: "SSE (Legacy)",
  "streamable-http": "Streamable HTTP",
};

export function McpPanel() {
  const t = useT();
  const servers = useAiStore((state) => state.mcpServers);

  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [importing, setImporting] = useState(false);

  const selected = servers.find((s) => s.id === selectedId) ?? servers[0] ?? null;

  return (
    <>
      {/* Stated up front rather than discovered later: the config here is stored faithfully but
          nothing connects to a server yet. */}
      {!MCP_RUNTIME_SUPPORTED && (
        <div className="flex items-start gap-2 border-b border-amber-500/30 bg-amber-500/5 px-5 py-2.5">
          <TriangleAlert
            className="mt-0.5 size-3.5 shrink-0 text-amber-600 dark:text-amber-500"
            aria-hidden
          />
          <p className="text-[0.6875rem] leading-relaxed text-muted-foreground">
            {t("mcp.runtimeNotSupported")}
          </p>
        </div>
      )}

      <MasterDetail
        list={
          <>
            <div className="flex items-center gap-1.5 border-b p-2">
              <Button
                size="sm"
                variant="outline"
                className="flex-1"
                onPress={() => setImporting(true)}
              >
                <FileJson className="size-3.5" aria-hidden />
                {t("mcp.importJson")}
              </Button>
            </div>

            <div className="flex-1 overflow-y-auto p-1.5">
              {servers.map((server) => (
                <button
                  key={server.id}
                  type="button"
                  onClick={() => setSelectedId(server.id)}
                  className={cn(
                    "flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left transition-colors",
                    server.id === selected?.id ? "bg-accent" : "hover:bg-accent/50",
                  )}
                >
                  <span className="min-w-0 flex-1">
                    <span className="block truncate text-xs font-medium">{server.name}</span>
                    <span className="block text-[0.6875rem] text-muted-foreground">
                      {TRANSPORT_LABELS[server.transport]}
                    </span>
                  </span>
                  <span
                    aria-hidden
                    className={cn(
                      "size-1.5 shrink-0 rounded-full",
                      server.enabled ? "bg-emerald-500" : "bg-muted-foreground/40",
                    )}
                  />
                </button>
              ))}
            </div>
          </>
        }
        detail={
          selected ? (
            <McpDetail key={selected.id} server={selected} />
          ) : (
            <PanelEmpty
              title={t("mcp.empty")}
              description={t("mcp.emptyDescription")}
              action={
                <Button size="sm" onPress={() => setImporting(true)}>
                  <Plus className="size-3.5" aria-hidden />
                  {t("mcp.add")}
                </Button>
              }
            />
          )
        }
      />

      <McpImportDialog isOpen={importing} onOpenChange={setImporting} />
    </>
  );
}

function McpDetail({ server }: { server: McpServer }) {
  const t = useT();
  const updateMcpServer = useAiStore((state) => state.updateMcpServer);
  const deleteMcpServer = useAiStore((state) => state.deleteMcpServer);

  const isStdio = server.transport === "stdio";
  const [deleting, setDeleting] = useState(false);

  return (
    <>
      <header className="flex shrink-0 items-center gap-3 border-b px-5 py-3.5">
        <div className="min-w-0 flex-1">
          <h2 className="truncate text-sm font-semibold">{server.name}</h2>
          <p className="truncate text-xs text-muted-foreground">
            {TRANSPORT_LABELS[server.transport]}
          </p>
        </div>

        <Switch
          isSelected={server.enabled}
          onChange={(value) => void updateMcpServer(server.id, { enabled: value })}
          aria-label={server.enabled ? t("common.disable") : t("common.enable")}
        />

        <Button
          size="icon-sm"
          variant="ghost"
          aria-label={t("common.delete")}
          onPress={() => setDeleting(true)}
        >
          <Trash2 className="size-3.5" aria-hidden />
        </Button>
      </header>

      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto p-5">
        <FormField label={t("mcp.transport")}>
          <Select
            selectedKey={server.transport}
            onSelectionChange={(key) =>
              void updateMcpServer(server.id, { transport: String(key) as McpTransport })
            }
            className="w-64"
          >
            <SelectTrigger aria-label={t("mcp.transport")}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(Object.keys(TRANSPORT_LABELS) as McpTransport[]).map((value) => (
                <SelectItem key={value} id={value}>
                  {TRANSPORT_LABELS[value]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </FormField>

        {isStdio ? (
          <>
            <FormField label={t("mcp.command")}>
              <Input
                defaultValue={server.command ?? ""}
                onBlur={(event) =>
                  void updateMcpServer(server.id, { command: event.target.value })
                }
                aria-label={t("mcp.command")}
                className="font-mono text-xs"
              />
            </FormField>

            {server.args.length > 0 && (
              <FormField label={t("mcp.args")}>
                <pre className="overflow-x-auto rounded-lg border bg-muted/40 p-2 font-mono text-[0.6875rem]">
                  {server.args.join(" ")}
                </pre>
              </FormField>
            )}
          </>
        ) : (
          <>
            <FormField label={t("mcp.url")}>
              <Input
                defaultValue={server.url ?? ""}
                onBlur={(event) => void updateMcpServer(server.id, { url: event.target.value })}
                aria-label={t("mcp.url")}
                className="font-mono text-xs"
              />
            </FormField>

            {server.transport === "streamable-http" && (
              <div className="flex items-center justify-between gap-4 rounded-lg border p-3">
                <div>
                  <p className="text-xs font-medium">{t("mcp.autoFallback")}</p>
                  <p className="text-[0.6875rem] text-muted-foreground">
                    {t("mcp.autoFallbackHint")}
                  </p>
                </div>
                <Switch
                  isSelected={server.autoFallback}
                  onChange={(value) => void updateMcpServer(server.id, { autoFallback: value })}
                  aria-label={t("mcp.autoFallback")}
                />
              </div>
            )}
          </>
        )}

        {server.env.length > 0 && (
          <FormField label={t("mcp.env")}>
            <div className="divide-y rounded-lg border text-xs">
              {server.env.map((entry) => (
                <div key={entry.key} className="flex gap-2 px-2.5 py-1.5 font-mono">
                  <span className="text-muted-foreground">{entry.key}</span>
                  <span className="truncate">{entry.value}</span>
                </div>
              ))}
            </div>
          </FormField>
        )}
      </div>

      <ConfirmDialog
        isOpen={deleting}
        onOpenChange={setDeleting}
        title={t("mcp.deleteConfirm", { name: server.name })}
        onConfirm={() => deleteMcpServer(server.id)}
      />
    </>
  );
}

function McpImportDialog({
  isOpen,
  onOpenChange,
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const t = useT();
  const importMcpServers = useAiStore((state) => state.importMcpServers);

  const [json, setJson] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit() {
    const parsed = parseMcpConfig(json);

    if (!parsed.ok) {
      setError(`${t("mcp.importInvalid")}: ${parsed.detail}`);
      return;
    }

    setBusy(true);
    setError(null);

    try {
      const added = await importMcpServers(parsed.servers as CreateMcpServerInput[]);
      window.alert(t("mcp.imported", { count: added }));
      setJson("");
      onOpenChange(false);
    } catch (importError) {
      setError(importError instanceof Error ? importError.message : t("errors.unknown"));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      className="w-[min(36rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
    >
      <DialogHeader>
        <DialogTitle>{t("mcp.importJson")}</DialogTitle>
        <DialogDescription>{t("mcp.importJsonHint")}</DialogDescription>
      </DialogHeader>

      <Textarea
        value={json}
        onChange={(event) => setJson(event.target.value)}
        rows={12}
        placeholder={'{\n  "mcpServers": {\n    "filesystem": {\n      "command": "npx",\n      "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"]\n    }\n  }\n}'}
        aria-label={t("mcp.importJson")}
        className="font-mono text-xs"
      />

      {error && <p className="text-xs text-destructive">{error}</p>}

      <DialogFooter>
        <Button variant="outline" size="sm" onPress={() => onOpenChange(false)}>
          {t("common.cancel")}
        </Button>
        <Button size="sm" isDisabled={!json.trim() || busy} onPress={() => void submit()}>
          {t("common.import")}
        </Button>
      </DialogFooter>
    </Dialog>
  );
}
