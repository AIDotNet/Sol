# Sol

Sol 是一个以设备为边界的 AI 画布，用于在浏览器中创建和连接图片、视频与文本工作流。它将全屏节点画布、渠道配置、持久化素材、异步视频任务、实时更新、Agent、MCP 工具和可安装 Skills 组合在一起。

[English README](README.md)

## 功能概览

- **可视化 AI 画布**：创建、编辑、持久化并重新打开基于节点的工作流。
- **多渠道生成**：在界面中配置 OpenAI 兼容、Anthropic、Gemini、图片和视频渠道。API Key 加密存储，永不以明文返回。
- **图片、文本与视频**：图片和文本支持同步生成；视频以异步任务提交，即使离开页面也能继续追踪进度。
- **Agent 与工具**：基于画布上下文运行 Agent 会话；MCP 工具需要审批，Skill 内容按需加载。
- **游客 + 账号模式**：默认无需注册即可使用；支持 GitHub 登录。登录后将当前设备关联到账号，账号关联的设备共享云端数据。
- **多第三方登录设计**：OAuth provider 采用统一适配器和 `provider_key + subject` 映射，后续可增加 Google、Microsoft 或企业 OIDC，不把 provider 字段硬编码进账号表。
- **实时通信**：使用 SignalR 推送画布和 Agent 活动事件。
- **隔离执行 Skills**：脚本 Skill 在独立、无网络的 runner 容器中执行，并受资源限制；文档型和资源型 Skill 不依赖 runner。

## 架构

```text
浏览器 (Next.js :3000)
    |  通过同源 rewrite 代理 /api/* 和 /hubs/*
    v
Sol.Api (.NET :5298)
    |-- PostgreSQL  设备/账号身份、OAuth session、画布、渠道、Agent、Skill 元数据与二进制 payload
    |-- Redis       缓存、在线状态、锁、可选 SignalR backplane
    |-- RabbitMQ    集成事件
    `-- Skill runner（隔离容器）
```

后端按 Clean Architecture 拆分为四个项目：

```text
Sol.Domain -> Sol.Application -> Sol.Infrastructure -> Sol.Api
```

API 面向 Native AOT 构建。JSON 元数据使用源生成，Dapper 调用集中在 `Sol.Infrastructure`，并将 AOT 相关诊断提升为构建错误。详见 [docs/architecture.md](docs/architecture.md) 和 [docs/aot-constraints.md](docs/aot-constraints.md)。

## 环境要求

- Docker Desktop 与 Docker Compose
- .NET SDK `11.0.100-preview.5.26302.115`（由 [`global.json`](global.json) 锁定）
- Node.js 与 npm，或使用 Bun 管理前端
- macOS 发布 Native AOT 时需要 Xcode Command Line Tools（`xcode-select -p`）

> 当前使用的是 .NET 11 preview SDK。由于 `global.json` 禁止自动 roll-forward，必须使用锁定版本。

## 快速开始

1. 启动本地依赖：

   ```bash
   docker compose up -d --build
   docker compose ps
   ```

   该命令会启动 PostgreSQL、Redis、RabbitMQ 和隔离的 Skill runner。开发环境宿主端口为：PostgreSQL `5432`、Redis `6380`、RabbitMQ `5673`，RabbitMQ 管理台 `15673`。

2. 在第二个终端启动 API：

   ```bash
   dotnet run --project src/Sol.Api
   ```

   开发环境会自动执行数据库迁移。检查就绪状态：

   ```bash
   curl http://localhost:5298/health/ready
   ```

3. 在第三个终端安装并启动前端：

   ```bash
   cd web
   cp .env.example .env.local
   npm install
   npm run dev
   ```

   打开 <http://localhost:3000>。Next.js 会把 `/api/*` 和 `/hubs/*` rewrite 到 API，使设备 Cookie 始终保持第一方 Cookie。

4. 打开设置对话框并添加 AI 渠道 API Key，然后才能生成内容。
5. 如需启用 GitHub 登录，按 [docs/authentication.md](docs/authentication.md) 配置 OAuth App 和环境变量；未配置时仍保持游客模式。

完整的本地开发流程、数据库检查、实时通信验证和排错说明见 [docs/local-development.md](docs/local-development.md)。

## 配置

开发默认配置位于 [`src/Sol.Api/appsettings.Development.json`](src/Sol.Api/appsettings.Development.json)。生产环境应通过环境变量、user secrets 或外部密钥管理服务提供配置。

重要配置包括：

| 配置项 | 作用 |
| --- | --- |
| `Postgres:ConnectionString` | PostgreSQL 连接字符串 |
| `Redis:Configuration` | Redis 地址 |
| `RabbitMq:*` | RabbitMQ 连接与拓扑配置 |
| `DeviceIdentity:Pepper` | 设备身份哈希使用的服务端 pepper |
| `Authentication:PublicOrigin` | OAuth 回调使用的浏览器 origin |
| `Authentication:GitHub:*` | GitHub OAuth 开关、Client ID、Client Secret 与 scope |
| `Ai:EncryptionKey` | 加密保存渠道 API Key 的密钥 |
| `Ai:AssetRoot` | 旧版本本地素材的兼容读取目录；新素材默认写入 PostgreSQL 云端 blob |
| `Realtime:Backplane` | 多个 API 节点时设为 `Redis` 以启用 SignalR backplane |
| `Skills:SandboxEnabled` | 是否通过 OpenSandbox 执行已审批的 Skill 脚本 |
| `Skills:OpenSandboxDomain` | OpenSandbox API 地址 |
| `Skills:OpenSandboxApiKey` | OpenSandbox API Key；应保存在密钥管理服务中 |
| `Skills:OpenSandboxImage` | 每次 Skill 执行使用的 OpenSandbox 镜像 |

不要提交真实 pepper、加密密钥、渠道 API Key 或连接字符串。轮换 `DeviceIdentity:Pepper` 会使已有设备指纹失效；轮换 `Ai:EncryptionKey` 会导致已存储的渠道 Key 无法解密，除非先完成迁移。运维说明见 [docs/operations.md](docs/operations.md)。

## 开发命令

在仓库根目录执行：

```bash
dotnet build Sol.slnx
dotnet test Sol.slnx
```

在 `web/` 目录执行：

```bash
npm run lint
npm run test
npm run test:e2e   # 需要 API 和依赖服务已启动
npm run build
npx tsc --noEmit
```

Playwright 测试会串行执行，因为它们共享 API 和数据库。后端单元测试位于 [`tests/Sol.UnitTests`](tests/Sol.UnitTests)。

在 Apple Silicon Mac 上验证 Native AOT 发布路径：

```bash
dotnet publish src/Sol.Api -c Release -r osx-arm64 /p:PublishAot=true /p:TrimmerSingleWarn=false
```

JIT 构建和测试不能完全证明 Native AOT 兼容性。修改序列化、反射或 Dapper 代码前，请先阅读 [docs/aot-constraints.md](docs/aot-constraints.md)。

## 文档索引

- [架构](docs/architecture.md)
- [AI 与画布 API](docs/ai-canvas-api.md)
- [设备识别与隐私边界](docs/device-identification.md)
- [SignalR 实时通信契约](docs/realtime-signalr.md)
- [数据模型与迁移](docs/data-model.md)
- [本地开发](docs/local-development.md)
- [运维与部署](docs/operations.md)
- [Native AOT 约束](docs/aot-constraints.md)
- [架构决策记录](docs/adr/)
- [前端架构](web/README.md)

## 安全边界

Sol 默认使用确定性的 `device_id` 隔离游客数据。GitHub 登录成功后，账号通过 `sol_account_device` 关联多个确定性设备；只有匹配的账号 session 才能激活账号范围，概率性访客匹配不会被用作资源归属键。渠道 Key 使用 AES-256-GCM 加密，API 响应只暴露掩码后的 Key 信息。完整的 OAuth 与多 provider 约束见 [docs/authentication.md](docs/authentication.md)。

上传素材会同时校验 MIME 类型和文件签名，并默认将图片/视频二进制与 Skill 包文件写入 PostgreSQL 云端 blob；旧版本的本地目录仅作为迁移兼容读取。服务端不会拉取外部参考图，避免把生成端点变成 SSRF 跳板。Skill 压缩包会检查危险路径和解压限制，可执行 Skill 在 API 进程之外隔离运行。

项目仍在积极开发中。将实例暴露到公网前，请先阅读安全和运维文档。

## 许可证

Sol 使用 [MIT License](LICENSE) 发布。版权所有 © 2026 AIDotNet。
