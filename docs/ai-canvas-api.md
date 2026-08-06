# AI 与画布 API

设备识别之外的第二个功能域：AI 渠道配置、图片/视频/文本生成、画布存储。

## 归属模型

**所有资源都以 `device_id` 为归属键，绝不用 `visitor_id`。**

`visitor` 可能由 `ProbabilisticCoarse` 的 0.6 置信度猜测拼装而成（见 [device-identification.md](device-identification.md)）。若按 visitor 取配置，共用 NAT 出口的办公室里陌生人会读到彼此的 API Key。`device_id` 只来自确定性通道（Cookie 1.0 / localStorage 恢复 0.95）。

所有端点从 `DeviceContextMiddleware` 读 `device_id`，**无 Cookie 一律 401**，且不会隐式铸造身份。

「不存在」与「属于别的设备」一律返回 **404 而非 403**——否则 404/403 的差异会变成一个 ID 探测信道。归属判断写在仓储的 SQL 谓词里，不是事后 if，调用方无从遗漏。

## 端点

### 渠道与模型

```
GET    /api/v1/ai/providers                      当前设备的渠道（Key 已掩码）
POST   /api/v1/ai/providers                      新建
PATCH  /api/v1/ai/providers/{id}
DELETE /api/v1/ai/providers/{id}
POST   /api/v1/ai/providers/{id}/check           连通性测试
GET    /api/v1/ai/providers/{id}/upstream-models 拉取上游 /v1/models
POST   /api/v1/ai/providers/{id}/models          新增模型
POST   /api/v1/ai/providers/{id}/models/import   批量导入（已存在的跳过）
PATCH  /api/v1/ai/models/{modelId}
DELETE /api/v1/ai/models/{modelId}
POST   /api/v1/ai/config/import                  URL 快速配置落库
```

### URL 渠道快速配置

页面入口支持在 hash 中携带配置：

```text
/#settings=base64url:<UTF-8 JSON 的 Base64URL 编码>
```

规范载荷是一个 `providers` 数组，可同时配置多个渠道。内置渠道只需要 `builtinId` 和
`apiKey`；前端会从内置预设补全名称、协议、Base URL、预设版本和默认模型：

```json
{
  "providers": [
    { "builtinId": "openai", "apiKey": "sk-example" }
  ]
}
```

自定义渠道需要 `name`、`type` 和 `baseUrl`，`apiKey` 与 `models` 可省略。省略模型不会触发
服务端网络请求，渠道会保留为空，之后可在设置页手动添加或拉取：

```json
{
  "providers": [
    {
      "name": "My Relay",
      "type": "openai-chat",
      "baseUrl": "https://relay.example.com/v1",
      "apiKey": "key-example",
      "models": [
        { "id": "my-model", "name": "My Model", "category": "chat" }
      ]
    }
  ]
}
```

`autoApply: true` 只对不携带 `apiKey` 的链接生效。任何带 Key 的链接都必须经过确认，预览
会显示脱敏 Key，且参数会在页面加载后从地址栏移除。`?settings=`、原始 JSON 和旧的
`base64:` 格式仍兼容，但新生成的链接应使用 hash + `base64url:`，避免 Key 进入请求日志。

### 生成

```
POST   /api/v1/ai/images     → { assets: [{ url, mediaType, assetId }] }
POST   /api/v1/ai/text       → { text }
POST   /api/v1/ai/videos     → { jobId }
GET    /api/v1/ai/videos/{id} → { status, progress, assetUrl, error }
DELETE /api/v1/ai/videos/{id} 取消（已完成的任务是 no-op）
```

### Agent 与 Skills

```
POST   /api/v1/agent/sessions
GET    /api/v1/agent/sessions?canvasId=...
GET    /api/v1/agent/sessions/{id}/messages
DELETE /api/v1/agent/sessions/{id}/messages
POST   /api/v1/agent/sessions/{id}/runs
GET    /api/v1/agent/runs/{id}
GET    /api/v1/agent/runs/{id}/events?afterSeq=...
DELETE /api/v1/agent/runs/{id}
GET    /api/v1/agent/tools

GET    /api/v1/skills/
GET    /api/v1/skills/{id}
POST   /api/v1/skills/scan        multipart，只扫描不安装
POST   /api/v1/skills/            multipart，重新扫描同一上传并安装
DELETE /api/v1/skills/{id}
```

Skill 安装接受 bare `SKILL.md` 或 ZIP。ZIP 逐 entry 读取，拒绝绝对路径、`..`、反斜杠、重复归一化路径、symlink、嵌套压缩包与异常压缩比，并限制上传大小、解压总量和 entry 数。danger 级发现必须二次确认；风险扫描不被当作沙箱。

Agent 只在 run 开始时向模型披露 Skill 的 slug/name/description；完整正文和资源需通过 `load_skill` 按需读取。`run_skill_script` 只在隔离 runner 健康且存在脚本 Skill 时出现，每次执行均需审批。

脚本不会在 `Sol.Api` 进程执行。API 通过 Unix socket 把单个 Skill 的文件发送给独立 runner；runner 容器无网络、无 Sol 配置/凭据/数据库卷、根文件系统只读。支持非特权 user namespace 的 Linux 内核用 bubblewrap 建立每-job user/pid/network/mount namespace；禁用该能力的 Docker Desktop 使用受限 chroot + 每-job 唯一 UID，且子进程看不到 `/proc`、控制 socket、其他 job 或容器根。仅固定解释器 `.sh`/`.py`/`.js` 可用，参数不经过 shell，CPU/地址空间/文件/进程/输出/时限均有硬上限。runner 不可用时 prose/resource Skills 仍可加载，只隐藏脚本工具。

### 画布与资产

```
GET    /api/v1/canvas             列表（含 SQL 计算的 nodeCount，不含图本体）
POST   /api/v1/canvas
GET    /api/v1/canvas/{id}
PUT    /api/v1/canvas/{id}        不存在则按该 id 创建
DELETE /api/v1/canvas/{id}
POST   /api/v1/canvas/assets      上传图片（multipart）
GET    /api/v1/canvas/assets      素材库列表（?kind=image|video，?limit 上限 200）
GET    /api/v1/canvas/assets/{id} 流式返回字节
DELETE /api/v1/canvas/assets/{id} 删除元数据行与磁盘文件
```

**删除的顺序是先删行、后删文件**：若删文件失败，结果是一个孤儿文件，而不是一行指向不存在文件的记录。仍然引用该资产的画布节点会显示破图——这是有意的取舍，否则「可能被引用」会让素材库根本无法清理。

## 协议支持矩阵

`ProviderType` 描述的是**请求如何构造**，与厂商无关——多数厂商都讲 `openai-chat`。

| 协议 | 文本 | 图片 | 视频 |
|---|---|---|---|
| `openai-chat` | ✅ `/chat/completions` | — | — |
| `openai-responses` | ✅ `/responses` | — | — |
| `anthropic` | ✅ `/v1/messages` | — | — |
| `gemini` | ✅ `generateContent` | ✅ `generateContent` | — |
| `openai-images` | — | ✅ `/images/generations`、`/images/edits` | — |
| `openai-video` | — | — | ✅ `/videos` |
| `xai-video` | — | — | ✅ 同 OpenAI 形状 |
| `seedance-video` | — | — | ✅ `/contents/generations/tasks` |

前端在 `web/features/ai/protocol-support.ts` 维护同一份矩阵，驱动「尚未支持」标记。新增协议但客户端未就绪时，把该项标为 `planned`，UI 会自动禁用相关模型。

**MCP runtime 支持 Streamable HTTP 与 legacy SSE**；stdio 配置仍可存储，但服务端不会启动本地进程。所有 MCP tool 每次调用都必须由用户审批，URL 经 DNS 固定与 SSRF 私网过滤，header/env 值只以服务端密文保存。

### 协议解析：模型覆盖渠道

```
model.Type ?? (category == Image ? openai-images
             : category == Video ? seedance-video
             : provider.Type)
```

图片/视频模型在没有显式覆盖时不回落到渠道协议——渠道通常按其**对话**协议声明，而它的图片路由讲的是另一套。

这条规则在 `AiProvider.ResolveProtocol`（后端）与 `resolveProtocol`（前端 `features/ai/types.ts`）中各实现一次，**两处必须一致**：前端据此决定渲染哪些参数控件，后端据此决定分发给哪个客户端。

### Base URL 归一化

`AiProvider.NormalizeBaseUrl` 在**服务端**的每个写入路径执行。用户粘贴的通常是厂商文档里的 OpenAI 兼容地址，而各协议客户端会自己拼接路径：

- `anthropic`：剥掉尾部 `/v1`，否则变成 `…/v1/v1/messages`
- `gemini`：剥掉尾部 `/openai`

前端也做一次（即时反馈），但**服务端才是权威**——API 可被直接调用，分享链接导入也不该依赖发送方记得归一化。

## API Key

AES-256-GCM 加密后入库，密钥来自 `Ai:EncryptionKey`（见 [operations.md](operations.md)）。

- 每次加密生成新的 96 位 nonce。GCM 下 nonce 重用会同时泄漏两段明文的异或与认证子密钥，因此绝不复用
- **明文永不出现在任何响应体中**。`ProviderResponse` 根本没有这个字段，`AiContractMapper` 是唯一的序列化入口，只读 `HasApiKey` 与尾四位掩码
- 三态更新语义：字段缺省=保持不变（改 Base URL 的常见情形）、空串=清除、有值=替换。单纯可空类型表达不了「保持」，而弄错会静默抹掉用户的 Key

## 生成的边界条件

**参考图只接受自家资产路径**。`/api/v1/canvas/assets/` 之外的 URL 一律忽略，不会去拉取——否则这个端点就成了服务端请求伪造的跳板。实测 `169.254.169.254` 元数据端点与 `file:///etc/passwd` 均被静默丢弃。

**上传做双重校验**：MIME 白名单（png/jpeg/webp/gif）+ 文件头魔数比对。Content-Type 是客户端可伪造的，只信它就能把任意内容存成图片类型再被取回。**SVG 被排除**——它是可携带脚本的 XML，直接导航到存储的 SVG 会以同源身份执行。资产响应带 `X-Content-Type-Options: nosniff`。

**视频是彻底异步的**。POST 在厂商受理后立即返回 jobId，由 `VideoJobPoller` 在服务端推进，因此关掉标签页任务照样跑完。客户端轮询自家的行，而不是挂住连接几分钟。

## 画布存储

`canvas.graph` 是 **jsonb，对服务端不透明**——由画布客户端产出与消费，服务端只存取。逐节点建行不会带来任何好处（图总是整体读写），却会把 schema 绑死在一个变更频率高得多的客户端模型上。

客户端同时保留 localStorage 副本作即时缓存与离线编辑，服务端是持久副本；加载时取 `updatedAt`/`savedAt` 较新者。

**持久化前会剥离 `data:` 与 `blob:` URL**。前者会把数 MB base64 写进每次保存与每帧撤销快照；后者更隐蔽——体积小、保存正常，但随文档卸载即失效，还原回来就是一个无法恢复的破图节点。剥离后节点退回可重新上传的空状态。

带 `jobId` 的视频节点是例外：它的工作在服务端继续，因此保留 `running`，由客户端在 hydration 时重新挂上轮询。

## 相关文档

- [device-identification.md](device-identification.md) —— 归属键的由来与置信度上限
- [aot-constraints.md](aot-constraints.md) —— 为何上游 payload 一律用 `JsonObject`/`JsonDocument` 而不建 DTO
- [operations.md](operations.md) —— `Ai:*` 配置项、资产目录备份、视频轮询的单实例假设
