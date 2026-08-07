"use client";

import { Button } from "@/components/ui/button";

interface ErrorStateProps {
  title?: string;
  description?: string;
  onRetry?: () => void;
}

export function ErrorState({
  title = "Something went wrong",
  description,
  onRetry,
}: ErrorStateProps) {
  return (
    <div className="canvas-fade-in flex flex-col items-center justify-center gap-2 rounded-lg border border-dashed py-16 text-center">
      <p className="text-sm font-medium">{title}</p>
      {description ? (
        <p className="max-w-sm text-sm text-muted-foreground">{description}</p>
      ) : null}
      {onRetry ? (
        <Button variant="outline" size="sm" className="mt-2" onPress={onRetry}>
          Try again
        </Button>
      ) : null}
    </div>
  );
}
