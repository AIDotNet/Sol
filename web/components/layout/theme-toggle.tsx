"use client";

import { Moon, Sun } from "lucide-react";
import { useTheme } from "next-themes";
import { useSyncExternalStore } from "react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipTrigger } from "@/components/ui/tooltip";

function subscribe() {
  return () => {};
}

// Hydration-safe "has this rendered on the client yet" check — avoids the
// setState-in-effect cascading-render pattern for gating theme-dependent UI.
function useMounted() {
  return useSyncExternalStore(
    subscribe,
    () => true,
    () => false,
  );
}

export function ThemeToggle() {
  const { resolvedTheme, setTheme } = useTheme();
  const mounted = useMounted();

  if (!mounted) {
    return <Button variant="ghost" size="icon" aria-label="Toggle theme" isDisabled />;
  }

  const isDark = resolvedTheme === "dark";

  return (
    <TooltipTrigger>
      <Button
        variant="ghost"
        size="icon"
        aria-label="Toggle theme"
        onPress={() => setTheme(isDark ? "light" : "dark")}
      >
        {isDark ? <Sun className="size-4" /> : <Moon className="size-4" />}
      </Button>
      <Tooltip>{isDark ? "Switch to light theme" : "Switch to dark theme"}</Tooltip>
    </TooltipTrigger>
  );
}
