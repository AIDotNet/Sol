"use client";

import { FileText, Loader2, ShieldCheck, Trash2, TriangleAlert, Upload } from "lucide-react";
import { useEffect, useRef, useState } from "react";
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
import * as api from "@/features/skills/api";
import { useSkillsStore } from "@/features/skills/store";
import { PanelBody, PanelEmpty, PanelHeader } from "@/features/settings/panels/panel-shell";
import { cn } from "@/lib/utils";

export function SkillsPanel() {
  const t = useT();
  const input = useRef<HTMLInputElement>(null);
  const skills = useSkillsStore((state) => state.skills);
  const loading = useSkillsStore((state) => state.loading);
  const error = useSkillsStore((state) => state.error);
  const load = useSkillsStore((state) => state.load);
  const install = useSkillsStore((state) => state.install);
  const remove = useSkillsStore((state) => state.remove);

  const [file, setFile] = useState<File | null>(null);
  const [scan, setScan] = useState<api.SkillScan | null>(null);
  const [scanning, setScanning] = useState(false);
  const [installing, setInstalling] = useState(false);
  const [dialogError, setDialogError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<api.Skill | null>(null);

  useEffect(() => {
    void load();
  }, [load]);

  async function selectFile(selected: File | undefined) {
    if (!selected) return;
    setFile(selected);
    setScan(null);
    setDialogError(null);
    setScanning(true);
    try {
      setScan(await api.scanSkill(selected));
    } catch (scanError) {
      setDialogError(scanError instanceof Error ? scanError.message : t("errors.unknown"));
    } finally {
      setScanning(false);
      if (input.current) input.current.value = "";
    }
  }

  function closeUpload() {
    if (installing) return;
    setFile(null);
    setScan(null);
    setDialogError(null);
  }

  async function confirmInstall() {
    if (!file || !scan) return;
    setInstalling(true);
    setDialogError(null);
    try {
      await install(file, scan.maxRisk === "danger");
      setFile(null);
      setScan(null);
      setDialogError(null);
    } catch (installError) {
      setDialogError(installError instanceof Error ? installError.message : t("errors.unknown"));
    } finally {
      setInstalling(false);
    }
  }

  return (
    <>
      <PanelHeader
        title={t("skills.title")}
        description={t("skills.description")}
        actions={
          <Button size="sm" onPress={() => input.current?.click()}>
            <Upload className="size-3.5" aria-hidden />
            {t("skills.upload")}
          </Button>
        }
      />
      <input
        ref={input}
        type="file"
        accept=".zip,application/zip,.md,text/markdown"
        className="hidden"
        onChange={(event) => void selectFile(event.target.files?.[0])}
      />

      {skills.length === 0 && !loading ? (
        <PanelEmpty
          title={t("skills.empty")}
          description={error ?? t("skills.emptyDescription")}
          action={
            <Button size="sm" onPress={() => input.current?.click()}>
              <Upload className="size-3.5" aria-hidden />
              {t("skills.upload")}
            </Button>
          }
        />
      ) : (
        <PanelBody className="gap-3">
          {error && <p className="text-xs text-destructive">{error}</p>}
          {loading && skills.length === 0 ? (
            <div className="flex items-center gap-2 text-xs text-muted-foreground">
              <Loader2 className="size-3.5 animate-spin" aria-hidden />
              {t("common.loading")}
            </div>
          ) : null}
          {skills.map((skill) => (
            <article key={skill.id} className="flex items-start gap-3 rounded-lg border p-3">
              <span
                className={cn(
                  "flex size-9 shrink-0 items-center justify-center rounded-lg",
                  skill.maxRisk === "danger"
                    ? "bg-destructive/10 text-destructive"
                    : skill.maxRisk === "warning"
                      ? "bg-amber-500/10 text-amber-600"
                      : "bg-emerald-500/10 text-emerald-600",
                )}
              >
                {skill.maxRisk === "safe" ? (
                  <ShieldCheck className="size-4" aria-hidden />
                ) : (
                  <TriangleAlert className="size-4" aria-hidden />
                )}
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="truncate text-sm font-medium">{skill.name}</h3>
                  <RiskBadge risk={skill.maxRisk} />
                  {skill.hasScripts ? (
                    <span className="rounded bg-muted px-1.5 py-0.5 text-[0.625rem] text-muted-foreground">
                      {t("skills.containsScripts")}
                    </span>
                  ) : null}
                </div>
                <p className="mt-1 text-xs text-muted-foreground">
                  {skill.description || t("skills.noDescription")}
                </p>
                <p className="mt-1 font-mono text-[0.625rem] text-muted-foreground/80">
                  {skill.slug} · {formatBytes(skill.byteSize)}
                </p>
              </div>
              <Button
                size="icon-sm"
                variant="ghost"
                aria-label={t("common.delete")}
                onPress={() => setDeleting(skill)}
              >
                <Trash2 className="size-3.5" aria-hidden />
              </Button>
            </article>
          ))}
        </PanelBody>
      )}

      <Dialog
        isOpen={file !== null}
        onOpenChange={(open) => { if (!open) closeUpload(); }}
        className="w-[min(40rem,calc(100vw-2rem))] max-w-none sm:max-w-none"
      >
        <DialogHeader>
          <DialogTitle>{t("skills.scanTitle")}</DialogTitle>
          <DialogDescription>{file?.name}</DialogDescription>
        </DialogHeader>

        {scanning ? (
          <div className="flex items-center gap-2 py-8 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin" aria-hidden />
            {t("skills.scanning")}
          </div>
        ) : scan ? (
          <div className="flex max-h-[26rem] flex-col gap-4 overflow-y-auto">
            <div className="rounded-lg border p-3">
              <div className="flex items-center gap-2">
                <FileText className="size-4 text-muted-foreground" aria-hidden />
                <p className="text-sm font-medium">{scan.name}</p>
                <RiskBadge risk={scan.maxRisk} />
              </div>
              <p className="mt-1 text-xs text-muted-foreground">
                {scan.description || t("skills.noDescription")}
              </p>
              <p className="mt-2 text-[0.6875rem] text-muted-foreground">
                {t("skills.scanSummary", { files: scan.files.length, size: formatBytes(scan.byteSize) })}
              </p>
            </div>

            {scan.risks.length > 0 ? (
              <div className="flex flex-col gap-2">
                <p className="text-xs font-medium">{t("skills.risks")}</p>
                {scan.risks.map((risk, index) => (
                  <div
                    key={`${risk.code}-${risk.path}-${index}`}
                    className={cn(
                      "rounded-lg border p-2.5 text-xs",
                      risk.severity === "danger"
                        ? "border-destructive/30 bg-destructive/5"
                        : "border-amber-500/30 bg-amber-500/5",
                    )}
                  >
                    <div className="flex items-center gap-2">
                      <RiskBadge risk={risk.severity} />
                      <span className="font-mono text-[0.6875rem] text-muted-foreground">
                        {risk.path}{risk.line ? `:${risk.line}` : ""}
                      </span>
                    </div>
                    <p className="mt-1 text-muted-foreground">{risk.message}</p>
                  </div>
                ))}
              </div>
            ) : (
              <p className="text-xs text-emerald-600">{t("skills.noRisks")}</p>
            )}

            {scan.maxRisk === "danger" ? (
              <div className="flex items-start gap-2 rounded-lg border border-destructive/30 bg-destructive/5 p-3">
                <TriangleAlert className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />
                <p className="text-xs text-muted-foreground">{t("skills.dangerConfirm")}</p>
              </div>
            ) : null}
          </div>
        ) : null}

        {dialogError && <p className="text-xs text-destructive">{dialogError}</p>}

        <DialogFooter>
          <Button variant="outline" size="sm" isDisabled={installing} onPress={closeUpload}>
            {t("common.cancel")}
          </Button>
          <Button
            size="sm"
            variant={scan?.maxRisk === "danger" ? "destructive" : "default"}
            isDisabled={!scan || scanning || installing}
            onPress={() => void confirmInstall()}
          >
            {installing ? <Loader2 className="size-3.5 animate-spin" aria-hidden /> : null}
            {scan?.maxRisk === "danger" ? t("skills.installDanger") : t("skills.install")}
          </Button>
        </DialogFooter>
      </Dialog>

      <ConfirmDialog
        isOpen={deleting !== null}
        onOpenChange={(open) => { if (!open) setDeleting(null); }}
        title={t("skills.deleteConfirm", { name: deleting?.name ?? "" })}
        onConfirm={async () => {
          if (!deleting) return;
          await remove(deleting.id);
          setDeleting(null);
        }}
      />
    </>
  );
}

function RiskBadge({ risk }: { risk: api.SkillRiskLevel }) {
  const t = useT();
  return (
    <span
      className={cn(
        "rounded px-1.5 py-0.5 text-[0.625rem] font-medium",
        risk === "danger"
          ? "bg-destructive/10 text-destructive"
          : risk === "warning"
            ? "bg-amber-500/10 text-amber-700 dark:text-amber-400"
            : "bg-emerald-500/10 text-emerald-700 dark:text-emerald-400",
      )}
    >
      {risk === "danger"
        ? t("skills.riskDanger")
        : risk === "warning"
          ? t("skills.riskWarning")
          : t("skills.riskSafe")}
    </span>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
