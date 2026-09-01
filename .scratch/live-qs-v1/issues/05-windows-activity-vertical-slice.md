# 05 — 打通 Windows 活动纵向链路

**What to build:** 让一个真实 Windows 前台活动区间从现有采样器进入持久 outbox，经版本化批量协议写入 MongoDB，最终在 Owner Web 时间线中可见。

**Blocked by:** 02 — 建立可执行契约基础; 04 — 交付分级凭据管理.

**Status:** resolved

- [x] 前台活动 Schema 明确定义区间、应用标识、AFK、来源、finalized 和 revision 语义。
- [x] 现有 Win32 采样、SQLite 和托盘生命周期继续工作，协议迁移不要求重写平台适配器。
- [x] Windows 为逻辑区间保持稳定事件标识，并在区间延长或结束时递增 revision。
- [x] 批量上传逐项返回 accepted、duplicate、stale revision 或 rejected，允许部分成功。
- [x] 客户端只在对应 revision 获得确认后移除 outbox 项，永久拒绝不会无限重试。
- [x] 原始窗口标题和完整执行路径不会出现在上传请求或服务端事件中。
- [x] Owner 能在 WebUI 时间线看到来自 Windows 的区间、应用、设备和同步时间。
- [x] 真实 HTTP 与 MongoDB 测试证明重复发送十次仍只有一个最新逻辑事件，旧 revision 无法覆盖新 revision。

## Answer

契约核查先行：`activity.interval.v1` 已完整覆盖清单第 1 项的六类语义——区间
（信封 start_at/end_at + payload.duration，final 时 end_at 必填的 if/then）、
应用标识（application_id + 可选 application_label/subject_id，描述明确
"never a full executable path"）、AFK（is_afk）、来源（source.kind 枚举
windows.foreground | android.accessibility + record_id）、finalized
（信封 finalization_state checkpoint | final）、revision（信封 revision ≥ 1，
OpenAPI EventAcknowledgement 枚举已含 stale_revision）。**契约零改动**，
`contracts check` 确认四端派生模型保持同步。

服务端补齐票据 04 明确推迟的两块（`server/src/modules/events/`）：

- **修订语义**：`ingestBatchItem` 按 raw_hash（owner/event_id 的 HMAC）定位逻辑
  事件后做三方比较——同修订重投返回 `duplicate`（幂等确认）；低修订返回
  `stale_revision` 且绝不覆盖（SPEC 实现决策 12"较低或相等修订不能替换更高
  修订"）；高修订先把被取代的快照原样归档进新增的 `EventRevision` 集合
  （`{event_id}:{revision}` 幂等 upsert，事实不销毁、可审计），再以
  `updateOne({raw_hash, revision: 已比较修订}, $set)` 条件更新原子替换存储
  事实（SPEC"竞争修订原子解决"），丢失竞态则重读重答（上限 5 轮）。
  `test/events-revisions.test.ts` 用真实 HTTP + MongoDB 证明：同一事件连发
  十次恰有一个最新逻辑事件（1 accepted + 9 duplicate，count=1）；旧修订无法
  覆盖新修订；单批四种结果混合的部分成功；被取代修订在归档中可查。
- **payload registry**（`payload-registry.ts`）：activity.interval.v1 深度校验
  ——必填 application_id/is_afk/duration、duration 单位强制 ms 且为非负整数、
  application_id 拒绝含路径分隔符或盘符前缀（服务端侧隐私护栏）、未知载荷
  字段拒绝（原始标题无处藏身）、final 必须携带 end_at、end_at 不得早于
  start_at、end_at 存在时 duration 必须与区间边界一致（±1ms 舍入容差）。
  失败统一 `rejected` + 稳定码 `invalid_event` 与可诊断信息。

Windows 重建协议层（`Infrastructure/Sync/`），平台适配器不动：

- `CloudSyncClient` 改为携带 Device Token 调 `POST /api/v1/events/batch`
  （Owner 在 WebUI 凭据页创建后粘贴到 Windows 设置；100 条按契约分批；
  用生成的 `ActivityIntervalEventV1`/`EventBatchRequest`/`EventBatchResponse`
  契约模型序列化，逐项 Acknowledgement 映射为 outcome）。
- 事件标识：`EventIds.ForSegment` 实现 RFC 4122 v5（SHA-1，固定命名空间，
  Python uuid5 交叉验证的已知向量测试），输入为 `设备ID + SQLite 库安装 GUID
  + 段号`——重启/重试/检查点都保持同一稳定 event_id，数据库重建则获得全新
  身份，绝不与已上传历史冲突。
- revision 语义：沿用 `activity_segments.sync_version` 作为修订号——新段
  revision 1（checkpoint），每次合并延长 +1（checkpoint 流式），新段插入使
  上一段定稿时再 +1 并置 finalized（final + end_at + duration）。
- outbox 语义：`SyncWorker.RunOnceAsync` 逐项消费 Acknowledgement——
  accepted/duplicate/stale_revision 均视为对应修订已确认，移除 outbox 行；
  rejected 永久标记（`sync_queue.permanent`，死信保留 last_error、仍在
  队列深度中可见，永不重试）；HTTP 层瞬时失败维持既有指数退避。
- SQLite 仅加列迁移（`finalized`/`permanent`/`install_guid`，PRAGMA
  table_info 守护的 ALTER TABLE）；Win32 采样、段合并/AFK 拆分、托盘状态机、
  设置、单实例均未改动。设置页云同步区新增 Owner ID 输入（默认 `local`，
  与服务端 DEFAULT_USER_ID 默认一致）。

WebUI：`web/src/api/events.ts` 用生成的 `EventPage` 契约类型读取
`GET /api/v1/events`（浏览器时区作为报告时区），使用页新增「活动区间
（版本化事件）」表格——开始/结束（未定稿显示"进行中"）、应用
（application_label ?? application_id）、设备、时长、状态（AFK/检查点或
已结束/修订号）、同步时间（provenance.observed_at），`completeness=partial`
显示警告标签，`next_cursor` 支持"加载更多"。

验证（Mongo 经 `docker compose up -d` 启动，结束后 `docker compose down`）：
`npm --prefix contracts run check`；`cd server && npm run typecheck &&
npm test && npm run build`（56 条测试全部通过，其中新增 7 条真实 HTTP +
MongoDB 覆盖修订幂等/覆盖/陈旧/部分成功与 payload 深度校验）；`cd web &&
npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=artifacts/check/`
（29 条通过：UUIDv5 已知向量、修订/定稿、永久拒绝、瞬态退避、契约信封
与隐私断言、分批）`&& dotnet build LiveQs.Windows/LiveQs.Windows.csproj
-o artifacts/verify`。本机 WPF 采集器常驻锁定默认 bin 输出，windows 检查
用 `-p:BaseOutputPath=artifacts/check/` 与 `-o artifacts/verify` 绕开
MSB3027（与 `scripts/check.ps1 windows` 的测试步不兼容，见 Comments）。

## Comments

- 设计决策（checkpoint 流式而非仅定稿上传）：沿用现有 SQLite 节奏——每次
  合并延长都重新入队，SyncWorker 以 revision=sync_version、
  finalization_state=checkpoint 上传当前边界。理由：SPEC 实现决策 14 明确
  "活动区间使用稳定标识与修订检查点"，长会话（如整天挂着的 IDE）若只在
  定稿后上传，进程崩溃即丢整段；检查点让离线/宕机最多丢一个采样间隔。
  代价是活跃时约每采样周期一批请求（与旧 ingest 节奏相同，LAN 内可承受）。
  定稿时机：仅当新段插入时定稿上一段（合并规则只延长最新段，这是确定性
  边界）；采集器中途死亡时服务端保留最后一个 checkpoint——按 SPEC 服务端
  不替设备延展事件，下次启动补发 final 修订。
- 设计决策（事件标识 = 设备 + 安装 GUID + 段号）：纯 (设备, 段号) 在重装/
  清库后会产生与云端历史的 event_id 碰撞（段号重新从 1 计数）。引入
  `sync_state.install_guid`（库内生成、随库生存）加入 v5 名字空间，实现
  "库存活期内稳定、库重建后全新"，无需服务器参与分配。
- 设计决策（修订替换的可审计性）：EventRevision 集合按
  `{event_id}:{revision}` 幂等归档被取代快照，EventModel 只存最新修订，
  读取路径不变（默认查询返回最新有效修订）。checkpoint 的中间快照因此
  全量留痕；Owner 修正/作废（invalidated + 审计）属后续修正票据，本票
  不预建。
- 设计决策（同修订不同内容返回 duplicate）：幂等以 (event_id, revision)
  为键；同修订内容差异视为客户端缺陷，不覆盖（修订没有更高）。低修订
  永远 stale_revision。
- 设计决策（owner_id 由设备回显）：契约信封要求 owner_id 且服务端校验
  与凭据属主一致；V1 单 Owner 身份是服务端 DEFAULT_USER_ID（默认
  `local`），设备侧无法自动得知，故 Windows 设置新增 Owner ID（默认
  `local`）由 Owner 配置。若运维改过 DEFAULT_USER_ID 而未同步设备设置，
  事件会被永久拒绝（死信可见、可诊断）。更优解（凭据视图下发属主或
  服务端绑定性放宽）留给后续票据重新评估。
- 设计决策（死信可见性）：`GetPendingSyncCountAsync` 统计含 permanent 行
  ——被永久拒绝的条目对 Owner 仍是"待处理"，配合托盘 LastError（含稳定
  错误码）提供最小可见性；专门的失败队列界面属诊断票据（story 27/28）。
- 机器约束：本机常驻的 `LiveQs.Windows` 采集器锁定 WPF 默认 bin 输出，
  windows 检查须用 `dotnet test LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
  -p:BaseOutputPath=artifacts/check/` 与 `dotnet build
  LiveQs.Windows/LiveQs.Windows.csproj -o artifacts/verify`；`scripts/check.ps1
  windows` 的测试步骤会撞 MSB3027，在采集器停止前不要跑。
- 已知边界与后续：真实 GetForegroundWindow/AFK/托盘行为无法自动化覆盖，
  需真机冒烟（采集器 + WebUI 时间线联测）；安卓侧属票据 12–14；WebUI
  报告时区暂用浏览器时区（报告时区设置为汇总票据范围）；修订竞态循环
  上限 5 轮后按 500 兜底（单写者场景实际不可达）。
