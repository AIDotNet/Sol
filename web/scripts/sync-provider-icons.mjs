/**
 * Copies the provider logos we use out of @lobehub/icons-static-svg and into public/.
 *
 * The package ships ~1000 raw .svg files. Next.js can't import an .svg as a component without a
 * loader, and we don't want the whole set in the bundle, so the handful we reference are copied
 * to public/provider-icons/ and served as plain images.
 *
 * Run after adding a preset with a new iconSlug:  node scripts/sync-provider-icons.mjs
 */
import { copyFileSync, existsSync, mkdirSync, readdirSync, rmSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const source = join(here, "..", "node_modules", "@lobehub", "icons-static-svg", "icons");
const target = join(here, "..", "public", "provider-icons");

/** Slugs referenced by features/ai/presets and by detectModelIconSlug. */
const SLUGS = [
  "openai",
  "claude",
  "gemini",
  "doubao",
  "siliconcloud",
  "grok",
  "openrouter",
  "deepseek",
  "ollama",
  "qwen",
  "zhipu",
  "minimax",
  "moonshot",
  "azure",
  "meta",
  "mistral",
];

if (!existsSync(source)) {
  console.error(`Icon package not found at ${source} — run npm install first.`);
  process.exit(1);
}

const available = new Set(readdirSync(source));

rmSync(target, { recursive: true, force: true });
mkdirSync(target, { recursive: true });

const copied = [];
const missing = [];

for (const slug of SLUGS) {
  // Prefer the brand-coloured variant; several slugs only ship a monochrome file.
  const file = available.has(`${slug}-color.svg`) ? `${slug}-color.svg` : `${slug}.svg`;

  if (!available.has(file)) {
    missing.push(slug);
    continue;
  }

  copyFileSync(join(source, file), join(target, `${slug}.svg`));
  copied.push(slug);
}

console.log(`Copied ${copied.length} provider icons to public/provider-icons/`);
if (missing.length > 0) {
  console.warn(`No icon found for: ${missing.join(", ")}`);
}
