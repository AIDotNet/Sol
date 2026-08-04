import Link from "next/link";
import { buttonVariants } from "@/components/ui/button-variants";
import { EmptyState } from "@/components/ui/empty-state";
import { cn } from "@/lib/utils";

export default function NotFound() {
  return (
    <EmptyState
      title="Page not found"
      description="The page you're looking for doesn't exist or was moved."
      action={
        <Link href="/" className={cn(buttonVariants({ variant: "outline", size: "sm" }))}>
          Back to home
        </Link>
      }
    />
  );
}
