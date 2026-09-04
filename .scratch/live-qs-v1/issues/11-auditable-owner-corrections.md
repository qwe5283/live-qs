# 11 — 交付可审计人工修正

**What to build:** 让 Owner 修正自动提取或分类结果、标记误报并立即获得正确摘要，同时完整保留采集来源和修改历史。

**Blocked by:** 03 — 交付局域网 Owner 登录; 05 — 打通 Windows 活动纵向链路.

**Status:** resolved

- [x] WebUI 允许对契约批准的结构化字段进行修正，并可记录可选原因。
- [x] 修正使用同一事件标识和更高 revision，保留原始结构化观测及来源。
- [x] Owner 能将误报标记为无效，使其不再进入时间线默认视图和统计。
- [x] 默认查询返回最新有效解释，并标明自动或人工、是否 corrected。
- [x] 关键金额、时间或分类修正会重建受影响的派生摘要。
- [x] 审计记录显示 actor、时间、变更字段和原因，但不包含凭据或禁止上传的原始文本。
- [x] 未认证、Device Token 和 Query Token 均无法执行修正。

## Answer

契约先行：`openapi.yaml` 新增 **`POST /api/v1/events/{event_id}/corrections`**（仅
Owner Session；新增 path 参数 `EventId`）。请求 `EventCorrectionRequest` =
`fields`（`CorrectionField { path, value }` 数组，可省略）+ 可选 `reason`
（≤500 字，可 null）+ 可选 `invalidate` 布尔；响应 `EventCorrectionResult` =
修正后的完整契约事件信封 + `changed_fields` + `reason` + `invalidated` +
`corrected_at` + `revision` + `impact`（`CorrectionImpact { metric, timezone,
affected_ranges, result_count }`，沿用票据 15 的"影响记录"精神）。事件信封
Schema（`event-envelope.v1.schema.json`）新增**可选** `correction
{ corrected_at, reason }`——存在即表示最新有效修订是人工解释，缺席即设备自动
解释，读取端由此标明自动/人工与是否 corrected（story 42、实现决策 20）。
四端派生模型重新生成，漂移门禁通过。

**修正与设备修订流的共存机制（本票核心设计，保留高段修订空间）**：修正分配
`newRevision = max(storedRevision, CORRECTION_REVISION_BASE) + 1`，基线
`CORRECTION_REVISION_BASE = 10^9`。设备检查点流（sync_version 每次 +1）永远在
低位空间，因此修正一旦落库，该事件任何后续设备重传在**既有**三方比较下必然
数值更低 → 照常回答 `stale_revision`——"较低或相等修订不能替换更高修订"
（SPEC 实现决策 12）无需任何例外，批量幂等与 stale_revision 语义零改动；作废
同样落位高位，设备重传无法复活误报。批量入口新增防线：设备修订 ≥ 10^9 一律
`rejected` + 稳定码 `revision_reserved`（保留空间真正保留，顺带封住越界整数
修订）。Windows outbox 本就把 stale_revision 视为对应修订已确认（票据 05），
采集器无重试风暴。

服务端（`server/src/modules/corrections/`）：

- **字段治理**：`payload-registry.ts` 新增 `CORRECTABLE_PATHS`——每注册类型
  只允许契约批准的结构化解释字段（叶路径）：支付 = amount.value /
  amount.currency / direction / merchant / category / pending_confirmation /
  start_at；活动 = application_label / subject_id / is_afk / start_at / end_at；
  心率 = beats_per_minute / start_at；步数 = count.value / start_at / end_at；
  睡眠 = start_at / end_at。身份、来源、设备、provenance、privacy、
  finalization 一律不可修正；自由文本在任何类型的可修正集合中都不存在。
  修正把叶路径合并进原 payload 后**重新过注册表 zod 校验 + 区间语义校验**
  （金额非整数分、路径形商户、未知类别、瞬时带 end_at 等全部 400）；声明
  duration 一律由修正后区间边界重新推导，永不手填。路径不在集合 → 400
  `field_not_correctable`；合并结果不合法 → 400 `invalid_correction`；无字段
  且无作废变化的空请求 → 400 `invalid_correction`（不烧审计修订）。
- **原子替换与归档**：与 ingest 同型——先幂等归档被取代快照进
  `EventRevision`（`{event_id}:{storedRevision}`），再
  `updateOne({id, revision: 已比较修订}, $set)` 条件替换；输给竞态则重读重算
  （上限 5 轮）。同一 event_id 连续修正分配 BASE+1、BASE+2…，旧修订全部可查，
  事实从不销毁。
- **读取语义**：`listEvents`（通用 + 健康 + 支付域读取）与域 enrichment
  （冲突检测、`pending_confirmation_count`）统一排除 `invalidated`——误报退出
  默认时间线与统计（OpenAPI 本就承诺 "latest, non-invalidated event
  revisions"，此次补齐实现）；使用指标/来源策略选择此前已排除，行为对齐。
- **派生摘要重建**：延续"计算即读取"——金额/时间/分类修正后下一次读取即按
  最新有效修订输出，测试用真实 HTTP 证明修正 end_at 后日报 device_minutes
  60→120。响应与审计携带影响记录：受影响指标键（usage.app_minutes /
  health.* / payment.transaction_totals）+ Owner 报表时区下新旧触及日报日之
  并集 + result_count。
- **审计**：`event.corrected` 记录 actor（user）、时间、event_id、
  from/to_revision、`changed_fields`（仅点路径，不含值）、reason、invalidated、
  timezone、metrics_changed、affected_ranges、result_count——不含任何凭据
  材料与原始文本；完整新旧快照本就在 EventRevision 归档中，审计无需重复值。
- **鉴权**：路由仅挂 `ownerAuth()`（cookie 会话），Bearer 凭据根本不参与——
  未认证、Device Token、Query Token 一律 401，集成测试逐一断言且零审计落库。

WebUI：新增共享组件 `EventCorrectionModal`（按事件类型渲染且仅渲染上述可修正
字段：文本/整数/金额（元字符串 ↔ 整数分精确转换，浮点永不接触金额）/布尔/
下拉/datetime-local，作废开关与可选原因同框提交）+ `api/corrections.ts`
（可修正字段描述与服务端注册表同构镜像）。三个域视图接入：消费页交易表新增
「解释」（人工修正/自动提取）与「操作」列——修正、待确认行一键「确认」（以
更高修订把 `pending_confirmation` 置 false，票据 14 铺好的状态面就此贯通，
驳回误报走修正框作废开关）；使用页活动区间表、健康页观测时间线同获修正入口
与解释徽标；修正成功后重取当前范围，页面摘要即时反映最新有效修订（影响天数
以 toast 汇报）。作废行直接从默认列表消失（清单第 3 项的语义本身）。

验证（Mongo 经 `docker compose up -d`，全程未 `docker compose down`；测试全部
使用独立 `live_qs_test_*` 库，未触碰 live 冒烟数据）：
`npm --prefix contracts run check`（lint + 6 条测试 + 四端模型同步）；
`cd server && npm run typecheck && npm test && npm run build`（145 条测试全部
通过，新增 9 条真实 HTTP + MongoDB 集成：结构化修正与原因、修订归档与来源
保留、设备高修订 stale_revision 不覆盖人工解释、revision_reserved 防线、
作废退出时间线与统计且保留归档、待确认一键确认计数归零、时间修正重建日报
并回报影响日、五类非法修正稳定拒绝、三类凭据 401、404 与连续修正修订链、
作废恢复仅经可审计修订）；`cd web && npm run build`；`cd windows && dotnet
test LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（34 条通过，生成模型重生成后编译无回归）；
`cd android && ./gradlew.bat lint test assembleDebug`（生成模型重生成，构建
成功）。

## Comments

- 设计决策（修正修订走保留高段而非"修正后拒收设备修订"标志位）：备选有二
  ——修正后给事件打人工标志、设备修订无条件 stale；或比较时加入 owner 修正
  计数器。两者都给三方比较开例外，且"修订更高却被拒"与 SPEC 决策 12 的字面
  不变式冲突。保留高段（修正修订 ≥ 10^9）让"人工解释"就是字面意义上更高的
  修订：幂等（同修订重投 duplicate）、陈旧（低修订 stale_revision）、覆盖
  （高修订原子替换）三条既有规则原封不动地解释全部行为，1 亿量级的检查点
  计数余量对单段活动毫无现实意义，批量入口再以 `revision_reserved` 拒收越界
  修订守住边界。代价是修订号空间出现人为断层，读取端本就只展示修订号本身，
  不受影响。
- 设计决策（可修正字段 = 注册表治理的叶路径，而非整 payload 替换）：叶路径
  （如 payload.amount.value）让"改金额保币种"成为最小修正，未触及字段的
  采集原值天然保留；合并后整体重过 zod strictObject，未知字段/自由文本即便
  从管理面进来也无处藏身。duration 与 count.unit 是派生/单位常量，不在可修正
  集合——时长修正走区间边界，由服务端重推导，杜绝"边界与声明时长不一致"的
  事件进入存储。
- 设计决策（correction 放信封而非 payload）：自动/人工是修订的解释来源属性，
  对全部五种事件类型同构；放 payload 则每个 schema 都要加可选字段且与"采集
  内容"混淆。可选对象（缺席 = 自动）让存量事件无需迁移即可读，设备从不产生
  该字段（服务端 ingest 不复制它，只有修正服务写该列），被攻陷设备也无法
  伪造人工 provenance。
- 设计决策（审计只记点路径不记值）：清单要求审计展示"变更字段与原因"；新旧
  完整快照已在 EventRevision 归档（票据 05 的"事实不销毁"），审计层重复存值
  只会扩大敏感面（金额虽非禁传文本，但审计表与共享 sanitize 规则的交集应
  越小越好）。changed_fields 用点路径恰好绕开 sanitizeAuditDetails 的
  payload/raw 类键名脱敏，审计记录原样可读。
- 设计决策（作废可逆，但只能由 Owner 经修订逆转）：`invalidate` 缺席 = 维持
  现状，true = 作废，false = 复核后恢复——恢复本身也是一次带审计的高位修订，
  设备重传在两种状态下都只能 stale_revision。清单只要求"标记无效不立即擦除"，
  可逆性让误标有出路且全程留痕。
- 设计决策（pending_confirmation 的确认/驳回落位）：确认 = 一键以更高修订置
  false（附固定原因"确认非重复观测"入审计）；驳回 = 修正框内作废开关（疑似
  重复即误报）。票据 14 Comments 预告的"修正流可直接改写该标记"就此兑现，
  两观测并存的裁决权完整回到 Owner。
- 已知边界与后续：作废观测退出默认视图后暂无专用"回收站"界面（经
  `event.corrected` 审计与 EventRevision 归档可查，检索 UI 留给诊断票据）；
  capture_timezone / capture_offset / privacy_level 暂不可修正（清单未要求，
  最小集合优先）；对开放检查点（无 end_at）UI 不提供补设 end_at（服务端支持，
  属"采集器中途死亡"场景的罕见修补）；未注册类型的 legacy 行一律不可修正
  （注册表外无契约可依）；修正 UI 的时间录入用浏览器本地时区（提交为 UTC
  瞬时，即时正确性不受录入时区影响，与报表时区无关）；修正端点未设 429 速率
  限制（与全站一致，公网化时统一补齐，见 SPEC Out of Scope）。
