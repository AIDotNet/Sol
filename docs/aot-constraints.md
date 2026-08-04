# AOT 约束

> 这是本项目最重要的一篇文档。下面每一条都对应一种"编译通过、JIT 测试全绿、发布成原生二进制后运行时才炸"的失败模式。

## 实测结论（.NET 11 preview.5，osx-arm64）

在写业务代码之前先做了冒烟验证，同时引入全部风险面。结果：

| 组件 | 结论 | 证据 |
|---|---|---|
| SignalR 服务端 | ✅ 可用 | 原生二进制 negotiate 返回 200，WebSocket 实连成功，`Ping` 往返正常 |
| **SignalR Redis backplane** | ✅ **可用**（超出预期） | 原生二进制启动后 Redis 中出现真实频道 `sol…:all` / `:internal:groups` / `:ack` / `:return`，零错误 |
| Dapper.AOT | ✅ 可用 | issue #168 **未复现**；13 个调用点全部生成 interceptor，零反射回退 |
| Npgsql（Slim） | ✅ 可用 | 零 IL 告警 |
| StackExchange.Redis | ✅ 可用 | 零 IL 告警 |
| RabbitMQ.Client v7 | ✅ 可用 | 零 IL 告警，拓扑声明与消费者正常 |

发布产物 **25 MB** 自包含单文件，全部端到端测试在原生二进制上通过。

**关于 backplane：** `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 的 csproj **没有** `IsTrimmable`/`IsAotCompatible` 标记（对比 `SignalR.Core` 是有的），微软未做官方认证。但实测完全可用。因此它由配置开关 `Realtime:Backplane = None|Redis` 控制，**默认关闭**——不是因为它不工作，而是因为单机部署不需要它，且官方未背书。需要多节点时打开即可。

### 唯一残留的 IL 告警

```
IL2067: Serilog.Capturing.PropertyValueConverter.TryConvertStructure(...)
```

来自 Serilog 的解构路径，只有在用 `@` 解构操作符记录复杂对象时才可达（例如 `logger.LogInformation("{@User}", user)`）。本项目不这样用。**如果你要用 `@` 解构，先验证该类型在 AOT 下的行为。**

---

## 禁用清单

### 绝对禁止

| 禁止项 | 原因 | 替代方案 |
|---|---|---|
| `Hub<TClient>` / `IHubContext<THub,TClient>` | `TypedClientBuilder<T>` 用 `Reflection.Emit` 动态生成客户端代理，标注 `[RequiresDynamicCode]` | 普通 `Hub` + `SendAsync("Method", args)`，方法名集中在 `RealtimeMethods` |
| MessagePack 协议 | 其 SignalR 协议包未标记 trimmable | 只用 JSON 协议 + 源生成 |
| `LuaScript.Prepare(...)` / `ScriptParameterMapper` | 内部 `Expression.Compile()`，AOT 下抛 `PlatformNotSupportedException` | `ScriptEvaluateAsync(script, RedisKey[], RedisValue[])` 原始形式 |
| `AddValidatorsFromAssembly*` | 反射扫描，AOT 下**静默注册零个**——校验在生产环境空转，而所有 JIT 测试通过 | 逐个 `AddSingleton<IValidator<T>, TValidator>()` + 启动断言 |
| `ReadFrom.Configuration(...)`（Serilog） | `Serilog.Settings.Configuration` 反射发现 sink 程序集，AOT 下什么也找不到，日志静默消失 | 代码内配置 `new LoggerConfiguration()` |
| Swashbuckle | 维护者明确表示不会支持 AOT（需要重写） | `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` |
| `services.Configure<T>(IConfiguration)` | 反射绑定，AOT 下可能产出全默认值对象——连接串为空，然后在离故障点很远的地方失败 | 手写绑定，见 `Options/OptionsBinder.cs` |
| `TypedResults.Json(value)`（无 JsonTypeInfo 重载） | 反射序列化 | `TypedResults.Json(value, Ctx.Default.Type, statusCode: …)` |
| `EnableDynamicJson()` / `MapComposite<T>()`（Npgsql） | 需要运行时代码生成 | 自己用源生成 STJ 序列化成 string |

### Dapper.AOT 不支持的用法

以下用法**不会报编译错误**，而是静默回退到反射版 Dapper，然后在 AOT 运行时抛异常：

- 多映射 `Query<T1,T2,TResult>`（arity > 1）
- `QueryMultiple` / `QueryMultipleAsync`
- 非泛型 `Query(typeof(Foo))`
- 参数为 `object` / `dynamic` / `DynamicParameters` / 泛型 `T`
- 元组返回（用 `readonly record struct` 代替）
- `SqlMapper.Settings` 与 `ITypeHandler`（被完全忽略）

**防护措施**：`Directory.Build.props` 中把 `DAP001;DAP013;DAP015;DAP016;DAP017` 设为 error，这些静默回退变成编译失败。

---

## 结构性防护

### 1. `IsAotCompatible=true` 加在所有类库上

这是 `Directory.Build.props` 里价值最高的一行。它在**类库**上开启 trim/AOT 分析器，使违规在**所在层的编译期**就以 IL2xxx/IL3xxx 暴露，而不是等到最终 publish 时淹没在噪音里。

实际战果：开发过程中它抓到了两处真实隐患——`Configure<T>(IConfiguration)` 的反射绑定，以及健康检查 503 分支里的反射 `TypedResults.Json`（后者尤其阴险：它恰好在依赖已经故障时才执行）。

### 2. Dapper 调用点被强制集中

Dapper.AOT 通过 **C# interceptor** 工作，而 interceptor 是编译期产物，**只在包含调用点的编译单元内生效，不跨 `ProjectReference` 传递**。因此：

- `Dapper.AOT` 包引用、`<InterceptorsPreviewNamespaces>`、`[module: DapperAot]` **只在 `Sol.Infrastructure`**
- `Sol.Api.csproj` **完全不引用 Dapper**——泄漏到那里的 Dapper 调用会静默编译成反射版本

这个 AOT 约束恰好**机械性地强制了 Clean Architecture 的"数据访问只在一层"**。

### 3. 端口签名里带 `JsonTypeInfo<T>`

```csharp
Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct);
```

把源生成元数据写进接口签名，使 AOT 错误的序列化路径**在结构上不可达**——根本不存在可以误入的反射重载。

### 4. 启动期自检

`Program.cs` 的 `VerifyAotRegistrations` 逐个解析预期的 validator，缺失则启动崩溃。把"生产环境校验静默空转"换成"启动立刻失败"，后者便宜得多。

---

## 验证方法

**JIT 模式通过不能证明 AOT 可用。** 以下三类问题在 `dotnet run` 下全部静默：FluentValidation 注册为空、STJ resolver 缺失、Dapper 回退到反射。必须对原生二进制重跑全部测试：

```bash
dotnet publish src/Sol.Api -c Release -r osx-arm64 /p:PublishAot=true /p:TrimmerSingleWarn=false

# 必须在发布目录内运行，否则 content root 不对，读不到 appsettings
cd src/Sol.Api/bin/Release/net11.0/osx-arm64/publish
ASPNETCORE_ENVIRONMENT=Development ./Sol.Api --urls http://localhost:5299
```

检查 Dapper 是否真的生成了 interceptor（而非静默回退）：

```bash
dotnet build src/Sol.Infrastructure /p:EmitCompilerGeneratedFiles=true /p:CompilerGeneratedFilesOutputPath=/tmp/gen
grep -c 'InterceptsLocation' /tmp/gen/Dapper.AOT.Analyzers/*/*.cs   # 应等于 Dapper 调用点数量
rm -rf /tmp/gen   # 注意：不要输出到项目目录内，否则会被重复编译
```

---

## 版本锁定

`global.json` 用 `rollForward: disable` 锁定 SDK。`Microsoft.AspNetCore.OpenApi` 与 `Microsoft.AspNetCore.SignalR.StackExchangeRedis` **必须与 SDK preview band 严格一致**（当前 `11.0.0-preview.5.26302.115`）；升级时三者必须同步移动。

`Microsoft.OpenApi` 在 `Directory.Packages.props` 中被显式提升到 `3.9.0`：传递引入的 3.3.1 存在 CVE-2026-49451（循环 `$ref` 导致栈溢出，GHSA-v5pm-xwqc-g5wc），3.5.4 起修复。

## 遗留风险

- **Dapper.AOT issue #168**（ILC 扫描 Dapper.dll 报致命错误）本项目未复现，但该 issue 仍 open 且零维护者回复。若未来升级后复现，缓解手段是在 `Sol.Infrastructure` 范围内 `<NoWarn>IL2026;IL2070;IL2046;IL3050</NoWarn>`，然后**必须用真实数据库验证运行时**。
- **Dapper.AOT 使用的是 Roslyn 旧版 interceptor 形式**（`InterceptsLocationAttribute(string,int,int)`），会触发 CS9270（已在 `Sol.Infrastructure` 局部 NoWarn）。该形式处于弃用路径上，未来 Roslyn 版本可能移除。
- **Serilog 解构路径的 IL2067**（见上）。
