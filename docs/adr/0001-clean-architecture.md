# ADR 0001：Clean Architecture 严格分层

**状态**：已采纳
**日期**：2026-07-31

## 背景

项目起点是单个 `Sol.csproj` 的 AOT minimal-API 模板。需要一个能长期演进、便于测试、且基础设施可替换的结构。

候选方案：垂直切片单项目、经典分层多项目、Clean Architecture 严格分层。

## 决策

采用 **Clean Architecture 严格分层**，四个项目：`Sol.Domain` → `Sol.Application` → `Sol.Infrastructure` → `Sol.Api`，依赖只能向内。

## 理由

- 依赖倒置使基础设施可替换。这一点在本项目中**立刻兑现了价值**：Dapper.AOT 有一个未解决的 issue #168，若它在本环境复现，回退方案（手写 `NpgsqlDataReader` 映射）只需改 `Sol.Infrastructure/Persistence/*.cs`，因为接口定义在 Application 层——零涟漪。
- AOT 约束与该结构天然契合：Dapper.AOT 的 interceptor 不跨项目传递，**机械性地强制了"数据访问只在一层"**（见 [ADR 0002](0002-dapper-aot-over-efcore.md)）。
- 核心用例 `ResolveDeviceIdentity` 不含任何 I/O 依赖，可直接单元测试。

## 代价

- 样板代码更多：一个新特性通常要碰 3 个项目。
- 每层都要配置 `IsAotCompatible`（已由 `Directory.Build.props` 统一处理）。
- 两个 `JsonSerializerContext`（Api 与 Infrastructure 各一），因为 Infrastructure 不能引用 Api。

## 例外

`SignalRNotifier`（`IRealtimeNotifier` 的实现）放在 `Sol.Api` 而非 `Sol.Infrastructure`，因为它需要具体的 `SolHub` 类型。组合根实现 Application 接口是正统做法；把 Hub 拖进 Infrastructure 才是真正的层级倒置。
