# 06 — 交付同步诊断与失败队列

**What to build:** 让 Owner 能判断数据缺失是没有活动、尚未上传还是同步失败，并让客户端在断网和进程重启后继续可靠交付。

**Blocked by:** 05 — 打通 Windows 活动纵向链路.

**Status:** resolved

- [x] WebUI 显示每台设备最后采集、最后成功上传、待上传数量和最近错误。
- [x] 瞬时网络或服务端错误使用带抖动的指数退避，恢复后自动继续。
- [x] Schema 非法、scope 不足等永久失败进入可查看失败队列，不再无限重试。
- [x] 失败项显示稳定错误码和安全摘要，不泄露 Token 或本地原始敏感文本。
- [x] 客户端进程退出并重启后，未确认 outbox 和失败队列状态保持一致。
- [x] 自动化状态机测试覆盖成功、断网、部分确认、永久拒绝、stale revision 和重启。
- [x] 人工模拟断网后恢复，Owner 无需修改数据库即可观察队列逐步清空。

## Answer

契约先行：`openapi.yaml` 新增 **Diagnostics** 标签与 **`POST /api/v1/diagnostics/sync`**（仅 Device Token，`events:write`）与 **`GET /api/v1/diagnostics/sync`**（仅 Owner Session）。快照 = `platform`、可选 `device_name`（纯展示元数据）、`collected_at`（最近一次本地观测的采集时刻）、`last_successful_upload_at`、`oldest_pending_at`、`pending_count`（可重试 outbox 深度）、`permanent_failure_count`（死信数量）、`recent_errors`（≤ 10 条，`code`/`message`/`occurred_at`）。全部 `additionalProperties: false`：原始窗口标题、执行路径、通知正文与 Token 在快照里没有任何可藏字段，未知字段直接 400；`code` 强制 `^[a-z][a-z0-9_]*$` 稳定码形状、`message` 上限 300。四端派生模型重新生成，漂移门禁通过。

**快照通道选择（专用端点而非扩展心跳上下文）**：诊断随同步节奏推送（Windows SyncWorker 每轮、Android WorkManager 每 15 分钟），而非 15 秒心跳节奏——按心跳推会以每分钟 4 次的频率重写几乎不变的数据并放大凭据审计；心跳是"可过期当前状态投影"（CONTEXT.md，6 小时 TTL 只够长期兜底），而同步诊断是持久的每设备运营状态（设备离线一天后 Owner 仍须看到它的最后上传时间与死信），两者新鲜度语义不同；心跳还被前台采样门控，事件同步可在采样暂停时照常活跃；票据 07 的设计决策（"TTL 兜底内的离线设备仍可见，配合票据 06 的同步诊断区分没活动与采集器坏了"）本就预设了二者分离。

服务端（`server/src/modules/diagnostics/`）：`SyncDiagnostic` 投影按 device_key（= 凭据 ID，服务端绑定）upsert，last-write-wins（快照是尽力而为的节奏上报、从不排队，不存在乱序回退面）；读取时按服务器接收时刻计算 `age_seconds`（票据 07 同款读取时过期模式），设备停止推送时快照变陈旧而非消失；30 天 TTL 只兜底集合增长；所有时刻的未来偏移 > 5 分钟拒绝（防投毒）；集成测试断言诊断之后事件集合为 0。

Windows（票据 05 的机制之上补齐可见性）：SQLite 新增 `sync_errors` 环形缓冲（20 条，插入即剪枝）；`GetSyncOverviewAsync` 一次读出 pending（非永久）/permanent/最早待传（`MIN(started_utc)` over 队列）/最后采集（`MAX(last_sample_utc)`）/最后成功上传（`sync_state.last_success_utc`）；`SyncErrorDescriber` 把传输失败映射为稳定码 + 固定中文摘要（`server_error`/`server_rejected`/`network_error`/`request_timeout`/`invalid_sync_response`/`sync_failed`），**原始异常文本永不出设备**（可能嵌本地内容的响应体只留在本地日志与托盘）；永久拒绝复用服务器逐项稳定码（`invalid_event`、`insufficient_scope` 等），`SyncOutcome` 新增 `ErrorCode`，环形缓冲按码去重（一批 100 条同类拒绝只记 1 条）；SyncWorker 每轮（含空转与失败轮）从**本地存储**而非内存构建快照推送，推送失败仅记日志等下一轮重试。退避沿用票据 05 的 `15·2^n`（封顶 1 小时）+ 0–10 秒抖动，轮询循环天然恢复后自动续传。

Android（票据 12/13 的机制之上补齐推送）：`SyncDiagnosticsState`（JSON 文件，`lastCollectionAt`/`lastSuccessfulUploadAt`/`lastTransientError`，进程重启存活）；`SyncDiagnosticsReporter` 聚合 usage/health/payment 三域 `ContractEventSpoolQueue`（最早 `createdAt` → `oldest_pending_at`）与 `ContractSyncFailures`（服务器稳定码 + 契约安全文本原样携带），瞬时失败经 `describeTransientFailure` 映射为固定摘要（异常文本不外传），按时间倒序取 10 条随 `LifeSyncWorker` 每轮推送，推送失败使本轮 `Result.retry()`（下轮先重推）。退避 = WorkManager `BackoffPolicy.EXPONENTIAL`（1 分钟起）+ `NetworkType.CONNECTED` 约束（恢复联网自动续传），抖动委托操作系统调度（多设备天然错峰）。

WebUI：仪表盘新增**「同步诊断」**面板（`SyncDiagnosticCard`，与设备状态并列、10 秒轮询共用）：每设备显示最后采集、最后成功上传、最早待传、待上传/永久失败计数标签（> 0 时琥珀/红色高亮 + 左侧警示条）、快照年龄与最近错误列表（稳定码标签 + 摘要 + 时间）。"最后采集"来源的设计取舍：取设备上报的 `collected_at`（Windows = 本地库 `MAX(last_sample_utc)`，Android = 最近一次采集轮次时刻），**不用**心跳 `captured_at`——心跳只在设备在线时更新，断网期间本地采集照常而心跳冻结，用它会把"离线积压"误读成"没有采集"。

测试（自动可测部分）：

- 服务端真实 HTTP + MongoDB 集成 6 条（`test/diagnostics.test.ts`）：快照存储与 Owner 读取（年龄可控时钟驱动）、同设备替换 + 双设备独立轨道与排序、三类凭据鉴权边界、未知字段/非法码形状/超长消息/超窗错误条数/负数计数/未来偏移全拒（含"原始标题无处藏身"断言）、零事件零指标污染、未采集过的新装设备（字段缺省 → 读取端 null）。
- Windows 9 条（`DiagnosticsTests.cs`）：描述器矩阵（五类异常 → 稳定码，断言异常文本不进摘要）；环形缓冲 20 条上限 + 最新优先；概览计数（pending/permanent 分列、oldest、采集、最后成功）；**状态机全覆盖**——成功（清空 + 最后成功落库）、断网（pending 1 + 网络错误 + 退避未到不重试）、部分确认 + 永久拒绝（死信计数 + 稳定码入历史 + 永不重试 + 空转轮仍推送）、stale revision（确认移除清空）、**重启**（同库新 worker 的快照与重启前一致：未确认 outbox 与错误历史均在，退避过后正常排空）；快照线上形状（Bearer、无 window_title/原始标题、计数与码正确）。
- Android 7 条（`SyncDiagnosticsReporterTest.kt`/`SyncDiagnosticsStateTest.kt`）：跨域聚合计数 + 时间戳 + 错误合并倒序（含瞬时错误）、10 条窗口裁剪、瞬时摘要稳定且不含异常细节、推送失败传播使本轮重试、状态文件重启存活与损坏容错。drainer 六种确认语义由票据 13 既有测试继续覆盖。

验证（Mongo 经 `docker compose up -d`，全程未 `docker compose down`；测试使用独立 `live_qs_test_*` 库；未触碰运行中的采集器与 live 冒烟库）：`npm --prefix contracts run check`（lint + 6 条 + 四端模型同步）；`cd server && npm run typecheck && npm test && npm run build`（**174 条全部通过**，新增 6 条）；`cd web && npm run build`；`cd windows && dotnet test LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=artifacts/check/`（**67 条通过**，新增 9 条）；`cd android && ./gradlew.bat lint test assembleDebug`（**81 条 JVM 单测通过**，新增 7 条）。

## Comments

- 设计决策（pending 与 permanent 在快照中分列，而托盘 `GetPendingSyncCountAsync` 维持含死信的总深度）：托盘语义来自票据 05 的决策（"被永久拒绝的条目对 Owner 仍是待处理"），保持不变；快照把两者拆开才能回答"在重试中"还是"永远卡住"，WebUI 的警示条按二者各自 > 0 独立点亮。两处口径不同是刻意的，均已在代码注释中写明。
- 设计决策（Windows 最后成功上传读 `sync_state.last_success_utc` 而非内存字段）：内存字段重启即丢，快照必须从存储构建才能兑现"重启后状态一致"；这也让"最后成功上传"在进程死亡后仍然诚实（最后落库事实），而非归零。Android 同理落在 `SyncDiagnosticsState` 文件。
- 设计决策（快照 best-effort、从不排队）：快照本身进 outbox 会在长时间断网后堆出陈旧队列，恢复后逐条重放过期状态毫无意义——恢复后第一轮同步推送的就是新快照。断网期间服务端只能看到"最后一次推送 + 心跳离线"，这是服务器侧可见性的物理边界；`age_seconds` 把这个边界显性化给 Owner，而不是伪装新鲜。
- 设计决策（错误摘要的三层安全模型）：(a) 永久拒绝——服务器逐项返回的 `code`/`message` 是契约级文本（如 "duration 必须与区间边界一致"），稳定且无原始内容，原样携带；(b) 瞬时传输失败——异常 `Message` 可能嵌入响应体、URL、本地内容，两端一律替换为该码的固定摘要，异常原文只留在本地日志/托盘；(c) 快照 schema `additionalProperties: false` + `code` pattern + `message` 上限兜底，自由文本没有传输通道。
- 设计决策（Windows 每轮结尾推送、空转轮也推）：Owner 在恢复后要"看着队列逐步清空"，每次 10 秒轮询结束的最新计数就是那个阶梯；只在有流量时推送会让断网恢复后的第二轮起画面静默，无法区分"已清空"与"没推送"。
- 评审修复（Standards 轴自审）：routes 的四处 zod 日期 refine 收敛为共享 `isoInstant`；SyncDiagnosticCard 补上 `.device-main` 最小样式；Android `recordSuccessfulUpload` 从"传输成功（sent>0）"收紧为"修订获确认（accepted+duplicate+stale>0）"，与契约描述"最近一次获确认的上传"及 Windows worker 语义对齐；`collected_at` 在报告端改为可选（新装设备在首次采集前无法诚实填值），读取端保持必有键（服务端恒输出）。
- 已知边界与后续：清单第 7 项中"模拟断网-恢复后观察排空"的**机制**（每轮快照 + WebUI 面板 + 无需触库）已就绪并自动化，"人工模拟断网"本身是真机验收动作（暂停云同步/断网若干分钟后恢复，属票据 18 七天验收的步骤），本机常驻采集器继续使用旧二进制，Owner 重启采集器后新推送链路才生效；服务端快照列表暂无编辑/清除入口（死信的处置属后续运维票据）；Android 的 `collected_at` 粒度是"最近一次采集轮次"，与 Windows 的逐样本时间戳粒度不同（UsageStats 由系统写入，无更细的本地采集时刻可报）；`GET /diagnostics/sync` 当前仅 Owner Session，向 Query Token 开放属票据 16 的契约扩展点（与票据 07 的 `/status` 同一先紧后松路径）；快照推送与心跳一样会逐次记录凭据使用审计（节奏受同步 cadence 限制，远低于心跳）。
