# 本地开发

## 前置

- .NET SDK `11.0.100-preview.5.26302.115`（由 `global.json` 锁定，`rollForward: disable`）
- Docker + Docker Compose
- Node.js（前端）
- Xcode Command Line Tools（AOT 发布需要本机链接器：`xcode-select -p`）

本机**不需要**装 `psql` / `redis-cli`——它们在容器内，用 `docker compose exec` 调用。

## 端口

⚠️ **本项目的 Redis 与 RabbitMQ 使用了偏移端口**，因为开发机上已有其他项目长期占用默认端口。

| 服务 | 宿主端口 | 容器内 | 备注 |
|---|---|---|---|
| PostgreSQL | **5432** | 5432 | 默认 |
| Redis | **6380** | 6379 | 偏移 |
| RabbitMQ | **5673** | 5672 | 偏移 |
| RabbitMQ 管理台 | **15673** | 15672 | http://localhost:15673 （sol / sol_dev_password） |
| Sol.Api | 5298 | | |
| Next.js | 3000 | | |

若你的机器没有冲突，可以把 `docker-compose.yml` 与 `appsettings.Development.json` 改回默认端口。

## 启动

```bash
docker compose up -d
docker compose ps          # 三个服务都应为 (healthy)

dotnet run --project src/Sol.Api
# 开发环境 RunMigrationsOnStartup=true，会自动建表

curl http://localhost:5298/health/ready
```

前端：

```bash
cd web && npm run dev      # http://localhost:3000
```

`web/next.config.ts` 已配置 rewrites，把 `/api/*` 与 `/hubs/*` 代理到 `:5298`，使浏览器只看到一个 origin——这是设备 Cookie 能工作的前提（`SameSite=Lax` 跨源不发送；`SameSite=None` 要求 `Secure`，而明文 HTTP 下 `Secure` 被拒绝）。

## 验证

```bash
# 设备握手
curl -s -X POST http://localhost:5298/api/v1/device/handshake \
  -H 'Content-Type: application/json' -c /tmp/sol.jar \
  -d '{"v":1,"stable":{"timeZone":"Asia/Shanghai","platform":"MacIntel","hardwareConcurrency":8,
       "deviceMemoryGb":8,"screen":{"w":1512,"h":982,"colorDepth":24,"dpr":2},
       "gpu":{"vendor":"Apple","renderer":"Apple M1 Pro"},"primaryLanguage":"zh"},
       "volatile":{"userAgent":"curl-A","canvasHash":"aaa"},"clientStoredId":null}'

# 带 Cookie 重放 → 应返回同一 deviceId、confidence 1.0
curl -s -X POST http://localhost:5298/api/v1/device/handshake \
  -H 'Content-Type: application/json' -b /tmp/sol.jar -d '…同上…'

# 跨浏览器：stable 相同、volatile 不同、无 Cookie
#   → 新 deviceId、同 visitorId、confidence 0.6
```

数据库 / Redis / MQ（无需本机 CLI）：

```bash
docker compose exec -T postgres psql -U sol -d sol -c "SELECT * FROM schema_version ORDER BY version;"
docker compose exec -T postgres psql -U sol -d sol -c "SELECT * FROM device_link;"
docker compose exec -T redis redis-cli KEYS 'sol:*'
docker compose exec -T rabbitmq rabbitmqctl list_queues name messages consumers
```

实时通信：

```bash
cd web && node scripts/verify-realtime.mjs
```

## AOT 发布验证

**JIT 模式通过不能证明 AOT 可用**，参见 [aot-constraints.md](aot-constraints.md)。

```bash
dotnet publish src/Sol.Api -c Release -r osx-arm64 /p:PublishAot=true /p:TrimmerSingleWarn=false

# 必须 cd 进发布目录再运行：原生二进制以当前工作目录为 content root，
# 在别处启动会读不到 appsettings，表现为"连接串未配置"
cd src/Sol.Api/bin/Release/net11.0/osx-arm64/publish
ASPNETCORE_ENVIRONMENT=Development ./Sol.Api --urls http://localhost:5299
```

## 密钥

`DeviceIdentity:Pepper` 在开发环境有占位值，**生产必须替换**：

```bash
dotnet user-secrets set "DeviceIdentity:Pepper" "$(openssl rand -base64 32)" --project src/Sol.Api
# 或环境变量：DeviceIdentity__Pepper=…
```

⚠️ 轮换 pepper 会作废全部指纹与概率性关联，见 [device-identification.md](device-identification.md#运维注意)。

## 排错

| 症状 | 原因 |
|---|---|
| `Postgres:ConnectionString is not configured` | 原生二进制不在发布目录内运行（content root 不对） |
| 端口已被占用 | 其他项目占用了 6379/5672/15672，本项目已偏移到 6380/5673/15673 |
| RabbitMQ `ACCESS_REFUSED` | 连到了别的 RabbitMQ 实例，检查端口是否为 5673 |
| Postgres 容器启动失败提示 data 目录 | postgres:18+ 要求挂载点是 `/var/lib/postgresql`（不带 `/data`） |
| SignalR 连上后立即断开 | Hub 要求设备 Cookie，先调用握手接口 |
| 开发环境 WebSocket 不通 | Next.js rewrite 的 WS 升级不可靠，可直连 5298 或回退 SSE/长轮询 |
| Dapper 运行时报 `PlatformNotSupportedException` | 用了 Dapper.AOT 不支持的 API，见 [aot-constraints.md](aot-constraints.md) |

## 重置数据

```bash
docker compose exec -T postgres psql -U sol -d sol -c "TRUNCATE device_link, device, visitor CASCADE;"
docker compose down -v    # 连同数据卷一起删除
```
