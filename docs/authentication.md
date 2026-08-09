# 账号与第三方登录

## 用户状态

Sol 默认进入游客模式，不需要注册。游客的确定性身份仍由第一方 HttpOnly `sol_did` Cookie
和服务端 PostgreSQL 的 `device` 行组成；现有画布、渠道、素材、Agent 和 Skill API 不因游客
模式而改变。

用户在需要跨浏览器、跨设备访问时，可以使用 GitHub 登录。登录不会把游客数据放到浏览器
Cookie 里，也不会把 API Key 放到账号资料中，而是把当前 `device_id` 写入
`sol_account_device`。账号访问数据库时，服务端连接才会激活该账号对应的设备集合，因此：

```text
游客请求  -> 当前 device_id
登录请求  -> 当前 device_id + account session -> 该账号已明确关联的全部 device_id
```

清除浏览器数据不会删除云端数据；登录同一账号后，新设备会在第一次握手时被关联，并重新
看到账号作用域内的云端数据。退出登录会删除 session 和设备 Cookie，并从前端删除设备镜像，
随后以一个全新的游客设备继续使用。

## OAuth 安全边界

- OAuth state、PKCE verifier、provider、回跳路径和发起请求的设备都写入
  `sol_oauth_transaction`，state 只以 SHA-256 哈希入库，且回调时原子消费并检查过期时间。
- GitHub client secret 只存在 API 进程；浏览器只会被重定向到 GitHub，不会收到 secret 或上游
  access token。
- session Cookie 是随机不透明值，数据库只保存 SHA-256；Cookie 使用 HttpOnly、SameSite=Lax、
  生产环境 Secure 属性。
- 第三方身份的唯一键是 `(provider_key, subject)`，绝不按 email 自动合并账号。email 只是展示
  信息，因为不同 provider 对 email 的验证语义不同。
- 被账号关联的设备没有匹配 session 时，`DeviceContextMiddleware` 会阻断资源端点；不能仅凭
  旧的 `sol_did` Cookie 绕过账号登录。

## 多 provider 设计

`IExternalLoginProvider` 是唯一的 provider 适配边界。每个 provider 只负责：

1. 生成授权 URL；
2. 用 code 换取上游 profile；
3. 将上游 profile 归一化为 `provider_key + subject + displayName/email/avatarUrl`。

账号、session、OAuth transaction 和设备关联全部是 provider-neutral 的。新增 Google、Microsoft
或企业 OIDC 时，只需新增一个 provider adapter 并显式注册到
`IExternalLoginProviderRegistry`，不应在 `Account` 表添加 `github_id`、`google_id` 等列。

登录和绑定是两个不同的动作：

- `/api/v1/auth/login/{provider}`：登录已有 provider 身份，或为新身份创建账号；当前未关联的
  游客设备会被关联到该账号。
- `/api/v1/auth/link/{provider}`：必须已有有效 session，显式把另一个 provider 身份绑定到当前
  账号。如果该身份已经属于别的账号，返回冲突，不做隐式账号合并。

## 配置 GitHub

在 GitHub OAuth App 中把 Authorization callback URL 配置为：

```text
http://localhost:3000/api/v1/auth/callback/github
```

生产环境替换为浏览器实际访问的同源地址。通过环境变量提供 secret（不要提交到配置文件）：

```text
Authentication__PublicOrigin=https://sol.example.com
Authentication__GitHub__Enabled=true
Authentication__GitHub__ClientId=...
Authentication__GitHub__ClientSecret=...
Authentication__GitHub__Scope=read:user user:email
```

`Authentication:PublicOrigin` 必须是浏览器看到的 origin。开发环境 Next.js rewrite 会将上述
回调转发到 API；生产环境应让前端和 API 共享一个反向代理 origin。

使用 Docker Compose 时，从根目录 `.env.example` 创建未提交的 `.env`，填写
`PUBLIC_ORIGIN`、`GITHUB_OAUTH_CLIENT_ID` 与 `GITHUB_OAUTH_CLIENT_SECRET`，并将
`GITHUB_OAUTH_ENABLED` 设为 `true`。重新构建 `web` 与 `api` 后，GitHub 登录按钮会自动
出现在账户菜单中。
