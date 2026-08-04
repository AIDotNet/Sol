"use client";

import { Loader2, TriangleAlert } from "lucide-react";
import { useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { useAiStore } from "@/features/ai/store";
import { handshake } from "@/lib/device";

/**
 * Surfaces a failed configuration load.
 *
 * The store has always recorded this, but nothing rendered it — so a device whose cookie had
 * been cleared saw an empty provider list and no explanation, which reads as "my settings were
 * deleted" rather than "this browser is no longer recognised".
 *
 * The unauthorized case is recoverable in place: the handshake is idempotent, so re-running it
 * re-issues the cookie and the retry succeeds.
 */
export function ConfigErrorBanner() {
  const t = useT();
  const error = useAiStore((state) => state.error);
  const refresh = useAiStore((state) => state.refresh);

  const [recovering, setRecovering] = useState(false);

  if (!error) return null;

  const unauthorized = error === "unauthorized";

  async function recover() {
    setRecovering(true);
    try {
      if (unauthorized) {
        await handshake();
      }
      await refresh();
    } catch {
      // refresh() records the outcome in the store, so the banner updates either way.
    } finally {
      setRecovering(false);
    }
  }

  return (
    <div className="mx-5 mt-4 flex items-start gap-2.5 rounded-lg border border-destructive/30 bg-destructive/5 p-3">
      <TriangleAlert className="mt-0.5 size-4 shrink-0 text-destructive" aria-hidden />

      <div className="min-w-0 flex-1">
        <p className="text-xs text-destructive">
          {unauthorized ? t("errors.unauthorized") : t("errors.networkError")}
        </p>
        {!unauthorized && <p className="mt-0.5 text-[0.6875rem] text-muted-foreground">{error}</p>}
      </div>

      <Button
        size="sm"
        variant="outline"
        className="shrink-0"
        isDisabled={recovering}
        onPress={() => void recover()}
      >
        {recovering && <Loader2 className="size-3 animate-spin" aria-hidden />}
        {unauthorized ? t("general.resolveDevice") : t("common.retry")}
      </Button>
    </div>
  );
}
