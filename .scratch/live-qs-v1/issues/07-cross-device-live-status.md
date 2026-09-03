# 07 — 交付跨设备实时状态

**What to build:** 让 Owner 在 WebUI 同时看到 Windows 与 Android 的最新当前状态和在线性，同时确保心跳只是可过期投影，不会污染历史统计。

**Blocked by:** 02 — 建立可执行契约基础; 04 — 交付分级凭据管理.

**Status:** resolved

- [x] 心跳契约包含设备、采集时间、当前类型和最小结构化上下文，并执行 Device Token 限制。
- [x] Windows 与 Android 能使用现有生命周期机制发送或排队心跳。
- [x] WebUI 并列展示所有设备状态，不推断唯一全局注意力焦点。
- [x] 正常报告时状态年龄不超过三十秒，停止心跳后六十秒内显示离线。
- [x] 重复或乱序心跳不会让设备状态倒退。
- [x] 心跳数据不生成历史活动事件，也不参与任意使用时长汇总。
- [x] HTTP 集成测试使用可控时间验证并发设备、乱序更新和自动过期。

## Answer

本票实现跨越一次中断：第一位代理的未提交 WIP（契约、服务端、WebUI、Windows）
经完整审计后采纳，Android 发送端、Windows 心跳测试与若干评审修复由后续
代理补齐（见 Comments 第一条）。

契约先行：`openapi.yaml` 新增 Heartbeats 标签与两个端点，四端派生模型
重新生成并通过漂移检查。

- **`POST /api/v1/heartbeats`**（仅 Device Token，`events:write`）：
  `platform`（windows/android）、`captured_at`（观测时刻的 UTC
  instant，排序/新鲜度/过期全由它决定，离线排队的心跳无法虚报新鲜）、
  最小结构化 `activity`（`application_id`/`application_label`/
  `is_afk`，`additionalProperties: false` 让原始窗口标题无处藏身，
  路径分隔符/盘符形 application_id 拒绝）；`device_name` 仅展示用，
  服务端设备身份一律取自凭据 ID（沿用票据 04 的服务端绑定）。
- **`GET /api/v1/status`**（仅 Owner Session）：每台上报过的设备一条
  独立条目（device_id、device_name、platform、online、age_seconds、
  captured_at、activity），按采集时间旧→新排序并附 `server_time`；
  响应结构上不存在"全局焦点"，多设备天然并列。

服务端（`server/src/modules/heartbeats/`）：

- 心跳只写新增的 `DeviceStatus` 投影集合（device_key=凭据 ID 唯一），
  每设备最多一行；TTL 索引（6 小时）只作长期兜底清理，在线/离线与
  状态年龄在**读取时**按 `captured_at` 计算（60 秒阈值），停止心跳的
  设备先变离线、继续以离线状态留在列表中，直到兜底清理。
- 单调性：首条 `create`，冲突后按 `captured_at: { $lte: 传入 }` 条件
  更新——重复与乱序心跳 204 确认但绝不回退存储状态；采集时刻超前
  服务器时钟 5 分钟以上拒绝 400，防止投毒冻结投影。
- `shared/clock.ts` 时钟缝注入 `createApp`，HTTP 集成测试用可控时钟
  精确驱动 30/60 秒阈值。
- 心跳与历史完全隔离：服务只触碰投影集合；集成测试断言心跳之后
  事件集合为 0、`GET /events` 无数据、usage 日指标两种分钟数均为 0。
- 评审修复：TTL 兜底从 1 小时放宽到 6 小时并把测试时钟基点改为真实
  当前时间（旧基点落在真实过去，TTL 清扫可能在小概率窗口删掉测试
  投影造成 flake）；`isDuplicateKeyError` 三处重复提取为
  `shared/mongo.ts`。

WebUI：仪表盘「设备状态」改读契约 `GET /status`（`web/src/api/
status.ts`），设备卡（DeviceCard）显示在线/离线标签、当前应用
（label ?? id，AFK 显示 AFK）与状态年龄（秒/分/小时），多设备并列
渲染，不做单一焦点推断。

Windows：`HeartbeatWorker`（HostedService，15 秒节奏 < 30 秒新鲜度
要求）复用前台采样器捕获当前状态，沿用云同步开关/暂停/令牌门控；
`HeartbeatClient` 携带 Device Token POST `/api/v1/heartbeats`。心跳
是瞬时断言：不进出事件 outbox、失败仅记日志等下一节奏重试。

Android：无障碍服务（现有生命周期）+ 心跳 spool 队列保留，网络层
从已删除的 legacy `/api/v1/ingest/heartbeat` 改为契约
`HeartbeatRequest`（kotlinx 序列化生成模型，platform=android，
`captured_at` 为观测时刻，drainer 丢弃超过 5 分钟的陈旧排队项）；
新增 20 秒重申定时器，前台未变化也持续报告，避免"同一应用停留
超过 60 秒即离线"的抖动；删除 legacy `LifeHeartbeat` 模型。

测试与验证（Mongo 经 `docker compose up -d`）：服务端
`test/heartbeats.test.ts` 11 条可控时间真实 HTTP + MongoDB 集成测试
覆盖并发设备独立轨道、乱序不回退、重复幂等、自动过期（30/60 秒
边界逐秒推进）、鉴权边界（Owner Session/Query Token/无凭据全拒）、
路径形 id 与未知字段拒绝、事件与指标零污染；Windows 新增 5 条
（线上形状与隐私断言、非 2xx 抛错、采样驱动、三重门控、失败不中断
循环）；Android 心跳序列化形状测试。全量验证：`npm --prefix
contracts run check`；`cd server && npm run typecheck && npm test &&
npm run build`（97 条通过）；`cd web && npm run build`；`cd windows
&& dotnet test LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（34 条通过）；`cd android &&
./gradlew.bat lint test assembleDebug`。

## Comments

- 中断恢复说明：第一位代理被瞬态模型错误中断，遗留下未提交的 WIP。
  审计结论：契约、服务端模块（routes/service/clock/集成测试）、WebUI、
  Windows 发送端完整且与 SPEC 一致，全部采纳；缺口为 Android 发送端
  完全未动（legacy 客户端仍指向票据 04 已删除的
  `/api/v1/ingest/heartbeat`，链路已死）、Windows 测试只搬了 HTTP
  桩未写心跳用例、无 `event-merge` 删除决策记录。本票收尾补齐了
  这三处，并完成 Standards/Spec 双轴自审。
- 设计决策（`shared/event-merge.ts` 删除）：票据 04 曾保留它"供票据
  07 复用"，但 V1 模型下它的用途（服务端把心跳合并成历史事件、
  关闭开放区间）正是 SPEC 实现决策 13 明确禁止的行为——心跳是投影，
  历史区间由客户端生成。新实现无任何引用（grep 证实），按 ADR-0005
  删除而非保留死代码。
- 设计决策（读取时计算在线/过期，而非存储 online 标志 + 定时任务）：
  离线是"现在距最后一次心跳多久"的纯函数，存储标志需要后台定时器
  且测试难以可控时间驱动；读取时计算让 60 秒阈值精确到秒、时钟缝
  注入即可确定性测试，TTL 索引只兜底集合增长。6 小时兜底内的离线
  设备仍可见，配合票据 06 的同步诊断区分"没活动"与"采集器坏了"。
- 设计决策（`/status` 仅 Owner Session）：SPEC story 52 要求 AI 能读
  "当前上下文"，但本票清单只要求 WebUI 展示；向 Query Token 开放
  `GET /status` 属票据 16（只读 AI Skill）的契约扩展点，现在收紧、
  到时放宽是兼容变更。
- 设计决策（Android 重申定时器）：legacy 心跳纯事件驱动，用户停留
  同一应用超过 60 秒设备就会"离线"，恢复切换窗口再"上线"，抖动。
  20 秒定时器（Handler，随无障碍服务生命周期启停）与 Windows 的
  15 秒节奏对齐，同用现有服务进程，不新增常驻组件。已知盲点：息屏
  不产生 WINDOW_STATE_CHANGED，最后已知应用会被持续重申为当前
  （accessibility 无 AFK 信号），真正的 AFK/息屏语义留给票据 12+。
- 设计决策（`captured_at` 决定一切时间语义）：客户端时钟与服务器
  有偏差、离线排队的心跳更不可能新鲜；以观测时刻而非到达时刻排序，
  迟到的旧心跳天然被单调守卫拒之门外，未来偏移用 5 分钟上限拦截
  （更宽会冻结投影直到 TTL 清理，服务端宁可拒绝）。
- 已知边界：每次心跳都记 `credential.use` 审计并节流更新
  last_used_at（15 秒节奏 × 设备数会放大审计量，与既有 events/batch
  的使用审计同一模式，审计轮转/节流留后续）；Android 事件同步
  （`/api/v1/ingest/events`）仍为 legacy 通道，属票据 12 范围；
  心跳 application_label 来自本地应用显示名（文件描述/应用标签），
  不含窗口标题文本。
- 机器约束：本机常驻采集器锁定 WPF 默认 bin 输出，windows 检查沿用
  `-p:BaseOutputPath=artifacts/check/`（见票据 05 Comments）；全程
  未 `docker compose down`，未触碰运行中的采集器。
