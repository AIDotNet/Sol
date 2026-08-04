# ADR 0004：SignalR Redis backplane 默认关闭但可用

**状态**：已采纳
**日期**：2026-07-31

## 背景

多节点部署需要 backplane 才能让分组/广播跨实例生效。但 `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 的 csproj **既没有 `IsTrimmable` 也没有 `IsAotCompatible`**（对比 `SignalR.Core` 两者都有），微软未对其做 AOT 认证。

## 决策

**实测验证后确认可用，但默认关闭**，由配置开关 `Realtime:Backplane = None | Redis` 控制。

## 实测结论

源码审查先给出乐观信号：扫描 `RedisProtocol.cs`、`RedisHubLifetimeManager.cs` 等**零命中** `RequiresDynamicCode` / `MakeGenericType` / `Expression.` / `Activator.`；其 MessagePack 用法是底层 `new MessagePackWriter(buffer)` 手工帧编码，**不是**反射式的 `MessagePackSerializer.Serialize<T>()`。

实测印证了这一点：

- `dotnet publish /p:PublishAot=true` 相比不带 backplane 的基线**零新增 IL 告警**
- 原生二进制启动后，Redis 中出现**真实的 backplane 频道**：`sol…SolHub:all`、`:internal:groups`、`:internal:ack:…`、`:internal:return:…`
- negotiate 返回 200，运行期零错误

即：它不只是编译通过，而是确实在工作。

## 为什么仍然默认关闭

不是因为它不工作，而是：

- 单机部署不需要它，多一个组件就多一份故障面
- 微软未做官方认证，未来版本可能引入 AOT 不安全的代码路径而不被察觉
- 开关成本极低：改一个配置值，代码零改动（SignalR 分组本身就是 backplane 感知的）

## 代价

- 默认配置下多节点部署会静默失效（消息只到本节点）。**运维必须知道这个开关**——已写入 [operations.md](../operations.md) 与 [realtime-signalr.md](../realtime-signalr.md)。
- 升级该包时需要重新执行一次 AOT 发布 + Redis 频道检查。
