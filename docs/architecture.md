# 架构

## 分层

```
Sol.Domain          零外部依赖（无 NuGet、无 ProjectReference）
   ▲
Sol.Application     接口（端口）+ 用例 + 契约
   ▲
Sol.Infrastructure  Dapper/Npgsql/Redis/RabbitMQ 实现
   ▲
Sol.Api             组合根 · SignalR Hub · HTTP 端点 · 中间件
```

依赖箭头**只能向内**。`Sol.Api` 是唯一允许引用全部层的项目。

## 各层职责

### Sol.Domain
身份类型与不变量。`DeviceId`/`VisitorId`（UUIDv7 包装）、`Device`/`DeviceLink` 实体、`LinkMethod` 枚举、`LinkConfidence` 值对象（强制概率性关联的 0.6 上限）。

不含任何 I/O、任何框架类型。想在这里加包引用时，说明该类型其实属于 Application。

### Sol.Application
定义**端口**（`IDeviceRepository`、`ICacheStore`、`IEventPublisher`、`IRealtimeNotifier`…）与**用例**（`ResolveDeviceIdentity`）。

关键设计：端口签名里带 `JsonTypeInfo<T>`，使 AOT 错误的序列化路径在结构上不可达（见 [aot-constraints.md](aot-constraints.md)）。

### Sol.Infrastructure
实现端口。**所有 Dapper 调用点都必须在这里**——原因见下。

### Sol.Api
组合根。Program.cs、SignalR Hub、Minimal API 端点、中间件、`ApiJsonContext`。

## 三个关键位置决策

### 1. 为什么所有 Dapper 调用必须集中在 Infrastructure

Dapper.AOT 通过 **C# interceptor** 工作。interceptor 是编译期产物，**只在包含调用点的编译单元内生效，不跨 `ProjectReference` 传递**。

后果：如果 Dapper 调用泄漏到 `Sol.Api`，它会**静默编译成反射版 Dapper**——没有编译错误，因为生成器根本没在那里运行——然后在 AOT 运行时抛异常。

防护：`Sol.Api.csproj` **完全不引用 Dapper**。这个 AOT 约束恰好机械性地强制了 Clean Architecture 的"数据访问只在一层"。

### 2. 为什么 `SolHub` 在 Sol.Api 而不是 Infrastructure

Hub 是**交付机制**，等同 Controller，属最外层。另外 JSON hub protocol 的 resolver 需要覆盖所有过线类型，Hub 与其 `JsonSerializerContext` 同处一个程序集，源生成器才能获得完整视图。

`IRealtimeNotifier` 接口定义在 Application，但实现 `SignalRNotifier` **也在 Sol.Api**——因为它需要 `SolHub` 类型，放 Infrastructure 会把交付层类型拖进内层。组合根实现 Application 接口是正统 Clean Architecture 做法。

### 3. 为什么需要两个 JsonSerializerContext

| Context | 位置 | 覆盖 |
|---|---|---|
| `ApiJsonContext` | `Sol.Api/Serialization/` | HTTP 请求响应、SignalR 过线 DTO |
| `IntegrationJsonContext` | `Sol.Infrastructure/Serialization/` | RabbitMQ 事件、Redis 缓存信封 |

不能合一：Infrastructure 不允许引用 Api。共享的过线 DTO 放 `Sol.Application/Contracts/`，两个生成器都能看到。`TypeInfoResolverChain` 支持多个 resolver，组合无冲突。

## 请求生命周期（设备握手）

```
POST /api/v1/device/handshake
  │
  ├─ Serilog 请求日志
  ├─ DeviceContextMiddleware   读 Cookie → HttpContext.Items + 日志 scope（只读，不创建身份）
  ├─ DeviceEndpoints.HandshakeAsync
  │    ├─ IValidator<DeviceSignalsPayload>   显式注册的 FluentValidation
  │    └─ ResolveDeviceIdentity.HandleAsync  ← 核心用例（Application 层）
  │         ├─ IDeviceRepository        Dapper.AOT（Infrastructure）
  │         ├─ IFingerprintHasher       量化 + SHA256 + pepper
  │         └─ IVisitorRepository       device_link 边写入
  └─ DeviceCookie.Write        每次握手都补发，让确定性层尽快接管
```

`DeviceContextMiddleware` **只读不写**：它从不创建身份。身份只由握手端点铸造，因此健康探针、爬虫等请求不会静默产生设备行。

## 构建期防护

`Directory.Build.props` 中两组设置承担主要防护职责：

```xml
<IsAotCompatible>true</IsAotCompatible>
<WarningsAsErrors>$(WarningsAsErrors);DAP001;DAP013;DAP015;DAP016;DAP017</WarningsAsErrors>
```

- `IsAotCompatible` 在**类库**上开启 trim/AOT 分析器，让违规在所在层编译期暴露
- `DAP001` 等设为 error，把 Dapper.AOT 的静默回退变成编译失败

包版本集中在 `Directory.Packages.props`（含一处安全提升：`Microsoft.OpenApi` → 3.9.0）。SDK 由 `global.json` 锁定 `rollForward: disable`。

## 目录

```
src/Sol.Domain/Identity/          DeviceId, VisitorId, Device, DeviceLink, LinkConfidence, LinkMethod
src/Sol.Application/
  Abstractions/{Persistence,Caching,Messaging,Realtime,Security,Common}/   端口
  Contracts/{Device,Realtime}/    过线 DTO（两个 JSON context 共享）
  Features/Identity/              ResolveDeviceIdentity + Options + Validator
  Events/                         集成事件
src/Sol.Infrastructure/
  Persistence/                    DapperModule, NpgsqlDataSourceFactory, 仓储, Migrations/
  Caching/                        RedisCacheStore, RedisPresenceTracker, RedisDistributedLock
  Messaging/                      连接提供者, 拓扑, 发布者, 消费者, 处理器注册表
  Security/                       Sha256FingerprintHasher, IpPrefixExtractor
  Health/ Options/ Common/
src/Sol.Api/
  Program.cs                      组合根
  Endpoints/ Hubs/ Realtime/ Middleware/ Extensions/ Serialization/
db/migrations/                    版本化 SQL（嵌入资源）
web/                              Next.js 前端（lib/device.ts 握手客户端）
```
