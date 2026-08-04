# ADR 0002：用 Dapper.AOT 而非 EF Core

**状态**：已采纳
**日期**：2026-07-31

## 背景

需求同时要求 Native AOT 与 Dapper + PostgreSQL。但**经典 Dapper 在设计上就与 AOT 不兼容**——它靠运行时 IL 生成来做对象映射。

## 决策

使用 **Dapper.AOT 1.0.52**（配合 Dapper 2.1.79）。它通过 Roslyn 源生成器 + C# interceptor，在编译期把 Dapper 调用替换为生成的映射代码。

放弃 EF Core：其 Migrations 依赖运行时模型构建，AOT 下不可用（迁移改用版本化 SQL，见 [data-model.md](../data-model.md)）。

## 风险与实测结论

**已知风险**：[DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168) 报告 `PublishAot=true` 时 ILC 仍会扫描 `Dapper.dll` 并报致命 IL 错误。该 issue 2026-03-16 开启，**至今零维护者回复**。

**因此在写任何业务代码之前先做了冒烟验证**——这是本项目最重要的流程决策。结论：

- **issue #168 未复现**。`dotnet publish -r osx-arm64 /p:PublishAot=true` 零 IL 告警。
- 13 个 Dapper 调用点**全部生成了 interceptor**，无一回退到反射（通过 `EmitCompilerGeneratedFiles` 检查 `InterceptsLocation` 数量确认）。
- 原生二进制对真实 PostgreSQL 的读写全部通过。

若未来升级后复现，缓解手段是在 `Sol.Infrastructure` 范围内 `<NoWarn>IL2026;IL2070;IL2046;IL3050</NoWarn>`，然后**必须用真实数据库验证运行时**——publish 干净不等于运行时正确。

## 约束

Dapper.AOT 不支持多映射 `Query<T1,T2,TResult>`、`QueryMultiple`、非泛型 `Query(typeof(T))`、`object`/`dynamic`/`DynamicParameters` 参数、元组返回、`ITypeHandler`。

**这些用法不会报编译错误**，而是静默回退到反射版 Dapper，然后在 AOT 运行时抛异常。防护：`Directory.Build.props` 把 `DAP001;DAP013;DAP015;DAP016;DAP017` 设为 error。

包引用、`InterceptorsPreviewNamespaces`、`[module: DapperAot]` 必须与调用点在同一项目——interceptor 不跨 `ProjectReference` 传递。`Sol.Api` 因此**完全不引用 Dapper**。

## 其他代价

- Dapper.AOT 使用 Roslyn 旧版 interceptor 形式（触发 CS9270，已局部 NoWarn），该形式处于弃用路径上。
- 项目维护节奏偏慢（发布间隔以季度计）。
- 需要注意 PostgreSQL 类型映射细节：`smallint` 列要写 `@Param::smallint`，`timestamptz` 读取要用 `DateTime` 而非 `DateTimeOffset`。

## 回退方案

保留 `IDeviceRepository`/`IVisitorRepository` 接口不变，实现改为手写 `NpgsqlDataReader` 映射。因接口在 Application 层，改动范围仅限 `Sol.Infrastructure/Persistence/*.cs`。
