# 设备识别

## 先说结论：需求只能部分满足

原始需求是"生成设备唯一 ID 绑定用户，**用户切换浏览器也需要能识别**"。

**跨浏览器识别同一设备在技术上无法可靠实现。** 这不是实现水平问题，是浏览器沙箱的物理限制：

- **高熵信号是浏览器相关的。** canvas、音频、字体指纹在不同引擎下必然产生不同值——字体光栅化算法、抗锯齿策略、图形后端都不同。它们能高精度识别**浏览器**，对识别**设备**毫无帮助。
- **跨浏览器稳定的信号熵太低。** 时区、操作系统、CPU 核数、内存、屏幕分辨率加起来约 **10-15 bits 且高度相关**，不足以区分个体。
- **行业现状印证了这一点。** FingerprintJS 维护者明确表态：同一设备不同浏览器得到不同 ID 是"正确且符合预期的行为"（[issue #762](https://github.com/fingerprintjs/fingerprintjs/issues/762)）。其 $199/月的 Pro 版宣称 99.5% 准确率，是**按浏览器**而非按设备。
- **学术界最佳结果也不够。** Cao et al., NDSS 2017 达到跨浏览器唯一性 83.24%——意味着约 **17% 的用户会与陌生人碰撞**。而该数据早于 Safari 26 默认开启的高级指纹防护。
- **趋势在恶化。** Safari 26 默认保护 screen/hardwareConcurrency/canvas/WebAudio；Firefox 正在弃用 `WEBGL_debug_renderer_info`；UA-CH 的 `getHighEntropyValues` 仅 Chromium 支持——恰好在最需要跨浏览器的场景下不可用。

**如果业务确实需要硬性的跨设备绑定**，唯一可靠的路径在浏览器沙箱之外：原生 App、账号登录、手机/邮箱验证，或设备绑定的 passkey。

## 采用的分层方案

### 第一层：确定性（~99%，业务真正应该绑定的 ID）

服务端生成 UUIDv7，同时通过两条独立通道下发：

1. **HttpOnly Cookie**（`sol_did`）—— JS 读不到，防 XSS 窃取
2. **握手响应体中的 `deviceId`** —— 客户端读取后写入 localStorage

⚠️ **这里有个常见设计错误**：`HttpOnly` Cookie **无法**被 JS 镜像到 localStorage——那正是 `HttpOnly` 的设计目的。所以 deviceId 必须在**响应体**里再返回一次。两条通道互为备份：清 Cookie 时 localStorage 还在，清 localStorage 时 Cookie 还在。

### 第二层：概率性（置信度上限 0.6，**仅作提示**）

服务端用浏览器无关信号 + IP 段计算 `fp_coarse`，尝试把新浏览器归到已知设备。

**置信度硬上限 0.6**，且 `LinkConfidence.Create` 会强制钳位——调用方无法把猜测抬升成事实。业务规则应当：

- **≥ 1.0（Cookie 命中）** 才用于硬性放行/拦截
- **0.6（概率关联）** 最多用于增加摩擦（验证码、二次确认），**绝不能**据此拒绝真实用户

## 客户端 → 服务端 载荷契约

```json
{
  "v": 1,
  "stable": {
    "timeZone": "Asia/Shanghai",
    "platform": "MacIntel",
    "hardwareConcurrency": 8,
    "deviceMemoryGb": 8,
    "screen": { "w": 1512, "h": 982, "colorDepth": 24, "dpr": 2 },
    "gpu": { "vendor": "Apple", "renderer": "Apple M1 Pro" },
    "primaryLanguage": "zh"
  },
  "volatile": {
    "userAgent": "Mozilla/5.0 …",
    "languages": ["zh-CN", "en-US"],
    "canvasHash": "3f2a…", "audioHash": "9c17…", "fontsHash": "b840…"
  },
  "clientStoredId": "0192f4c1-…"
}
```

- `stable` = 浏览器**无关**信号 → 参与 `fp_coarse`（跨浏览器关联用）
- `volatile` = 浏览器**相关**信号 → 仅参与 `fp_exact`（识别同一浏览器用）
- **所有字段可选**。隐私加固的浏览器会拒绝提供其中若干项，服务端把缺失归一化为 `"?"` 哨兵值，产出低熵但稳定的哈希，而不是报错。

客户端实现见 [`web/lib/device.ts`](../web/lib/device.ts)。

## 归一化与哈希

**必须先量化再哈希**，否则窗口缩放、DPR 浮点噪声、驱动版本变化都会击碎哈希，设备每次访问都像新的。

| 信号 | 量化方式 | 原因 |
|---|---|---|
| `screen` | **方向无关**：输出 `(max(w,h), min(w,h))`，丢弃旋转 | 手机横竖屏切换不应变成新设备 |
| `dpr` | 保留 1 位小数 | 缩放时浏览器上报带浮点噪声 |
| `deviceMemoryGb` | 吸附到 `{0.25,0.5,1,2,4,8,16,32,64}` | 浏览器本身就做了粗化 |
| `hardwareConcurrency` | 分桶 | 上报值会被浏览器钳制 |
| `gpu.renderer` | 剥离驱动版本号，只留型号 token | 驱动更新不应改变身份 |
| `primaryLanguage` | 只取首段（`zh-CN` → `zh`） | 避免同设备因语言变体分裂 |
| `timeZone` | 用 IANA 字符串，**丢弃 tzOffset** | offset 可推导且受夏令时影响 |

```
fp_coarse = SHA256("v1|" + canonical(stable量化) + "|ip=" + ipPrefix + "|" + PEPPER)
fp_exact  = SHA256("v1|" + canonical(stable量化 ∪ volatile) + "|" + PEPPER)
```

- canonical = 固定键序、无空白、带版本前缀
- `ipPrefix`：IPv4 取 `/24`，IPv6 取 `/48`（保留"同一网络"信号，同时抗 DHCP 续租与 SLAAC 轮换，也减少个人数据留存）
- `PEPPER` 是服务端密钥，使存储的指纹无法从公开可见的信号值反算，也无法与其他系统的库做碰撞

**信号原始分量与哈希一并存储**（`device.signals` jsonb）。只存哈希会让未来的信号集升级失去全部历史设备；存分量才能重新推导。

## 解析算法

```
1. Cookie 存在且 device 行存在
       → 确定性命中，刷新 last_seen，confidence 1.0

2. 无 Cookie 但 clientStoredId 匹配到 device 行
       → localStorage 恢复（同浏览器清了 Cookie），补发 Cookie，confidence 0.95

3. 都不命中 → 新建 device_id (UUIDv7)，再尝试模糊关联：

   candidates = device WHERE fp_coarse = @coarse
                  AND ip_prefix   = @ipPrefix
                  AND sig_version = @v
                  AND last_seen  >= now() - 24h
                  AND fp_exact   <> @exact      ← 同 fp_exact 即同浏览器，本该有 Cookie

   3a. 候选 = 0                     → 新建 visitor
   3b. visitor_id 唯一 且 候选数 ≤ 5 → 关联，method=ProbabilisticCoarse，confidence 0.6
   3c. visitor_id 不唯一 或 超出上限 → 判定歧义，新建 visitor，记录指标，绝不猜测
```

### `MaxCoarseFanout` 是承重设计

在仅约 10-15 bits 相关熵的前提下，**一个共用 NAT 出口的办公室会让几十台陌生设备落到同一个 `(fp_coarse, ip_prefix)`**。没有这个基数上限，规则 3b 会把他们全部焊成一个巨型 visitor——一个人的行为被记到另一个人头上。

原则：**低熵指纹只有在同时"罕见"时才允许关联。**

查询会多取一行（`limit = MaxCoarseFanout + 1`），使"超出上限"可被检测，而不是被静默截断成任意子集。

### 关联是可逆的边，不是行合并

`device_link` 存的是边。误判只需一条 `DELETE`，而不是一次数据抢救。`ON CONFLICT` 时置信度**只升不降**，所以后来的确定性命中会覆盖早先的猜测，反之不会。

模糊命中时同步补发 Cookie，让确定性层尽快重新接管——概率层的影响面因此随时间收敛。

## 实测验证

对**原生 AOT 二进制**执行的完整矩阵：

| 用例 | 期望 | 实测 |
|---|---|---|
| T1 首次访问 | 新建 device + visitor，1.0 | ✅ `new=True conf=1 Deterministic` |
| T2 携带 Cookie | 同 device，1.0 | ✅ `new=False conf=1 Deterministic` |
| T3 无 Cookie 有 storedId | 同 device，0.95 | ✅ `new=False conf=0.95 Deterministic` |
| **T4 跨浏览器** | **新 device，同 visitor，0.6** | ✅ `new=True conf=0.6 ProbabilisticCoarse` |
| **T5 超出 fanout** | **拒绝关联，新建 visitor** | ✅ 第 6 台起 `conf=1 Deterministic`（独立 visitor） |
| T6 非法 storedId | 400 | ✅ HTTP 400 |
| T7 `/me` 无 Cookie | 404 | ✅ HTTP 404 |

T5 尤其重要：**只会关联、从不拒绝的实现等于没有实现这个护栏。**

## 合规义务

**指纹采集不是"无 Cookie 就免同意"。**

- **欧盟**：EDPB Guidelines 2/2023 明确指纹采集适用 ePrivacy 指令第 5(3) 条——读取 `screen`、`navigator`、canvas 等构成"访问终端设备中存储的信息"，**需要事先同意**，与 Cookie 同等对待。ePrivacy 下**不能**以"正当利益"为依据，只有同意或豁免。防欺诈可能落入"严格必要"豁免，但必须有文档论证且范围最小。
- **英国**：ICO 2026 年 4 月的存储与访问技术指引明确覆盖指纹采集，并公开表态指纹采集"不是公平的追踪手段"——理由正是它削弱了用户的选择权与控制权。
- **中国 PIPL**：设备标识符/指纹在可关联到自然人时属于个人信息（个人信息），需要告知并取得同意；对外提供与跨境传输需单独同意；同时受最小必要、透明度与个人权利响应义务约束。
- **美国 CCPA**：指纹属于"唯一标识符"，适用收集告知与选择退出权。

**实践要点**：信号数量最小化；哈希加 pepper 后存储；缩短留存周期；把安全/风控用途与营销用途在流程上分开；上线前做 DPIA。

## 运维注意

**轮换 `DeviceIdentity:Pepper` 会使全部 `fp_coarse`/`fp_exact` 失效**，孤立所有概率性关联（确定性 Cookie 关联不受影响）。正确做法是配合 `sig_version` 升版 + 双写窗口——**此迁移方案尚未设计，不宜在轮换当下临场发挥**。

`sig_version` 同样是信号集变更的迁移杠杆：升版后新旧方案可以并存，而不是一次性作废所有设备。
