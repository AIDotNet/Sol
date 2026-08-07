"use client"

import * as React from "react"
import {
  Tooltip as TooltipPrimitive,
  TooltipTrigger as TooltipTriggerPrimitive,
  type TooltipProps as TooltipPrimitiveProps,
  type TooltipTriggerComponentProps as TooltipTriggerPrimitiveProps,
} from "react-aria-components"

import { cn } from "@/lib/utils"

function TooltipTrigger({
  delay = 300,
  closeDelay = 0,
  ...props
}: TooltipTriggerPrimitiveProps) {
  return (
    <TooltipTriggerPrimitive
      data-slot="tooltip-trigger"
      delay={delay}
      closeDelay={closeDelay}
      {...props}
    />
  )
}

function Tooltip({
  className,
  offset = 6,
  ...props
}: Omit<TooltipPrimitiveProps, "className"> & { className?: string }) {
  return (
    <TooltipPrimitive
      data-slot="tooltip"
      offset={offset}
      className={cn(
        "z-50 max-w-64 text-balance rounded-md bg-foreground px-2 py-1 text-xs text-background shadow-md duration-100 data-entering:animate-in data-entering:fade-in-0 data-entering:zoom-in-95 data-exiting:animate-out data-exiting:fade-out-0 data-exiting:zoom-out-95",
        className
      )}
      {...props}
    />
  )
}

export {
  type TooltipPrimitiveProps,
  type TooltipTriggerPrimitiveProps,
  Tooltip,
  TooltipTrigger,
}
