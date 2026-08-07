"use client";

import { Cloud, GitBranch, Link2, Loader2, LogIn, LogOut } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useT } from "@/components/providers/i18n-provider";
import { Button } from "@/components/ui/button";
import { useAuth } from "@/features/auth/auth-provider";
import { cn } from "@/lib/utils";

export function AuthMenu() {
  const t = useT();
  const { account, providers, loading, error, isGuest, login, link, logout } = useAuth();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const github = providers.find((provider) => provider.key === "github");

  useEffect(() => {
    if (!open) return;

    const close = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [open]);

  if (loading) {
    return (
      <Button size="sm" variant="ghost" aria-label={t("auth.loading")} isDisabled>
        <Loader2 className="size-3.5 animate-spin" aria-hidden />
      </Button>
    );
  }

  const label = account?.displayName ?? t("auth.guest");
  const initials = account?.displayName.trim().slice(0, 1).toUpperCase() ?? "G";

  return (
    <div ref={rootRef} className="relative">
      <Button
        size="sm"
        variant="ghost"
        aria-expanded={open}
        aria-haspopup="menu"
        onPress={() => setOpen((current) => !current)}
        className="max-w-36 gap-1.5 px-2"
      >
        {account?.avatarUrl ? (
          <span
            aria-hidden
            className="size-4 rounded-full bg-cover bg-center"
            style={{ backgroundImage: `url(${JSON.stringify(account.avatarUrl)})` }}
          />
        ) : account ? (
          <span className="flex size-4 items-center justify-center rounded-full bg-foreground text-[0.6rem] text-background">
            {initials}
          </span>
        ) : (
          <Cloud className="size-3.5 text-muted-foreground" aria-hidden />
        )}
        <span className="truncate text-xs">{label}</span>
      </Button>

      {open && (
        <div
          role="menu"
          className="canvas-menu absolute top-full right-0 z-40 mt-2 flex w-64 origin-top-right flex-col gap-2 rounded-xl border bg-popover p-3 text-popover-foreground shadow-xl"
        >
          <div className="flex items-start gap-2">
            {account ? (
              <div className="flex size-8 shrink-0 items-center justify-center overflow-hidden rounded-full bg-muted text-sm font-medium">
                {account.avatarUrl ? (
                  <span
                    aria-hidden
                    className="size-full bg-cover bg-center"
                    style={{ backgroundImage: `url(${JSON.stringify(account.avatarUrl)})` }}
                  />
                ) : (
                  initials
                )}
              </div>
            ) : (
              <div className="flex size-8 shrink-0 items-center justify-center rounded-full bg-muted">
                <Cloud className="size-4 text-muted-foreground" aria-hidden />
              </div>
            )}
            <div className="min-w-0">
              <p className="truncate text-sm font-medium">{label}</p>
              <p className="text-xs text-muted-foreground">
                {isGuest ? t("auth.guestDescription") : t("auth.cloudAccount")}
              </p>
            </div>
          </div>

          <div className="h-px bg-border" />

          {isGuest ? (
            github ? (
              <Button
                size="sm"
                variant="default"
                onPress={() => login(github.key)}
                className="w-full justify-start"
              >
                <GitBranch className="size-3.5" aria-hidden />
                {t("auth.loginWith", { provider: github.displayName })}
              </Button>
            ) : (
              <p className="px-1 text-xs leading-5 text-muted-foreground">
                {t("auth.providerUnavailable")}
              </p>
            )
          ) : (
            <>
              {github && account && !account.externalProviders.includes(github.key) && (
                <Button
                  size="sm"
                  variant="outline"
                  onPress={() => link(github.key)}
                  className="w-full justify-start"
                >
                  <Link2 className="size-3.5" aria-hidden />
                  {t("auth.linkProvider", { provider: github.displayName })}
                </Button>
              )}
              <Button
                size="sm"
                variant="ghost"
                onPress={() => void logout()}
                className="w-full justify-start text-muted-foreground hover:text-foreground"
              >
                <LogOut className="size-3.5" aria-hidden />
                {t("auth.logout")}
              </Button>
            </>
          )}

          {error && (
            <p className={cn("px-1 text-xs leading-5 text-destructive")}>
              {t("auth.loginFailed")}
            </p>
          )}

          {isGuest && github && (
            <p className="flex items-center gap-1 px-1 text-[0.6875rem] text-muted-foreground">
              <LogIn className="size-3" aria-hidden />
              {t("auth.loginHint")}
            </p>
          )}
        </div>
      )}
    </div>
  );
}
