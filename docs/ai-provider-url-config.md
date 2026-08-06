# AI 服务商 URL 自动配置对接文档

本文说明如何生成一个打开 Sol 后即可导入 AI 服务商配置的链接。

适用场景：

- 产品官网为用户生成“配置渠道”链接
- 管理后台向指定用户分发预置渠道
- 将多个 AI 服务商和模型配置打包导入当前设备

## 1. 工作流程

URL 配置由浏览器完成解析和预设补全，服务端只负责校验和落库：

```text
生成方
  │  JSON → UTF-8 → Base64URL
  ▼
Sol 页面打开
  │  解析、校验、补全内置预设
  │  等待设备握手和 AI 配置加载
  ▼
确认对话框（带 Key）/ 自动导入（无 Key 且 autoApply=true）
  │
  ▼
POST /api/v1/ai/config/import
  │  批量校验、加密 Key、渠道幂等更新、模型 absent-only 写入
  ▼
当前设备的 AI 配置
```

页面使用设备 Cookie 识别配置归属。没有设备 Cookie 时，导入接口返回 `401`，不会隐式创建设备。

## 2. 链接格式

推荐使用 hash 形式：

```text
https://sol.example.com/canvas#settings=base64url:<payload>
```

使用 hash 的原因是 fragment 不会出现在浏览器发给服务器的 HTTP 请求行中。页面识别配置后会从地址栏移除 `settings` 参数；但 API Key 仍然可能已经进入浏览器历史、剪贴板、截图或第三方页面监控记录，因此仍应使用短期 Key，并在必要时轮换。

当前解析器兼容以下形式：

| 形式 | 状态 | 说明 |
|---|---|---|
| `#settings=base64url:<payload>` | 推荐 | 新链接的规范形式，不带 `=` padding |
| `?settings=base64url:<payload>` | 兼容 | 不推荐，Key 可能进入访问日志 |
| `?settings=<URL 编码 JSON>` | 兼容 | 原始 JSON 需要 URL 编码 |
| `?settings=base64:<payload>` | 兼容 | 旧标准 Base64；`+` 被 query 解析为空格时会自动修复 |
| `#settings=base64:<payload>` | 兼容 | 旧 hash 链接 |

Hash 和 query 同时存在时优先使用 hash。hash 中存在格式错误的 `settings` 时，不会回退到 query 中的另一个配置。

## 3. URL 载荷契约

解码后的 JSON 结构如下：

```json
{
  "autoApply": false,
  "providers": [
    {
      "builtinId": "openai",
      "apiKey": "sk-example"
    }
  ]
}
```

### 3.1 顶层字段

| 字段 | 类型 | 必填 | 说明 |
|---|---|---:|---|
| `autoApply` | boolean | 否 | 默认 `false`。只有不携带 API Key 时才允许自动导入 |
| `providers` | array | 是 | 1–50 个服务商 |

### 3.2 内置服务商

内置服务商只需要 `builtinId`，以及可选的 `apiKey`：

```json
{
  "builtinId": "openai",
  "apiKey": "sk-example"
}
```

`builtinId` 必须存在于当前前端预设目录。下表是当前预设快照，模型数量会随预设版本变化：

| `builtinId` | 名称 | 默认协议 | 默认 Base URL | 默认模型 |
|---|---|---|---|---:|
| `openai` | OpenAI | `openai-chat` | `https://api.openai.com/v1` | 3 |
| `anthropic` | Anthropic | `anthropic` | `https://api.anthropic.com` | 2 |
| `google` | Google Gemini | `gemini` | `https://generativelanguage.googleapis.com` | 3 |
| `volcengine` | 火山方舟 | `openai-chat` | `https://ark.cn-beijing.volces.com/api/v3` | 2 |
| `siliconflow` | SiliconFlow | `openai-chat` | `https://api.siliconflow.cn/v1` | 2 |
| `xai` | xAI | `openai-chat` | `https://api.x.ai/v1` | 1 |
| `openrouter` | OpenRouter | `openai-chat` | `https://openrouter.ai/api/v1` | 0 |
| `deepseek` | DeepSeek | `openai-chat` | `https://api.deepseek.com/v1` | 1 |
| `ollama` | Ollama | `openai-chat` | `http://localhost:11434/v1` | 0 |
| `routin-ai` | Routin AI | `openai-chat` | `https://api.routin.ai/v1` | 103 |

内置服务商省略的名称、协议、Base URL、描述、预设版本和模型由预设补全。若 URL 显式提供这些字段，则显式值优先。打开页面时如果当前设备已经存在同一 `builtinId`，会按 ID 更新，不会重复创建渠道；已有模型设置不会被 URL 中的预设模型覆盖。

`ollama` 预设声明无需 API Key；其他内置服务商通常需要 Key。是否自动导入仍由 `autoApply` 和 URL 是否携带 Key 决定。

### 3.3 自定义服务商

自定义服务商必须提供 `name`、`type` 和 `baseUrl`：

```json
{
  "name": "My Relay",
  "type": "openai-chat",
  "baseUrl": "https://relay.example.com/v1",
  "apiKey": "key-example",
  "models": [
    {
      "id": "my-model",
      "name": "My Model",
      "category": "chat",
      "enabled": true
    }
  ]
}
```

`apiKey` 和 `models` 可以省略。省略 `models` 时，服务商会被落库，但不会自动请求上游 `/models`；用户可以在设置页手动添加模型或点击拉取模型。自定义服务商按名称（大小写不敏感）更新。

### 3.4 Provider 字段

| 字段 | 类型 | 说明 |
|---|---|---|
| `builtinId` | string | 内置预设 ID。存在时按内置渠道处理 |
| `name` | string | 自定义服务商必填；内置服务商可省略 |
| `type` | string | 自定义服务商必填；内置服务商可省略 |
| `baseUrl` | string | 自定义服务商必填；必须是绝对 HTTP/HTTPS 地址 |
| `apiKey` | string | 可选，明文载荷。不能传空字符串清除 Key |
| `description` | string | 可选展示描述，最多 1000 字符 |
| `icon` | string | 可选图标数据，最多 2048 字符 |
| `presetVersion` | integer | 可选预设版本，范围 1–100000 |
| `models` | array | 可选，最多 200 个模型 |

允许的 Provider 协议：

```text
openai-chat
openai-responses
anthropic
gemini
openai-images
openai-video
seedance-video
xai-video
```

`baseUrl` 的限制：

- 只能是绝对 `http://` 或 `https://` 地址，最多 2048 字符
- 拒绝用户信息，例如 `https://user:password@example.com/v1`
- 拒绝 query 和 fragment，例如 `?token=...`、`#fragment`
- 允许 `localhost`、`127.0.0.1` 等本地地址，以兼容 Ollama
- `anthropic` 会去掉尾部 `/v1` 或 `/messages`；`gemini` 会去掉尾部 `/openai`

### 3.5 Model 字段

URL 契约中模型标识字段名是 `id`：

| 字段 | 类型 | 默认/限制 | 说明 |
|---|---|---|---|
| `id` | string | 必填，最多 200 字符 | 上游模型 ID |
| `name` | string | 默认为 `id`，最多 200 字符 | 展示名称 |
| `category` | string | 默认为 `chat` | `chat`、`image`、`video`、`embedding`、`speech` |
| `type` | string | 可选 | 模型协议覆盖；省略时按服务商和分类解析 |
| `icon` | string | 可选，最多 2048 字符 | 模型图标数据 |
| `contextLength` | integer | 可选，1–100000000 | 上下文长度 |
| `maxOutputTokens` | integer | 可选，1–10000000 | 最大输出 Token 数 |
| `supportsVision` | boolean | 默认为 `false` | 是否支持视觉输入 |
| `supportsFunctionCall` | boolean | 默认为 `false` | 是否支持工具/函数调用 |
| `supportsThinking` | boolean | 默认为 `false` | 是否支持思考过程 |
| `enabled` | boolean | 默认为 `true` | 是否启用模型 |

导入到 HTTP API 时，模型字段名转换为 `modelKey`；URL 生成方不需要使用 `modelKey`。

## 4. 导入语义

### API Key

| URL 中的 `apiKey` | 新服务商 | 已有服务商 |
|---|---|---|
| 省略 | 不设置 Key | 保留原 Key |
| 非空字符串 | 加密后写入 | 替换原 Key |
| 空字符串 | 拒绝请求 | 拒绝请求 |

URL 导入不支持清空已有 Key。需要清除 Key 时，请使用设置页或普通 Provider 更新接口的三态语义。

### 模型

- 显式提供 `models`：只尝试添加 URL 中声明的模型。
- 内置服务商省略 `models`：展开当前预设模型。
- 自定义服务商省略 `models`：使用空模型列表。
- 已有模型按 `(providerId, modelKey)` 去重，重复导入不会覆盖名称、启用状态、能力标记等用户设置。
- 导入过程中不会请求任何上游模型接口，因此不会因上游网络失败而阻断渠道落库，也不会扩大 SSRF 面。

### 自动应用和确认

| 场景 | 行为 |
|---|---|
| 带 API Key，无论 `autoApply` | 始终显示确认框，并展示脱敏 Key |
| 不带 API Key，`autoApply: true` | 加载完成后自动导入，不弹确认框 |
| 不带 API Key，`autoApply` 缺省或为 `false` | 显示确认框 |
| 用户取消 | 清理 query/hash，不发写入请求 |
| 导入失败 | 清理 query/hash，保留错误提示，可重试 |

URL 参数会在识别后从地址栏移除，防止刷新页面重复触发。清理地址栏不等同于撤回已经暴露的 API Key。

## 5. 生成链接

### 浏览器 JavaScript

```ts
const config = {
  autoApply: false,
  providers: [
    { builtinId: "openai", apiKey: "sk-example" },
    {
      name: "My Relay",
      type: "openai-chat",
      baseUrl: "https://relay.example.com/v1",
      models: [{ id: "my-model", category: "chat", enabled: true }],
    },
  ],
};

const json = JSON.stringify(config);
const bytes = new TextEncoder().encode(json);
let binary = "";
for (const byte of bytes) binary += String.fromCharCode(byte);

const payload = btoa(binary)
  .replace(/\+/g, "-")
  .replace(/\//g, "_")
  .replace(/=+$/, "");

const link = `https://sol.example.com/canvas#settings=base64url:${payload}`;
```

生产代码应使用成熟的 Base64URL 实现；示例中的 `binary` 拼接只适合小型载荷。当前解码后的 JSON payload 上限为 512 KiB。

### Node.js

```js
const payload = Buffer.from(JSON.stringify(config), "utf8").toString("base64url");
const link = `https://sol.example.com/canvas#settings=base64url:${payload}`;
```

### Python

```python
import base64
import json

payload = base64.urlsafe_b64encode(
    json.dumps(config, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
).rstrip(b"=").decode("ascii")
link = f"https://sol.example.com/canvas#settings=base64url:{payload}"
```

如果生成 query 形式的原始 JSON 或旧标准 Base64，必须对参数进行 URL 编码；不要直接把未编码的 `+` 放在 query 中。

## 6. HTTP 导入接口

页面完成预设补全后调用：

```http
POST /api/v1/ai/config/import
Content-Type: application/json
Cookie: <device cookie>
```

请求体中的字段已经是服务端契约形式，模型字段使用 `modelKey`：

```json
{
  "providers": [
    {
      "builtinId": "openai",
      "name": "OpenAI",
      "description": "GPT 与 DALL·E / Sora 系列",
      "icon": null,
      "presetVersion": 1,
      "type": "openai-chat",
      "apiKey": "sk-example",
      "baseUrl": "https://api.openai.com/v1",
      "models": [
        {
          "modelKey": "gpt-image-1",
          "name": "GPT Image 1",
          "category": "image",
          "type": "openai-images",
          "enabled": true
        }
      ]
    }
  ]
}
```

成功响应：

```json
{
  "created": 1,
  "updated": 0
}
```

接口不返回明文 API Key。直接调用该接口的集成方必须自行完成内置预设补全；推荐使用页面 URL 导入流程，以确保前端预设与设置页保持一致。

常见错误：

| 状态码 | 含义 |
|---:|---|
| `401` | 缺少或无效的设备 Cookie |
| `400` | 载荷、服务商、协议、URL、模型或 Key 校验失败；响应中的 `details` 给出具体原因 |

错误响应形态：

```json
{
  "error": "invalid_request",
  "details": ["provider 'My Relay' baseUrl must not include a query or fragment"]
}
```

## 7. 对接检查清单

- [ ] 内置渠道只传稳定的 `builtinId`，不要把预设名称或 URL 当作唯一标识
- [ ] 自定义渠道同时提供 `name`、`type`、`baseUrl`
- [ ] 新链接使用 hash + `base64url:`，不要把 API Key 放到 query
- [ ] 生成前检查 UTF-8 解码后的 payload 不超过 512 KiB
- [ ] API Key 使用短期、最小权限 Key；不要把永久生产 Key 放入分享链接
- [ ] 对带 Key 的链接保留人工确认，不要在生成方把 `autoApply` 当作强制指令
- [ ] 自定义渠道没有模型时，在设置页提供手动添加或拉取模型入口
- [ ] 不要假设导入会请求上游 `/models`
- [ ] 处理 `401` 时先完成设备握手，处理 `400` 时展示 `details`

## 8. 实现参考

- 前端解析和补全：[web/features/settings/url-config.ts](../web/features/settings/url-config.ts)
- 前端导入提示：[web/features/settings/url-config-prompt.tsx](../web/features/settings/url-config-prompt.tsx)
- 后端导入端点：[AiConfigImportEndpoints.cs](../src/Sol.Api/Endpoints/AiConfigImportEndpoints.cs)
- AI Provider API 契约：[ProviderContracts.cs](../src/Sol.Application/Contracts/Ai/ProviderContracts.cs)
- 预设目录：[web/features/ai/presets/index.ts](../web/features/ai/presets/index.ts)
