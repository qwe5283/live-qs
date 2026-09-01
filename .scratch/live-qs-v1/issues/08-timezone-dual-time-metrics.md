# 08 — 交付时区与双时间指标

**What to build:** 让 Owner 以稳定报表时区查看活动，并明确区分所有设备相加的使用时间与跨设备去重后的实际活跃覆盖时间。

**Blocked by:** 05 — 打通 Windows 活动纵向链路.

**Status:** resolved

- [x] 事件事实以 UTC 保存，同时保留采集 IANA 时区和当时 offset。
- [x] Owner 能配置默认报表时区，WebUI 不因浏览器所在时区改变同一日报。
- [x] 日与周查询按请求或 Owner 默认时区划分，缓存身份包含时区。
- [x] `device_minutes` 对符合条件的设备区间求和，允许超过自然经过时间。
- [x] `active_minutes` 对非 AFK 区间求并集，重叠时间只计算一次。
- [x] 时间线保留并发设备轨道，摘要和 API 明确标注两种指标。
- [x] 自动化测试覆盖跨午夜、重叠、相邻、空区间和夏令时边界。

## Answer

契约先行：`openapi.yaml` 新增 Metrics 组与 Owner 设置端点，四端派生模型
重新生成，`contracts check` 保持同步。

- **`GET /api/v1/metrics/usage/day` / `/week`**（Owner Session 或
  Query Token，`events:read`）：`UsageMetrics` 显式标注 `device_minutes`
  （各设备区间求和，含 AFK 前台时间，可超过自然经过时间）与
  `active_minutes`（非 AFK 区间并集，重叠/相邻只计一次）。日报附分设备
  轨道（`device_id`/platform/两种分钟数），周报附七日按日分解；响应复用
  `QueryContext` 暴露实际 UTC 范围、解析出的时区、provenance 与
  completeness，满足 AI 读取"范围/时区/来源/完整性自述"的要求。
- **`GET/POST /api/v1/owner/settings`**（仅 OwnerSession）：持久化
  `report_timezone`（OwnerSettings 集合），服务端以 Intl 校验 IANA 时区，
  非法值 400 `invalid_timezone` 且原值不变；变更写入审计
  （`owner.settings.update`）。

服务端（`server/src/modules/metrics/`）：

- 纯函数 `interval-metrics.ts`：区间先按报表范围裁剪（clip），再求和
  （设备时间）或排序后合并相邻/重叠区间（活跃时间）；空输入为 0。
- 指标按**区间与报表范围的重叠**计算：跨午夜/跨周边界区间两侧各计入
  日内部分（`start_at < to && end_at > from` 的覆盖查询，走
  `user_id+start_at+end_at` 索引），排除 `invalidated` 事实；无 `end_at`
  的开放 checkpoint 时长未知，不计入。
- 日/周边界：`zonedDayRange` 复用，新增 `zonedWeekRange`（周一为一周
  起点，星期由日历日期本身决定，边界逐个午夜解析，天然覆盖 DST）；
  时区 = 请求 `timezone` 参数（覆盖）否则 Owner 默认报表时区。
- **计算即读取、不设汇总缓存**：时区是每次计算的输入，不存在可被时区
  污染的缓存身份；旧 `DailyRollup` 是遗留报表工件，未在其上构建。
- Query Token 聚合读取遵循凭据 privacy ceiling 与 allowed_event_types
  （与 `GET /api/v1/events` 同一套读取许可，逻辑抽为
  `privacyLevelsForRead`/`readableEventTypes` 共用）；数据被扣减时
  completeness=partial。聚合不含任何原始内容。Device Token 只有
  events:write，读取 403。

WebUI：

- 使用页新增日报/周报契约指标面板，全部取自 Owner 报表时区解析的
  服务端结果——浏览器时区不再影响同一份报告；默认报表日也按报表
  时区计算（`todayInTimezone`）。指标卡片与周报表显式标注
  「设备时间 / 活跃时间」两种口径及含义注释。
- 时间线新增并发设备轨道：每个设备一条轨道，区间按报表日内位置
  画条（AFK 灰条、活跃蓝条），轨道旁标注该设备两种分钟数；活动区间
  表展示 UTC 时刻与采集时区上下文（capture_timezone + offset）。
- 设置页新增报表时区配置（IANA 时区可搜索下拉，服务端持久化）。

验证（Mongo 经 `docker compose up -d`，结束后 `docker compose down`）：
`npm --prefix contracts run check`；`cd server && npm run typecheck &&
npm test && npm run build`（88 条测试全部通过，新增 25 条：区间并集/
裁剪纯函数、周范围/时区校验、Owner 设置端点、真实 HTTP + MongoDB 的
空区间/重叠/相邻/跨午夜/夏令时（America/New_York 23h/25h 日与跨 DST
周）/Owner 默认时区与请求覆盖/凭据限制与 partial 用例）；`cd web &&
npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（29 条通过）`&& dotnet build
LiveQs.Windows/LiveQs.Windows.csproj -o artifacts/verify`（重生成契约
模型后确认 C# 编译无回归）。

## Comments

- 设计决策（device_minutes 的"qualifying"读法）：CONTEXT.md 词汇表只在
  Active minutes 定义中出现"non-AFK"限定词，Device minutes 无此限定；
  结合 story 15"sum every device's activity 测量总设备消耗"，取
  **设备时间包含 AFK 前台时间**的读法（AFK 仍是设备开机占用的前台
  区间），活跃时间仅并集非 AFK 区间。该读法与"允许超过自然经过时间"
  一致，已在契约字段描述中写明。
- 设计决策（指标按区间与范围的重叠计算而非按 start_at 归属）：按
  start_at 归属会让同一长区间在跨日/跨周报告中重复计入或整体缺失，
  日报无法加总成周报；裁剪到范围后日报分解与周报总量一致，且设备
  时间仍可超过自然经过时间（并发设备各自贡献）。区间跨边界两侧各计
  入日部分。
- 设计决策（报表时区默认 UTC）：SPEC 未指定默认时区，取确定性的
  UTC，不引入隐式本地化假设；Owner 在设置页显式配置后，日/周边界
  才按其时区解析。未配置时 WebUI 与 API 行为完全一致（UTC），不会因
  浏览器位置漂移。
- 设计决策（周边界 = 报表时区内周一 00:00 起的七个本地日）：SPEC/
  CONTEXT.md 对周起点沉默，取 ISO 惯例周一为起点；星期几由日历日期
  决定与时区无关，周起止经 zonedDayRange 逐午夜解析，跨 DST 周为
  167±1 小时。
- 设计决策（无缓存/汇总表）："缓存身份包含时区"由"根本不缓存"满足：
  计算即读取（覆盖查询走既有索引），更换报表时区后下一次读取即按新
  日历输出，story 48"换时区重建派生汇总而无需改写事件"自然成立。
  若未来引入 rollup 持久化，键必须含时区并随设置变更失效（票据 15
  来源策略 / 11 修正重建时再评估）。
- 设计决策（聚合端点鉴权 = Owner Session 或 Query Token events:read）：
  SPEC AI 边界允许只读结构化查询与汇总接口；聚合对 Query Token 应用
  与原始事件读取完全相同的 privacy ceiling / allowed_event_types 约束，
  被扣减时 completeness=partial，凭据不会从聚合数反推出其不可读的
  数据。Device Token 仍仅可上传。
- 评审修复：自审（Standards 轴）发现 zonedWeekRange 重构中周末边界
  一度取"最后一日的午夜"而非"次日凌晨"（周长少 24h），由跨 DST 周
  测试当场拦截并修复；同时抽出 summarizeUsage/provenanceOf 消除三处
  重复。
- 已知限制与后续：分钟数为毫秒总和四舍五入到整分，亚分钟区间下
  各日之和与周总量可差 1 分钟（每层独立取整）；指标仅覆盖已注册的
  `activity.interval.v1`（V1 唯一事件类型），健康/消费指标待票据 13/14
  后接入同一骨架；legacy `/api/v1/usage/*`、`/reports/*` 路由与
  `DailyRollup` 未动（非契约遗留，后续票据清理）；本机采集器常驻，
  windows 检查沿用 `-p:BaseOutputPath=artifacts/check/` 与
  `-o artifacts/verify` 绕开 MSB3027（见票据 05 Comments）。
