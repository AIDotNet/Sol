# 实时通信（SignalR）

## AOT 前提

**SignalR 服务端与 Native AOT 兼容**（.NET 9 起官方兼容表标记为"部分支持"）。网上"SignalR 不支持 AOT ❌"的说法源自 **.NET 8 的旧文档**，至今仍能在 `?view=aspnetcore-8.0` 下看到。

代价是若干硬约束，见 [aot-constraints.md](aot-constraints.md)。最重要的一条：

> **禁止 `Hub<TClient>` 与 `IHubContext<THub,TClient>`。** 其 `TypedClientBuilder<T>` 用 `Reflection.Emit` 动态生成客户端代理，标注 `[RequiresDynamicCode]`，编译告警 + 运行时抛异常。

## Hub 契约

`SolHub : Hub`（普通 Hub），挂载在 `/hubs/sol`。

**服务端 → 客户端**（方法名集中在 `RealtimeMethods`，因为失去了强类型 Hub 的编译期检查）：

| 方法 | 载荷 | 说明 |
|---|---|---|
| `ReceiveMessage` | `RealtimeEnvelope` | 通用消息信封 |
| `DeviceLinked` | `RealtimeEnvelope` | 设备被关联到访客 |

**客户端 → 服务端**：

| 方法 | 返回 | 说明 |
|---|---|---|
| `Ping` | `Task<string>` → `"pong"` | 传输连通性检查 |

新增 Hub 方法时的约束：返回值只能是 `Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`；**禁止 `IAsyncEnumerable<T>`/`ChannelReader<T>` 且 `T` 为结构体**（这会在**启动时**抛异常，直接拖垮整个应用，而不是等到调用时）。

## 发送模式

```csharp
// AOT 安全：字符串式方法名
await hub.Clients.Group(HubGroups.ForDevice(deviceId))
         .SendAsync(RealtimeMethods.ReceiveMessage, envelope, ct);
```

应用层通过 `IRealtimeNotifier` 发送，不直接接触 `IHubContext`，magic string 因此被限制在 `Sol.Api/Hubs/HubGroups.cs` 一处。

**所有过线类型必须在 `ApiJsonContext` 中有 `[JsonSerializable]` 条目**，且注册方式必须是：

```csharp
options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
```

注意是 `Insert(0, …)` 而**不是**给 `TypeInfoResolver` 赋值——赋值会丢弃 SignalR 为自身协议类型安装的 resolver。

## 连接与设备绑定

不使用 `IUserIdProvider`——它需要 `ClaimsPrincipal`，而本项目无账号体系。改用分组：

```
OnConnectedAsync:
    从 Cookie 读 sol_did
    缺失或非法 → Context.Abort()      ← 强制客户端先完成握手
    加入 device:{deviceId}
    加入 visitor:{visitorId}（若已解析）
    IPresenceTracker.MarkOnline(deviceId, connectionId)

OnDisconnectedAsync:
    IPresenceTracker.MarkOffline(...)  ← 用 CancellationToken.None，
                                          此时连接令牌已取消，但清理必须完成
```

分组命名：`device:{guid:N}` / `visitor:{guid:N}`。分组天然支持 backplane，单机与集群无需改代码。

⚠️ **发送到 `visitor:` 分组时要谨慎**：该分组内除第一台设备外都是概率性关联的，**不要发送敏感内容**。

## 在线状态

Redis Set 而非计数器：一台设备可能有多个连接（多标签页），而计数器在进程崩溃、未执行断开处理时会永久漂移。Set 成员操作幂等，且带 12 小时 TTL 兜底清理失败的场景。

## Backplane

由配置开关控制：

```json
{ "Realtime": { "Backplane": "None" } }   // 或 "Redis"
```

**默认 `None`**。这不是因为它不工作——[实测](aot-constraints.md)在原生 AOT 二进制上完全可用，Redis 中出现真实频道且零错误——而是因为单机部署不需要它，且微软未对该包做 AOT 认证（其 csproj 没有 `IsTrimmable`/`IsAotCompatible` 标记）。

需要多节点时改为 `Redis` 即可，代码无需改动。

## 客户端接入

```ts
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/sol')          // 经 Next.js rewrite 代理，同源
  .withAutomaticReconnect()
  .build();

connection.on('ReceiveMessage', (envelope) => { /* … */ });
await connection.start();
```

**必须先调用 `/api/v1/device/handshake` 拿到 Cookie**，否则 Hub 会拒绝连接。

⚠️ **开发环境的 WebSocket 升级**：Next.js rewrites 代理 HTTP 可靠，但 WebSocket 升级历来不稳定。若 `/hubs` 出问题，可让客户端直连 `SOL_API_ORIGIN`，或允许回退到 SSE / 长轮询（negotiate 会同时返回这三种传输）。生产环境将前后端置于同一反向代理 origin 下即可。

## 验证

```bash
# negotiate 应返回 connectionId 与三种传输
curl -s -X POST "http://localhost:5298/hubs/sol/negotiate?negotiateVersion=1" -b cookies.jar

# 真实 WebSocket 连接 + Ping 往返
cd web && node scripts/verify-realtime.mjs

# backplane 是否真的在工作（不只是编译通过）
docker compose exec -T redis redis-cli PUBSUB CHANNELS 'sol*'
```
