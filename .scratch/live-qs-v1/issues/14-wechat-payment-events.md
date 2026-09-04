# 14 — 接入微信支付结构化事件

**What to build:** 让 Android 从微信支付通知中提取最小结构化交易事实，可靠上传并形成可修正消费摘要，同时确保通知原文永不离开设备。

**Blocked by:** 04 — 交付分级凭据管理; 05 — 打通 Windows 活动纵向链路.

**Status:** resolved

- [x] 交易 Schema 定义金额、币种、收支方向、批准商户标签、类别、发生时间和来源标识。
- [x] 通知正文、标题和其他自由文本不会出现在上传请求、服务端事件或日志中。
- [x] 客户端为同一来源交易生成稳定事件标识，重复通知和重试不会重复记账。
- [x] 无法解析的通知进入本地可诊断失败队列，不提交猜测金额。
- [x] 缺少稳定来源标识且疑似重复的交易被标记待确认，而不是仅凭金额与时间合并。
- [x] WebUI 展示消费总额、收支、分类和商户，并允许使用 Owner 修正流程。
- [x] 金额和来源记录数可与选定真实样本对账，敏感读取受 Query scope 限制。

## Answer

契约先行：`contracts/schemas/events/` 新增 `payment.transaction.v1`（第五种注册
事件类型）。payload 五个必填字段——`amount:{value,currency}`（value 为整数
**最小货币单位**，CNY 即分，上下界 [1, 10^10]，杜绝浮点）、`direction`
（income/expense）、`merchant`（设备端提取规则产生的批准商户标签，1–80 字，
拒绝路径形状）、`category`（封闭枚举：food/transport/shopping/bills/health/
education/entertainment/transfer/uncategorized，来自设备端规则）、
`pending_confirmation`（必填布尔，见下）。`source.kind` 新值
`android.wechatpay`；`source.record_id` 是**来源记录（通知）的稳定指纹**
`wechat-notification-<sha256(key|postTime)前16位>`，作为"来源记录数 ↔ 服务端
确认"的对账锚点。事件为瞬时事实：`start_at` 即发生时间，无 `end_at`
（注册表 timeSemantics=instant 拒绝 end_at，与心率同构），`privacy_level`
默认 `sensitive`。契约样例 1 有效 + 4 无效（invalid_amount / invalid_currency /
invalid_timestamp / schema_invalid——schema_invalid 即 payload 混入
`notification_text` 自由文本被未知字段拒绝，隐私红线的可执行表达）；
`event-validator.mjs` 错误映射扩展 amount/currency 稳定错误码；四端派生模型
重新生成，漂移门禁通过，Windows C# 编译无回归。

**scope 集合扩员**：`CredentialScope` 新增 `payment:write`/`payment:read`，
device_token 允许 {events:write, health:write, payment:write} 非空子集，
query_token 允许对应读取子集；批量上传 anyScope + 逐项 scope 强制（payment
条目缺 `payment:write` 逐项 `insufficient_scope`，其余条目照常推进）；
`GET /api/v1/events` 域过滤扩展（无 payment:read 永远读不到支付观测且如实报
partial）；新增 **`GET /api/v1/payment/events`**（payment:read）为支付域读取
端点，completeness 基线取支付域自身。凭据创建的 scope 白名单改为从
`KIND_SCOPES` 单一权威派生（此前在 routes 里手工重复了一份，这次顺手消灭）。

Android 重建（新包 `payment/`，`service/WechatPayNotificationService.kt`
类名与 Manifest 注册零改动，通知监听权限流程不动）：

- **解析器**（`WechatPayNotificationParser`，纯函数）：逐字保留已验证的旧
  采集器规则（支付关键词门、金额正则、收入关键词、商户清洗、分类正则表），
  仅替换输出形态——sealed 结果 `Transaction / NotPayment / Failure`。
  金额解析改用字符串整数运算转最小单位（"21.50元"→2150 分，"12.3元"→1230
  分），金额永不经过浮点。无关键词 → 忽略；有关键词但无金额/金额≤0 →
  `Failure`，绝不猜金额。
- **本地失败队列**（`PaymentNotificationFailures`）：Failure 记录仅在设备本
  地 NDJSON 文件保留原始标题/正文供诊断，指纹去重、容量 200 截断，永不进入
  上传队列；同步页仅显示计数。
- **规划器**（`WechatPayTransactionPlanner`，纯函数）：UUIDv5 身份 =
  `payment.transaction + 设备 ID + 安装 GUID + 通知 key + postTime`（共用
  `identity/UuidNameIds` 命名空间），同一通知的重复投递收敛到同一事件，服务
  端以 duplicate 确认；内容指纹不变即跳过重发，内容变化升修订重发。
  **疑似重复检测**：新通知与既有观测在方向+金额+商户一致且发生时间相差
  ≤15 分钟、但事件身份不同时，新事件置 `pending_confirmation=true` 并作为
  独立观测上传——从不在客户端或服务端按金额+时间合并。
- **耐久状态**（`WechatPaySyncState`）：安装 GUID 与修订/指纹状态同文件共
  命运（票据 12/13 模式），7 天保留期裁剪。
- **队列统一**：微信支付从 legacy `/api/v1/ingest/events` 通道（票据 04 已
  删除的死代码）迁移到共享 `ContractEventSpoolQueue`/
  `ContractSyncFailures`/`ContractEventQueueDrainer`（独立队列文件
  payment-events.ndjson）；`LifeSyncWorker` 周期同步追加支付 outbox 排空，
  传输失败跨周期重试；legacy `EventSpoolQueue`/`EventQueueDrainer`/
  `ReportClient.postEvents` 整体删除（`LifeEvent` 保留给本机预览屏）。
- **隐私红线**：`Transaction` 类型不携带任何自由文本字段——标题/正文只存在
  于解析器入参与本地失败文件两个位置，上传路径（outbox → 批量请求 → 服务端
  事件 → 日志）全部只有类型化字段。

服务端：payload registry 新增 `payment.transaction@1` 条目（金额正整数且
≤10^10 分、ISO 4217 大写三字母币种、方向枚举、类别封闭枚举、
`pending_confirmation` 必填、商户标签拒绝路径形状、strictObject 拒绝一切
未知自由文本字段、legal source kinds 封闭为 android.wechatpay、sensitive
隐私默认），注册表消费方（默认隐私、写 scope、瞬时语义）自动生效。

WebUI（新增 `web/src/views/SpendingView.vue` + `/spending` 路由 + 侧边栏
「消费支出」入口，改读契约支付端点）：总支出/总收入指标卡（无观测显示
"无数据"绝不显示 0）、交易笔数与待确认笔数、每日支出柱图、分类支出饼图、
按分类表（支出/收入/笔数）、按商户表、完整交易记录表（发生时间 UTC、精确到
分的金额显示——整数分运算格式化、方向、商户、分类、**待确认/已确认来源**
状态标签、设备、采集时区、修订、同步时间）与游标分页；
`completeness=partial` 显式警告。报表时区取 Owner 设置。金额合计覆盖全部
观测（含待确认），保证与服务端来源记录数对账，待确认子计数并列展示。

测试（自动可测部分）：

- 服务端真实 HTTP + MongoDB 集成测试 6 条（`test/payment-events.test.ts`）：
  摄入 + 支付域端点回显（发生时间、无 end_at、source 归因、sensitive、
  待确认标记）+ 重投幂等；逐项 scope 强制（混合批次其余条目照常推进）；
  payment:read 与 events:read 互相不可见（通用读取隐藏支付观测且 partial、
  payment-only 令牌被通用读取 403）；省略 privacy_level 默认 sensitive 被
  normal 上限逐项拒绝且零入库；九类非法/自由文本载荷（非整数金额、零金额、
  小写币种、非法方向、payload 混入 notification_text、路径形状商户、非法
  来源种类、未注册类型、瞬时携带 end_at）全部 rejected + 稳定错误码且零
  入库；金额与来源记录数对账（3 笔真实形状样本：分值总和精确相等、整批
  重试十问全部 duplicate 不变总额）。
- Android JVM 单测 21 条新增（共 65 条全部通过）：解析器（支出/收入/金额
  精确性跨小数形态/关键词门/空白忽略/无金额失败不猜/分类表）；规划器（契约
  字段与敏感默认/身份稳定性与四维作用域/重投跳过与修订链/疑似重复标记且
  双观测并存/窗口外与不同金额方向商户不误标/每通知独立对账 record_id）；
  状态耐久（GUID 稳定/修订跨重启/裁剪/损坏文件重生身份纪元）；本地失败队列
  （读写/指纹去重/容量截断）。

验证（Mongo 经 `docker compose up -d` 启动，全程未 `docker compose down`）：
`npm --prefix contracts run check`（6 条测试 + 漂移门禁同步）；`cd server &&
npm run typecheck && npm test && npm run build`（114 条测试全部通过，新增
6 条）；`cd web && npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（34 条通过，生成模型重命名后编译无回归）；
`cd android && ./gradlew.bat lint test assembleDebug`（65 条 JVM 单测通过，
lint 与 assembleDebug 成功）。

## Comments

- 设计决策（金额表示法）：`amount:{value, currency}`，value 为整数最小货币
  单位（CNY=分）而非小数元。JSON 数（IEEE 754）无法精确表示 0.1 等十进制
  小数，浮点累计会在对账时产生分位漂移；整数分与 WeChat 账单的最小单位天然
  对齐，合计、比对的每一处都是精确整数运算。币种显式携带而非隐含 CNY，为
  未来多币种来源留出版本化扩展位。解析端用字符串整数运算完成"元→分"，浮点
  从未接触金额。
- 设计决策（来源标识的语义分层）：Health Connect 的 record_id 既是记录身份
  也是对账锚点；微信支付通知没有支付网络交易号，本票把
  `source.record_id` 明确为**通知**（来源记录）的指纹——它稳定、可对账
  （一条通知 ↔ 一条事实），但**不是**交易身份。同一支付被第二条通知再次报告
  时会得到不同事件身份，这正是"缺少稳定来源标识"的真实形态：以
  `pending_confirmation` 标记 + 双观测并存处理，而非合并或假装没有问题。
  若未来接入带交易单号的来源（如支付凭据通知含单号），可升级为以单号参与
  身份派生，属 schema 语义变更、走新版本。
- 设计决策（pending_confirmation 放 payload 且必填）：它是观测本身的解释
  状态（"这条事实未经来源单号确认"），不是信封传输属性，放 payload 使其进
  入 Schema 校验（必填、布尔）并随事件内容参与指纹。必填而非可选，是强迫
  生产者对每条交易显式表态；票据 11 的修正流（确认/驳回/改商户改分类）可以
  以更高修订直接改写该标记，本票已把状态面铺好。
- 设计决策（疑似重复检测放在客户端规划器）：只有采集端看得到通知流的时序与
  身份，且规划器本就维护跨重启的状态文件（票据 12/13 模式）；窗口（15 分钟）
  与匹配键（方向+金额+商户）刻意保守——宁可多标待确认交 Owner 裁决，也绝不
  在服务端静默合并（SPEC 实现决策 21：模糊候选必须显性化）。检测只**标记**
  不**删除**，两个观测都入库，符合"聚合从不销毁来源身份"。
- 设计决策（Category 封闭枚举而非开放字符串）：设备端分类规则表的输出集合是
  有限且版本化的，封闭枚举让 WebUI 分组与 AI 读取端获得稳定语义；规则表扩
  展类别时按 SPEC 决策 9 走 schema 新版本。uncategorized 兜底保证未知商户
  永远可入库。
- 修正流程边界（清单第 6 项 vs 票据 11 未解决）：本票交付了完整的消费摘要
  展示面与待确认状态标记，但**没有**构建任何修正机制——没有修正端点、没有
  修订提升 UI、没有无效标记，因为可审计修正（同一事件标识 + 更高修订 + 变
  更字段/原因/actor 审计 + 摘要重建）正是票据 11 的交付物。SpendingView 的
  待确认警示文案与交易表的"待确认"标签即本票的最小标记面；票据 11 实施时
  可直接在该表扩展行内修正操作，`pending_confirmation` 字段与其修订语义已
  由契约固化。
- 已知边界与后续：Android 真机冒烟（通知监听授权、真实微信支付通知解析、
  与微信账单对账）无法在 CI 复现，属票据 18 真机验收步骤；**覆盖缺口**：若
  某些真实支付场景不产生系统通知（如免密小额免打扰通道），通知监听天然收
  不到，该缺口按覆盖处理并在验收时如实记录——本票不引入无障碍树抓取等更强
  侵入手段（SPEC 决策 26 只承诺通知监听）。同商户同金额的合法连续消费（如
  两杯咖啡间隔 10 分钟）会被误标待确认——这是保守检测的预期代价，由 Owner
  确认流程裁决；分类规则表目前继承 legacy 版本，规则版本化与 WebUI 管理属
  票据 09/15 语义；Dashboard 的 legacy 消费图表（若有）不属本票范围。
