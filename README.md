# Sol

Sol is a device-scoped AI canvas for creating and connecting image, video, and text workflows in the browser. It combines a full-screen node canvas with provider configuration, persistent assets, asynchronous video jobs, realtime updates, agents, MCP tools, and installable Skills.

[中文文档](README.zh-CN.md)

## What Sol provides

- **Visual AI canvas**: Create, edit, persist, and revisit node-based workflows.
- **Multi-provider generation**: Configure OpenAI-compatible, Anthropic, Gemini, image, and video providers from the UI. API keys are encrypted at rest and never returned in plaintext.
- **Images, text, and video**: Generate images and text synchronously; submit video jobs asynchronously and track their progress after leaving the page.
- **Agents and tools**: Run agent sessions against canvas context, with MCP tool approval and on-demand Skill loading.
- **Guest-first accounts**: No registration is required. A first-party device cookie owns guest data, while optional GitHub login links devices to a cloud-persisted account for cross-device access.
- **Realtime communication**: SignalR delivers live events for canvas and agent activity.
- **Sandboxed Skills**: Script Skills run in a separate, network-disabled runner container with resource limits; prose and resource Skills do not require the runner.

## Architecture

```text
Browser (Next.js :3000)
    |  /api/* and /hubs/* via same-origin rewrites
    v
Sol.Api (.NET :5298)
    |-- PostgreSQL  device identity, canvases, providers, agents, Skills metadata and binary payloads
    |-- Redis       cache, presence, locks, optional SignalR backplane
    |-- RabbitMQ    integration events
    `-- Skill runner (isolated container)
```

The backend is split into four projects following Clean Architecture:

```text
Sol.Domain -> Sol.Application -> Sol.Infrastructure -> Sol.Api
```

The API is built for Native AOT. JSON metadata is source-generated, Dapper calls are kept in `Sol.Infrastructure`, and AOT-related diagnostics are promoted to build errors. See [docs/architecture.md](docs/architecture.md) and [docs/aot-constraints.md](docs/aot-constraints.md).

## Requirements

- Docker Desktop with Docker Compose
- .NET SDK `11.0.100-preview.5.26302.115` (pinned by [`global.json`](global.json))
- Node.js and npm, or Bun, for the frontend
- Xcode Command Line Tools on macOS when publishing Native AOT (`xcode-select -p`)

> .NET 11 is currently a preview SDK. The pinned SDK is required because `global.json` disables roll-forward.

## Quick start

1. Start the local dependencies:

   ```bash
   docker compose up -d --build
   docker compose ps
   ```

   This starts PostgreSQL, Redis, RabbitMQ, and the isolated Skill runner. The development host ports are PostgreSQL `5432`, Redis `6380`, RabbitMQ `5673`, and RabbitMQ management UI `15673`.

2. Start the API in a second terminal:

   ```bash
   dotnet run --project src/Sol.Api
   ```

   Development migrations run automatically. Verify readiness:

   ```bash
   curl http://localhost:5298/health/ready
   ```

3. Install and start the web app in a third terminal:

   ```bash
   cd web
   cp .env.example .env.local
   npm install
   npm run dev
   ```

   Open <http://localhost:3000>. The Next.js app rewrites `/api/*` and `/hubs/*` to the API so the device cookie remains first-party.

4. Open the settings dialog and add an AI provider API key before generating content.

For the complete local-development workflow, database checks, realtime verification, and troubleshooting, see [docs/local-development.md](docs/local-development.md).

## Configuration

Development defaults are in [`src/Sol.Api/appsettings.Development.json`](src/Sol.Api/appsettings.Development.json). Production configuration should be supplied through environment variables, user secrets, or an external secret store.

Important settings include:

| Setting | Purpose |
| --- | --- |
| `Postgres:ConnectionString` | PostgreSQL connection string |
| `Redis:Configuration` | Redis endpoint |
| `RabbitMq:*` | RabbitMQ connection and topology settings |
| `DeviceIdentity:Pepper` | Server-side pepper for device identity hashing |
| `Ai:EncryptionKey` | Key used to encrypt stored provider API keys |
| `Ai:AssetRoot` | Legacy local-asset fallback directory; new media is stored in PostgreSQL cloud blobs by default |
| `Realtime:Backplane` | Set to `Redis` when multiple API nodes share a SignalR backplane |
| `Skills:SandboxEnabled` | Enables approved Skill script execution through OpenSandbox |
| `Skills:OpenSandboxDomain` | OpenSandbox API address |
| `Skills:OpenSandboxApiKey` | OpenSandbox API key; keep it in a secret store |
| `Skills:OpenSandboxImage` | OpenSandbox image used for each Skill execution |

Never commit real peppers, encryption keys, provider keys, or connection strings. Rotating `DeviceIdentity:Pepper` invalidates existing device fingerprints; rotating `Ai:EncryptionKey` makes stored provider keys undecryptable unless they are migrated first. Operational guidance is in [docs/operations.md](docs/operations.md).

## Development commands

From the repository root:

```bash
dotnet build Sol.slnx
dotnet test Sol.slnx
```

From `web/`:

```bash
npm run lint
npm run test
npm run test:e2e   # requires the API and dependencies to be running
npm run build
npx tsc --noEmit
```

Playwright tests run serially because they share the API and database. The focused backend unit tests are under [`tests/Sol.UnitTests`](tests/Sol.UnitTests).

To validate the Native AOT publish path on an Apple Silicon Mac:

```bash
dotnet publish src/Sol.Api -c Release -r osx-arm64 /p:PublishAot=true /p:TrimmerSingleWarn=false
```

JIT builds and tests do not fully prove Native AOT compatibility. Read [docs/aot-constraints.md](docs/aot-constraints.md) before changing serialization, reflection, or Dapper code.

## Documentation

- [Architecture](docs/architecture.md)
- [AI and canvas API](docs/ai-canvas-api.md)
- [Device identification and privacy boundaries](docs/device-identification.md)
- [Realtime SignalR contract](docs/realtime-signalr.md)
- [Data model and migrations](docs/data-model.md)
- [Local development](docs/local-development.md)
- [Operations and deployment](docs/operations.md)
- [Native AOT constraints](docs/aot-constraints.md)
- [Architecture decision records](docs/adr/)
- [Frontend architecture](web/README.md)

## Security boundaries

Sol is designed to keep user data scoped to a deterministic `device_id`. Requests without the device cookie are rejected by protected endpoints; probabilistic visitor matching is never used as the ownership key. Provider keys are encrypted with AES-256-GCM, and API responses expose only masked key information.

Uploaded assets are validated by MIME type and file signature. External reference images are not fetched by the server, which avoids turning generation into an SSRF primitive. Skill archives are checked for unsafe paths and extraction limits, while executable Skills are isolated outside the API process.

This project is still under active development. Review the security and operations documentation before exposing an instance to the public internet.

## License

Sol is released under the [MIT License](LICENSE). Copyright © 2026 AIDotNet.
