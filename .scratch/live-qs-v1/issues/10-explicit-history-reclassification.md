# 10 — 交付显式历史重分类

**What to build:** 让 Owner 在改进规则后主动选择重分类仍有本地上下文的历史活动，同时避免规则编辑导致报表在未确认时悄然变化。

**Blocked by:** 09 — 交付本地语义分类.

**Status:** resolved

- [x] 保存规则默认只影响新观测，既有云端分类不会自动变化。
- [x] WebUI 能显示预计设备、时间范围和可重分类限制，并要求 Owner 明确启动。
- [x] 设备只处理本地保留期内仍有必要上下文的事件，超出范围明确报告为不可恢复。
- [x] 重分类保持原事件标识并提交更高 revision，不创建重复时长。
- [x] 服务端使受影响时区和日期的派生摘要失效并重建。
- [x] WebUI 显示任务进度、成功、失败和不可恢复数量。
- [x] 审计记录包含 Owner、规则版本、时间范围和实际影响数量。

## Answer

契约先行：`openapi.yaml` 新增 **Reclassification** 标签与六个端点——Owner 管理面的 **`GET /api/v1/classification/reclassification/estimate`**（可选 from/to 的只读预估）、**`POST /api/v1/classification/reclassification/tasks`**（创建任务，409 保证同时只有一个 open 任务）、**`GET .../tasks/current`**（最新任务 + 进度，无任务 204）、**`POST .../tasks/{task_id}/close`**（幂等收尾并审计实际影响）；设备执行面（均 `rules:read` scope，与规则分发同一分发读权限）的 **`GET .../tasks/assignment`**（返回 open 任务，本设备已上报则 204）与 **`POST .../tasks/{task_id}/device-reports`**（同设备重复上报覆盖，204）。八个新 schema 全部 `additionalProperties: false`，四端派生模型重新生成，漂移门禁通过（quicktype 将原 `Status` 枚举改名为 `EventAcknowledgementStatus` 以避让新 `ReclassificationTaskStatus`，Windows/Android 既有引用同步更名）。

**任务生命周期（本票核心设计：服务端只协调，永不自行重分类）**：原始标题只存在于设备，服务端没有可重算的上下文，因此任务是一个协调对象——Owner 创建时冻结预估范围（estimate 快照存入任务文档）并指定目标规则集版本（默认当前已发布版本，> 已发布版本拒绝，因设备永远拉不到）；持有 `rules:read` 的设备轮询 assignment，先把本地缓存规则刷新到目标版本（刷不到就推迟本轮，绝不用旧规则重算），再对**本地仍保留**的候选段重跑本地引擎，把"解释变了"的事件经**既有批量协议**以更高修订提交，最后上报计数。任务范围在创建时冻结：之后新产生的观测本就由新规则经正常检查点流打标，不属于"历史"。

**范围定义（estimate 与 unrecoverable 共用同一过滤器）**：`activity.interval` 且 `finalization_state=final`、非 invalidated、`revision < 10^9`（保留段之外 = 人工修正，天然排除）、`data.is_afk != true`、start_at 在任务范围内。开放 checkpoint 不在范围内——它的每次延长都会以当前缓存规则重新上传（票据 09 的上传时分类语义），新规则自然作用于它的后续修订；定稿段才是"不会再被重传"的重分类对象。**不可恢复计数**由服务端逐设备计算：`max(0, 冻结范围数 − 该设备上报的 scanned)`——设备不再持有的原始上下文（超出本地保留期）被明确计入 unrecoverable 并在 WebUI 以警示展示，绝不静默跳过；负数方向钳位到零（任务创建后才定稿的段不虚增）。设个别别缺口（如本地段从未上传成功）由 rejected→failed 与 unknown→scanned 差额兜底呈现。

**人工修正必胜（关键交互 1）**：服务端范围过滤直接排除 `revision ≥ 10^9` 的修正事件（既不预估、也不要求设备重算）；设备若对创建后才被修正的事件提交重分类修订，既有三方比较照常回答 `stale_revision`——设备把它计为"无需变化"（yield，不是失败），不抬高本地 revision，并记录本次解释使后续轮次收敛。**设备修订空间（关键交互 2）**：重分类修订 = 本地 `sync_version + 1`，永远在保留线以下（批量入口 `revision_reserved` 防线不变）；接受后 `RecordReclassifiedAsync` 把本地 sync_version 推进到已确认修订，检查点流（开放段）因不参与重分类而无修订竞争。**无重复时长（关键交互 3）**：同一 event_id（`EventIds.ForSegment` 由设备+安装 GUID+段号确定性派生）+ 更高修订走原批量 upsert，集成测试证明重分类后 `GET /events` 仍只有一个逻辑事件、日报 device/active minutes 不变、被取代修订在 EventRevision 归档可查。

服务端（`server/src/modules/reclassification/`）：service 持有范围过滤器、estimate 聚合（`$group` by device_id 计数 + 最早/最晚 start_at）、任务 CRUD 与进度聚合；部分唯一索引 `{user_id, status:"open"}` 在存储层兜底单开任务不变量；审计三条——`reclassification.task_started`（Owner actor、目标规则版本、时间范围、预估设备/事件数）、`reclassification.device_reported`（设备 actor、四项计数）、`reclassification.task_closed`（Owner actor、目标规则版本、时间范围、报表时区、devices_reported/scanned/reclassified/unchanged/failed/unrecoverable 实际影响数）。**派生摘要重建（清单第 5 项）**沿用票据 08/11/15 的"计算即读取"——无汇总缓存可失效，重分类提交的更高修订在下一次读取即生效（测试证明日报与事件读取立即反映新修订），审计中的报表时区 + 任务时间范围即受影响日界的可复现陈述。

Windows（`LiveQs.Windows.Infrastructure/Sync/ReclassificationWorker.cs` 等）：`ReclassificationPlanner` 纯函数按"记录的上传结果 vs 当前规则重算"决断——相等→无需变化；不同→提交（包括规则删除后合法地剥除 subject）；**无记录（历史版本采集器上传的存量段）只加不删**：引擎新命中才提交、不再命中绝不剥除无法核实的 subject。`activity_segments` 新增 `upload_outcome` 列（PRAGMA 守护迁移），`CloudSyncClient` 在每次确认后记录上传解释，使后续任务的"无需变化"判断免于空转；候选查询只取**已定稿、非 AFK、不在 outbox** 的段（分页按段号推进）；5 分钟轮询、`CloudSyncEnabled` 闸门、批量 100 分批、目标版本不可达即推迟。Android 参照票据 12 的规划器语义评估后**本票不参与**（见 Comments），其生成模型仅随契约再生成。

WebUI：新增 **`/reclassification`「历史重分类」**页——影响预估卡（可选 UTC 范围 → 设备表：设备/平台/可重分类事件数/最早最晚观测 + 总数 + 固定的"可重分类限制"警示：仅定稿非 AFK 未修正未作废事件、超出设备保留期计入不可恢复、人工修正优先、当前由 Windows 采集器执行）；启动卡（目标规则集版本默认已发布版本、可选范围、popconfirm 二次确认的"明确启动"按钮）；进度卡（状态、目标版本、范围、六格计数：扫描/成功/无需变化/失败/不可恢复/已上报设备、逐设备上报表、unrecoverable>0 的显式警示、open 时可"结束任务并审计实际影响"、可手动刷新）。页首固定说明：保存规则只影响新观测，已上传事件不会自动变化。

测试（自动可测部分）：

- 服务端真实 HTTP + MongoDB 集成 15 条（`test/reclassification.test.ts`）：estimate 精确过滤（AFK/checkpoint/修正/作废全部排除 + 双设备分组与时间范围 + 反向区间 400 + 三类凭据 401）；**保存规则不触碰任何既有事件文档与归档（清单第 1 项，逐字节断言）**；任务创建（默认目标版本、冻结预估、审计字段）、非法目标/区间/双开 409、无任务 204；assignment 对 rules:read 设备可见、上报后对本设备隐藏他设备仍可见、缺 scope 403；设备上报聚合 + 服务端计算的 unrecoverable（冻结范围 − scanned）+ 重复上报覆盖 + 负数/未知任务/Owner 会话拒绝；close 幂等、审计实际影响数（含时区）、closed 后可再开任务、closed 后上报 409；**重分类提交保身份更高修订、单一逻辑事实、日报分钟数不变、归档可查（关键交互 3）**；**人工修正 stale_revision 胜出且被 estimate 排除（关键交互 1）**。
- Windows 11 条（`ReclassificationTests.cs`）：planner 决断矩阵（变更提交/相同跳过/存量只加不删/已知可剥除）；候选查询排除未定稿、AFK、outbox 内与范围外；`RecordReclassifiedAsync` 推进本地修订与记录；端到端（scripted HTTP）——同一 event_id + sync_version+1 + 新 subject + 原始标题不出现在线上 JSON + 上报计数 + 本地状态收敛 + 第二轮零提交；stale_revision 让步（计无需变化、不推本地修订、记录后收敛）；rejected 计失败且零本地写入；目标版本不可达推迟；无任务/同步禁用零流量。

验证（Mongo 经 `docker compose up -d`，全程未 `docker compose down`；测试全部使用独立 `live_qs_test_*` 库，未触碰 live 冒烟库与采集器）：`npm --prefix contracts run check`（lint + 6 条测试 + 四端模型同步）；`cd server && npm run typecheck && npm test && npm run build`（**168 条测试全部通过**，新增 15 条）；`cd web && npm run build`；`cd windows && dotnet test LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=artifacts/check/`（**58 条通过**，新增 11 条）；`cd android && ./gradlew.bat lint test assembleDebug`（**74 条 JVM 单测通过**，构建成功）。dev 服务（8787/5173）全程在线。

## Comments

- 设计决策（任务 = 设备执行的协调对象，而非服务端重算）：SPEC 实现决策 19 的"Devices re-evaluate only locally retained raw context"决定了服务端在物理上无法自行重分类——原始标题从未上传。备选是让设备把原始上下文传上来由服务端重算，这直接违反数据最小化（实现决策 15），一票否决。协调对象方案让"哪些事件可恢复"由设备上报的 scanned 与服务端冻结范围的差额显式回答，诚实且最小。
- 设计决策（范围冻结于创建时刻）：任务范围不随新观测漂移——创建后的观测本就由新规则经正常上传流打标，把它们算进任务是语义错误；冻结还让 unrecoverable 的分母确定、可复现。代价是任务创建后才定稿的"旧段"（如采集器重启补发的 final 修订）不计入本轮，留待下一个任务。
- 设计决策（重分类只处理已定稿段）：开放 checkpoint 的每次延长都会以当时缓存规则重新上传（票据 09 设计决策），新规则已天然作用于其后续修订；若重分类与其并行提交修订会与检查点流竞争同一修订号。定稿段不会再被正常流触碰（不在 outbox），`sync_version + 1` 无碰撞面。
- 设计决策（设备上传结果记录 + 存量"只加不删"）：判断"无需变化"需要知道云端当前解释，故新增 `upload_outcome` 列并在每次确认后记录。存量段无记录时云端解释不可知：引擎新命中即提交（增益）；不再命中绝不提交（剥除一个可能存在的 subject 属于无证据改写）。代价是存量段首个任务可能提交语义等价的"重申修订"，噪声以任务时间范围限定，第二轮起由记录归零。
- 设计决策（stale_revision 计入"无需变化"而非失败）：修订输给人工修正不是设备错误，而是"人的决定优先"的正常结果；计入失败会污化失败信号。设备同时不推进本地 revision、仅记录本次解释，保证绝不与人对抗且后续轮次收敛。
- 设计决策（Android 本票不参与重分类）：Android 用量观测只带包名，subject 映射虽确定，但其"原始上下文"（系统 UsageStats 事件日志）由操作系统持有且保留期短、不可靠查询任意历史窗口——"仅在本地保留内重算"的契约对 Android 无法诚实界定；且规划器对边界未变的已定稿会话按设计跳过重发。服务端范围按上报设备计算，Android 事件既不会虚增 unrecoverable 也不被改动；预估表按平台展示设备，Owner 可见覆盖面。若未来出现包名规则频繁变更的需求，需先解决系统保留期边界，属独立票据。
- 设计决策（estimate 为 GET 只读、任务创建冻结快照）：预估与创建分离让"看看影响面"零风险；创建时重新计算并存储自己的快照，任务进度与 unrecoverable 从快照推导，即使后续数据变化陈述仍然可复现。
- 评审修复：自审（Standards 轴）将 SyncModels.cs 的 using 从 namespace 之后移回文件顶部（与仓库风格一致），并删除服务端测试中一条与 `.expect(204)` 重复的同义断言；复检发现快照 `estimate.generated_at` 统一取任务创建时刻，避免"预估时间"与"冻结时间"两个口径。
- 已知边界与后续：任务收尾依赖 Owner 显式 close（设备全部上报后不自动关闭，长期离线设备永远"未上报"——这正是事实）；重分类审计不含逐事件清单（变更明细在 EventRevision 归档，专门的变更浏览 UI 属诊断票据）；`unrecoverable` 的分母以 start_at 归属、按定稿事件计，跨午夜区间不展开到报表日（日报归属由指标读取时的裁剪语义决定）；人工修正后设备侧的对应段会在未来每个任务里各试一次 stale_revision 后收敛（有记录后即跳过），量级可忽略；WebUI 进度为手动刷新，未做轮询。
