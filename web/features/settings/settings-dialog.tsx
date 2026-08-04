"use client";

import { Boxes, Cpu, Plug, Settings2, SlidersHorizontal } from "lucide-react";
import { useEffect, useState } from "react";
import { Dialog } from "@/components/ui/dialog";
import { useT } from "@/components/providers/i18n-provider";
import { useAiStore } from "@/features/ai/store";
import { ConfigErrorBanner } from "@/features/settings/config-error-banner";
import { GeneralPanel } from "@/features/settings/panels/general-panel";
import { McpPanel } from "@/features/settings/panels/mcp-panel";
import { ModelConfigPanel } from "@/features/settings/panels/model-config-panel";
import { ModelPanel } from "@/features/settings/panels/model-panel";
import { ProviderPanel } from "@/features/settings/panels/provider-panel";
import { cn } from "@/lib/utils";

export type SettingsTab = "general" | "providers" | "models" | "modelConfig" | "mcp";

const NAV_GROUPS: ReadonlyArray<{
  labelKey: "settings.navGroups.basic" | "settings.navGroups.ai" | "settings.navGroups.extensions";
  items: ReadonlyArray<{
    id: SettingsTab;
    labelKey:
      | "settings.nav.general"
      | "settings.nav.providers"
      | "settings.nav.models"
      | "settings.nav.modelConfig"
      | "settings.nav.mcp";
    icon: typeof Settings2;
  }>;
}> = [
  {
    labelKey: "settings.navGroups.basic",
    items: [{ id: "general", labelKey: "settings.nav.general", icon: Settings2 }],
  },
  {
    labelKey: "settings.navGroups.ai",
    items: [
      { id: "providers", labelKey: "settings.nav.providers", icon: Boxes },
      { id: "models", labelKey: "settings.nav.models", icon: Cpu },
      { id: "modelConfig", labelKey: "settings.nav.modelConfig", icon: SlidersHorizontal },
    ],
  },
  {
    labelKey: "settings.navGroups.extensions",
    items: [{ id: "mcp", labelKey: "settings.nav.mcp", icon: Plug }],
  },
];

export function SettingsDialog({
  isOpen,
  onOpenChange,
  initialTab = "general",
}: {
  isOpen: boolean;
  onOpenChange: (open: boolean) => void;
  initialTab?: SettingsTab;
}) {
  const t = useT();
  const [tab, setTab] = useState<SettingsTab>(initialTab);
  const load = useAiStore((state) => state.load);

  // Config is fetched when the dialog first opens rather than on mount, so a user who never
  // opens settings never pays for it.
  useEffect(() => {
    if (isOpen) {
      void load();
    }
  }, [isOpen, load]);

  // Re-syncing the tab on open is a render-phase concern, not an effect: comparing against the
  // previous value avoids the extra render pass setState-in-effect would trigger.
  const [lastOpen, setLastOpen] = useState(isOpen);
  if (isOpen !== lastOpen) {
    setLastOpen(isOpen);
    if (isOpen) setTab(initialTab);
  }

  return (
    <Dialog
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      // Master-detail needs room: the panels manage their own internal scrolling, so the
      // dialog is a fixed-height frame rather than a growing column.
      className="h-[min(42rem,calc(100dvh-4rem))] w-[min(64rem,calc(100vw-2rem))] max-w-none gap-0 overflow-hidden p-0 sm:max-w-none"
      showCloseButton={false}
    >
      <div className="flex h-full min-h-0">
        <nav className="flex w-52 shrink-0 flex-col gap-4 overflow-y-auto border-r bg-muted/20 p-3">
          <p className="px-2 pt-1 text-sm font-semibold">{t("settings.title")}</p>

          {NAV_GROUPS.map((group) => (
            <div key={group.labelKey} className="flex flex-col gap-0.5">
              <p className="px-2 pb-1 text-xs font-medium text-muted-foreground">
                {t(group.labelKey)}
              </p>

              {group.items.map((item) => {
                const Icon = item.icon;
                const active = tab === item.id;

                return (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => setTab(item.id)}
                    aria-current={active ? "page" : undefined}
                    className={cn(
                      "flex items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition-colors",
                      active
                        ? "bg-accent font-medium text-accent-foreground"
                        : "text-muted-foreground hover:bg-accent/50 hover:text-foreground",
                    )}
                  >
                    <Icon className="size-4 shrink-0" aria-hidden />
                    {t(item.labelKey)}
                  </button>
                );
              })}
            </div>
          ))}
        </nav>

        <div className="flex min-h-0 min-w-0 flex-1 flex-col">
          {/* Above the panels, so a failed load is explained wherever the user happens to be
              rather than only on the one that looks empty. */}
          <ConfigErrorBanner />

          {tab === "general" && <GeneralPanel />}
          {tab === "providers" && <ProviderPanel />}
          {tab === "models" && <ModelPanel />}
          {tab === "modelConfig" && <ModelConfigPanel />}
          {tab === "mcp" && <McpPanel />}
        </div>
      </div>
    </Dialog>
  );
}
