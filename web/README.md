This is the frontend for **Sol**, built with [Next.js](https://nextjs.org) (App Router), React 19, TypeScript, Tailwind CSS v4, and [shadcn/ui](https://ui.shadcn.com).

The whole app is one page: a node canvas for generating images and video, at `/`.

## Architecture

- **The canvas is the app.** `/` renders it full-bleed with no site chrome. Which document is
  open is resolved client-side (last opened → most recent → create), so there is no id in the
  URL and no redirect on the way in.
- **The browser never talks to a model vendor.** It posts a provider id and a prompt to the .NET
  API, which decrypts the key, calls upstream and stores the result. That keeps API keys off the
  client, sidesteps CORS, and works with providers that allow-list egress IPs.
- **Everything is device-scoped.** There are no accounts; the device cookie from
  `POST /api/v1/device/handshake` is the only credential. See
  [`docs/device-identification.md`](../docs/device-identification.md).
- Feature code lives under `features/<domain>`; shared primitives under `components/ui` and
  `components/layout`. `features/canvas` owns the graph, `features/ai` the provider config,
  `features/settings` the settings dialog.
- API calls run from the browser against the Next.js rewrite (`/api/*` → the .NET API) so the
  device cookie stays first-party. See `next.config.ts` for why that rewrite exists.

The API surface is documented in [`docs/ai-canvas-api.md`](../docs/ai-canvas-api.md).

## Getting Started

The app needs the .NET API and its dependencies:

```bash
# Terminal 1 — Postgres, Redis, RabbitMQ
docker compose up -d

# Terminal 2 — .NET API (http://localhost:5298)
dotnet run --project ../src/Sol.Api

# Terminal 3 — Next.js app (http://localhost:3000)
npm run dev
```

Copy `.env.example` to `.env.local` (already present in this checkout) and adjust if the API
runs on a different port.

Before anything can be generated, open the settings dialog (gear icon, top right) and add a
provider with an API key.

## Testing

```bash
npm run test        # Vitest — canvas graph logic, sync conflicts, config parsing
npm run test:e2e    # Playwright — requires the .NET API running on :5298
```

Playwright runs serially: every spec drives the same API and database, so concurrency produces
contention rather than coverage.

## Other scripts

```bash
npm run lint      # ESLint
npx tsc --noEmit  # Type-check
npm run build     # Production build
```
