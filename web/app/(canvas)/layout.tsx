/**
 * Full-bleed shell for the canvas. No header, no footer, no max-width — the graph editor owns
 * the entire viewport and manages its own floating panels.
 *
 * `overflow-hidden` matters: the canvas pans by transforming an oversized child, and without it
 * the page itself would scroll instead.
 */
export default function CanvasLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return <div className="h-dvh w-dvw overflow-hidden">{children}</div>;
}
