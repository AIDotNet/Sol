# Sol 后台框架文档

Sol 是一个 .NET 11 Native AOT 后端：SignalR 实时通信、无账号体系的设备识别、Dapper + PostgreSQL、RabbitMQ、Redis。

## 文档索引

| 文档 | 内容 |
|---|---|
| [architecture.md](architecture.md) | 分层结构、依赖规则、各层职责、关键位置决策 |
| [aot-constraints.md](aot-constraints.md) | **最重要的一篇**：AOT 禁用清单与实测结论 |
| [device-identification.md](device-identification.md) | 设备识别设计、能力边界、合规义务 |
| [ai-canvas-api.md](ai-canvas-api.md) | AI 渠道/生成/画布端点、协议矩阵、Key 与资产的安全边界 |
| [ai-provider-url-config.md](ai-provider-url-config.md) | AI 服务商 URL 自动配置的载荷、编码、导入语义与对接示例 |
| [realtime-signalr.md](realtime-signalr.md) | Hub 契约、分组、连接绑定、backplane |
| [data-model.md](data-model.md) | 表结构、索引理由、迁移约定 |
| [local-development.md](local-development.md) | 环境准备、启动、联调、排错 |
| [operations.md](operations.md) | 健康检查、日志、追踪、密钥与部署 |
| [adr/](adr/) | 架构决策记录 |

## 系统总览

```
浏览器 (Next.js :3000)
   │  /api/*  /hubs/*   ← Next.js rewrites 代理，保证同源 Cookie
   ▼
Sol.Api (:5298)  ──  SignalR Hub · 设备握手端点 · 健康检查
   │
   ├── PostgreSQL   设备与访客身份（Dapper.AOT）
   ├── Redis        缓存 · 在线状态 · 分布式锁 · SignalR backplane（可选）
   └── RabbitMQ     集成事件（topic 交换机 + 死信队列）
```

## 三个必须先知道的结论

**1. SignalR 与 Native AOT 兼容**，但**禁止使用强类型 Hub**（`Hub<TClient>` / `IHubContext<THub,TClient>`）。网上"SignalR 不支持 AOT"的说法源自 .NET 8 旧文档。详见 [aot-constraints.md](aot-constraints.md)。

**2. 跨浏览器识别同一设备无法可靠实现。** 这是浏览器沙箱的物理限制，不是实现问题。本项目采用分层方案：Cookie 确定性识别（~99%）+ 概率性关联（置信度上限 0.6，仅作提示）。详见 [device-identification.md](device-identification.md)。

**3. 本地基础设施端口有偏移**（Redis `6380`、RabbitMQ `5673`/`15673`），因为开发机上已有其他项目占用默认端口。详见 [local-development.md](local-development.md)。

## 快速开始

```bash
docker compose up -d
dotnet run --project src/Sol.Api
curl http://localhost:5298/health/ready
```
