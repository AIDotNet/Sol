# 运维

## 健康检查

两个端点职责不同，**不要混用**：

| 端点 | 检查内容 | 用途 |
|---|---|---|
| `/health/live` | 仅进程存活，**不做任何依赖 I/O** | 容器 liveness 探针 |
| `/health/ready` | Postgres `SELECT 1` + Redis `PING` + RabbitMQ 开 channel | readiness 探针 / 负载均衡 |

分开的原因：**Redis 抖动不应该导致编排系统重启一个本身健康的进程**。liveness 失败意味着"杀掉重来"，readiness 失败只意味着"暂时别给我流量"。

`/health/ready` 在任一依赖故障时返回 **503** 并附带每项的耗时与错误：

```json
{
  "status": "unhealthy",
  "dependencies": [
    { "name": "postgres", "healthy": true,  "error": null, "elapsedMs": 2.9 },
    { "name": "redis",    "healthy": false, "error": "…",  "elapsedMs": 5000.0 }
  ]
}
```

每项探测有 5 秒超时。健康检查为手写实现（未用社区 `AspNetCore.HealthChecks.*` 包）——那些包的 AOT 兼容性未经验证，且为三条单行查询引入大量传递依赖。

## 日志

Serilog，**在代码中配置**（`Extensions/SerilogSetup.cs`）。不能用 `ReadFrom.Configuration`——`Serilog.Settings.Configuration` 通过反射发现 sink 程序集，AOT 下什么也找不到，日志会静默消失。

输出为 CompactJson，每条带 `service` 字段。日志级别仍来自配置（`Logging:LogLevel:*`），通过强类型读取应用。

`DeviceContextMiddleware` 会把 `device_id` / `visitor_id` 注入日志 scope，便于按设备追查。

⚠️ **避免使用 `@` 解构操作符**（`logger.LogInformation("{@Obj}", obj)`）：Serilog 的解构路径在 AOT 下有 IL2067 告警，见 [aot-constraints.md](aot-constraints.md)。

## 追踪与指标

OpenTelemetry 1.17.0，OTLP exporter **仅在配置了 endpoint 时启用**，因此本地开发无需运行 collector：

```json
{ "OpenTelemetry": { "OtlpEndpoint": "http://localhost:4317" } }
```

已接入 ASP.NET Core 的 traces 与 metrics（含 Kestrel meter）。**未**引入 Npgsql / StackExchange.Redis 的 instrumentation 包——它们各自的 AOT 兼容性需要单独验证；需要数据库/缓存追踪时，在仓储内加自定义 `ActivitySource`，该方式天然无反射。

## 配置与密钥

| 键 | 说明 |
|---|---|
| `Postgres:ConnectionString` | |
| `Postgres:RunMigrationsOnStartup` | **生产设为 false**，改用 `--migrate-only` |
| `Redis:Configuration` | |
| `RabbitMq:*` | |
| `Realtime:Backplane` | `None`（默认）或 `Redis` |
| `DeviceIdentity:Pepper` | **密钥**，绝不入代码库 |
| `DeviceIdentity:MaxCoarseFanout` | 概率关联的基数上限，默认 5 |
| `DeviceIdentity:CoarseWindowHours` | 候选时间窗，默认 24 |
| `Ai:EncryptionKey` | **密钥**，base64 编码的 32 字节，加密渠道 API Key |
| `Ai:AssetRoot` | 生成的图片/视频落盘目录，默认 `./storage/assets` |
| `Ai:RequestTimeoutSeconds` | 上游生成请求超时，默认 1200，绑定时最低 600 |
| `Skills:Root` | 已安装 Skill 文件根目录，默认 `./storage/skills` |
| `Skills:MaxUploadBytes` / `MaxExtractedBytes` / `MaxEntries` | Skill 包边界 |
| `Skills:RunnerSocketPath` | Sol.Api 到隔离 runner 的 Unix socket |
| `Skills:RunnerTimeoutSeconds` | 脚本最长时限，默认 30，最大 120 |
| `Skills:RunnerMaxOutputBytes` | stdout/stderr 各自上限，默认 256 KiB，绑定范围 16 KiB–1 MiB |
| `Skills:RunnerMaxPackageBytes` | 单次发送给 runner 的解码后包上限，默认/最大 25 MiB；为 base64 与 JSON 开销预留空间 |
| `OpenTelemetry:OtlpEndpoint` | 留空则不导出 |

环境变量用双下划线：`DeviceIdentity__Pepper`、`Postgres__ConnectionString`。

**pepper 生成**：`openssl rand -base64 32`。轮换会作废全部指纹与概率性关联（确定性 Cookie 关联不受影响），需配合 `sig_version` 升版与双写窗口——该迁移方案尚未设计。

**`Ai:EncryptionKey` 生成**：同样是 `openssl rand -base64 32`，但轮换后果**比 pepper 严重得多**——pepper 轮换只丢失概率性关联，而这个 key 轮换会让已存储的全部 API Key 无法解密，用户必须逐个重新填写。按持久密钥对待，不要与 pepper 混用同一轮换流程。

未配置该项时进程**启动即失败**，而不是在第一次保存 Key 时才报错——密钥缺失属于配置错误，应该在部署时暴露。

**资产目录**：`Ai:AssetRoot` 下的文件是画布节点引用的实际图片与视频，数据库只存路径。该目录需要与数据库一起备份；单独恢复数据库会得到一批指向不存在文件的资产行（接口会返回 404，不会崩溃）。

**视频任务轮询是单实例假设**：`VideoJobPoller` 是一个 `BackgroundService`，每 5 秒取一批 `pending`/`running` 的任务推进。**多副本部署时每个实例都会轮询同一批任务**——上游会被重复查询，完成时也可能重复下载同一个视频。要横向扩容需要 `SELECT ... FOR UPDATE SKIP LOCKED` 或选主，目前未实现。单实例下行为正确。

超过 30 分钟没有进展的任务会被标记为 failed（`VideoJobPoller.StaleAfter`），避免上游接了任务却再不回报时留下永远轮询的行。

## Skill runner

上传包中的脚本绝不在 `Sol.Api` 内执行。`docker-compose.yml` 的 `skill-runner` 使用 `network_mode: none`、只读根文件系统、非 root 用户、capabilities 全部丢弃、`no-new-privileges`、CPU/内存/pids/tmpfs 限制，而且不挂载 Postgres/Redis/RabbitMQ、Skill 根目录、资产目录、源码或 Docker socket。API 与 runner 只共享 Unix socket 目录；每次请求只发送当前 Skill 的文件。

runner 优先用 bubblewrap 为每个 job 建立独立 user/pid/network/ipc/uts/mount namespace。Docker Desktop 默认禁止非特权 user namespace，因此另有经过实测的受限 chroot fallback：runner 只持有 `CHOWN`/`DAC_OVERRIDE`/`KILL`/`SETGID`/`SETUID`/`SYS_CHROOT`，构造私有只读 runtime 与 Skill 根后切换到每-job 唯一 UID，Linux 自动清空子进程 capabilities。两条路径都提供私有可写 work/tmp 并清空环境变量；子进程看不到控制 socket、`/proc`、Sol 凭据、容器根或其他 job。`.sh`、`.py`、`.js` 分别绑定固定解释器，上传包的 shebang 不参与选择。

```bash
# 构建并启动；若 runner 或 bubblewrap 不健康，Agent 会隐藏 run_skill_script，
# 但 load_skill 与普通资源仍然可用。
docker compose up -d --build skill-runner

docker compose logs skill-runner
ls -l src/Sol.Api/storage/runner/runner.sock
```

macOS Docker Desktop 的 VirtioFS 不能承载容器创建的 Unix socket，因此 compose 使用 Docker-managed `sol-runner-socket` volume，而不是宿主 bind mount。生产应把 API 与 runner 放进同一 Linux 编排单元并挂载这个 socket volume。若开发时仍从宿主运行 `Sol.Api`，它无法看见 VM 内 volume，`run_skill_script` 会被安全地隐藏；不要回退到公开 TCP，也不要为了启动 sibling container 而把 Docker socket 交给 API。runner 不可用时是可降级状态，不影响安装或加载 prose Skills。

普通 sandbox escape 最多落在无网络、无 Sol secret、无其他用户文件的 runner 容器；容器/内核级逃逸不可能由应用层完全消除，需同时依赖固定最小镜像、内核更新、seccomp/no-new-privileges、cap drop 与资源上限。

## 部署

```bash
dotnet publish src/Sol.Api -c Release -r linux-x64 /p:PublishAot=true
```

产出自包含单文件（macOS arm64 实测 25 MB），无需运行时。

**迁移作为独立步骤**：

```bash
./Sol.Api --migrate-only     # 退出码 0 成功 / 1 失败
```

`MigrationRunner` 用 PostgreSQL advisory lock 串行化并发实例，因此多副本同时执行是安全的。已应用脚本的 checksum 每次都会重新校验，**内容被改动会导致启动失败**——这是刻意的：静默的环境分叉比启动失败昂贵得多。

**注意**：原生二进制以当前工作目录为 content root。用 systemd / Docker 部署时确保 `WorkingDirectory` 指向包含 `appsettings.json` 的目录，否则配置读不到，表现为"连接串未配置"。

## 多节点

打开 `Realtime:Backplane=Redis` 即可，代码无需改动。SignalR 分组是 backplane 感知的。已在原生 AOT 二进制上实测可用（Redis 中出现真实频道，零错误）。

## 消息可靠性

- 拓扑：topic 交换机 `sol.events` → 队列 `sol.events.api`，配 `x-dead-letter-exchange` → `sol.dlx` → `sol.events.api.dlq`
- 消费失败**重试一次**（`requeue: !ea.Redelivered`），再失败进 DLQ。无限重入会让一条毒消息饿死整个队列
- 消费者外层带指数退避重连（2s → 30s 上限）
- 发布端开启 publisher confirms，消息 `Persistent`

DLQ 积压是需要告警的信号：

```bash
docker compose exec -T rabbitmq rabbitmqctl list_queues name messages consumers
```
