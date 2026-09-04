# 12 — 接入 Android UsageStats

**What to build:** 让 Android 将系统 UsageStats 作为每日应用使用的权威来源可靠上传，并在跨设备时间线与摘要中展示而不重复计算无障碍实时状态。

**Blocked by:** 05 — 打通 Windows 活动纵向链路; 08 — 交付时区与双时间指标.

**Status:** resolved

- [x] Android 使用事件 Schema 定义应用包名、区间或日汇总、单位、来源和时区语义。
- [x] 现有 UsageStats 权限与采集逻辑继续使用，并迁移到稳定事件标识、revision 和 durable outbox。
- [x] 断网、重复同步和进程重启不会丢失或重复每日应用事实。
- [x] 规范化策略以 UsageStats 贡献 Android 日应用时长，无障碍观察只贡献实时或上下文信息。
- [x] WebUI 将 Android 与 Windows 放在同一时间体系中，同时保留设备来源。
- [x] private 事件在客户端阻止上传，服务端拒绝不获准类型或隐私级别。
- [x] 与 Android 系统统计对照的每日总量偏差目标不超过百分之五，并记录测量方法。

## Answer

契约先行：`activity.interval.v1` 的 `SourceKind` 新增 `android.usagestats`
（复用既有事件类型，不新增 event_type，凭据 allowed_event_types 机制零改动），
枚举描述写明来源语义——UsageStats 是 Android 日应用总量的权威来源，无障碍
观察只支持当前与上下文活动。新增 1 条有效样例（Bilibili 前台会话的定稿修订，
含包名、区间边界、ms 单位、来源与采集时区/offset）与 1 条未知来源种类无效
样例（`schema_invalid`），四端派生模型重新生成，漂移门禁通过，Windows C#
编译无回归。

**区间重建而非日汇总**：SPEC 实现决策 14 要求"客户端生成历史活动区间"，
实现决策 21 指定 UsageStats 为日总量权威。选择把 `UsageStatsManager` 的
ACTIVITY_RESUMED/PAUSED 事件流配对成每包前台会话（区间），而非上传日汇总：
区间直接流入既有的源无关读取路径（时间线设备轨道、device_minutes/
active_minutes、事件表），日总量由服务端按报表时区裁剪汇总（票据 08 骨架
天然支持跨午夜区间两侧各计入），且修订语义（开放会话检查点→定稿）与
Windows 的段检查点模式完全同构。

Android 重建（`android/app/src/main/java/com/ailife/android/usage/`），平台
权限不动：

- **会话配对纯函数**（`UsageStatsIntervals`）：沿用旧采集器的配对语义——
  换包时同刻交棒（时长守恒）、孤儿 PAUSE 忽略（其会话起点早于查询窗口，
  属于更早一轮已上报）、同包重复 RESUMED 忽略、窗口末仍前台保持开放。
- **稳定事件标识**（`UsageEventIds`）：RFC 4122 v5，输入为
  `设备 ID + 安装 GUID + 包名 + 会话起点毫秒`，与 Windows 采集器共用同一
  命名空间 GUID，已知向量测试双向钉死（Windows EventIdTests 的
  Python uuid5 向量）。重装/清数据后安装 GUID 随 revision 状态文件一起
  重生（同文件共命运），身份纪元更新，绝不与云端历史碰撞。
- **修订语义**（`UsageStatsEventPlanner`，纯函数）：新会话 revision 1
  （checkpoint，end_at = 观测时刻）；开放会话每轮延长 revision +1
  （checkpoint 流式）；PAUSED 到达 revision +1 定稿（final + 精确边界）；
  边界未变的定稿会话跳过重发；罕见边界修正以更高修订替换。每轮从
  `min(48h 回看, 上轮水位 - 1h, 最老开放会话起点)` 重建，断网任意久后
  一次同步即可回填全部缺口，采集器进程死亡不丢事件（事件由系统写入，
  不依赖采集器存活）。
- **durable outbox**（`UsageEventSpoolQueue`）：NDJSON 文件，按 event_id
  upsert（离线重算后只保留最新待发修订），进程重启存活； drained 时
  `POST /api/v1/events/batch` 逐项确认——accepted/duplicate/stale_revision
  视为对应修订已确认移除出队，rejected 进入本地可见失败队列
  （`UsageSyncFailures`，同步页显示计数）且永不重试，传输失败/响应形状
  不符整批保留。private 观测在进入上传队列前被丢弃（`privacyLevelOf`
  接缝，V1 使用观测全部 normal；契约信封本就不可表达 private）。
- **接线**：`LifeSyncWorker` 中 UsageStats 从 legacy `/api/v1/ingest/events`
  通道（票据 04 已删除）迁移到契约通道；Health Connect 收集与旧队列原样
  保留（票据 13 处理）；心跳（无障碍实时状态）通道零改动。设置页新增
  Owner ID（默认 `local`，与服务端 DEFAULT_USER_ID 一致，事件 owner_id
  必须匹配凭据属主），同步页/状态页展示版本化队列深度、永久失败数。

服务端：payload registry 新增合法来源种类校验（SPEC 实现决策 9"注册
schema 定义 legal sources"），未知 `source.kind` 逐项拒绝
`invalid_event`。其余复用票据 05 的修订/幂等/授权语义。

WebUI：设备轨道时间线本就按 `device.platform` 标注来源；活动区间表设备列
补附平台标识（`android · <device_id>`）。日报/周报按 Owner 报表时区解析，
Android 与 Windows 区间在同一时间体系下聚合，`context.provenance` 保留
`android.usagestats` 来源。

测试（自动可测部分）：

- Android JVM 单测 32 条：UUIDv5 已知向量与身份稳定性；会话配对守恒
  （换包/孤儿暂停/开放会话/零长会话）；规划器修订链（开放→延长→定稿
  全程同一 event_id、revision 1→2→3，未变定稿跳过，边界修正升修订，
  private 丢弃且不留状态，sensitive 保持契约可表达）；状态文件耐久
  （GUID 稳定、清库重生、revision 跨重启、裁剪只丢超保留期定稿）；
  outbox 耐久与按事件 upsert、损坏行容错；drainer 六种确认语义
  （accepted/duplicate/stale 移除、rejected 入失败队列且二次同步零上传、
  传输失败保留、响应数量不符保留）。
- 服务端真实 HTTP + MongoDB 集成测试 4 条（`test/android-usage-events.test.ts`）：
  android.usagestats 检查点/定稿修订链与读取回显（source/device/平台/
  provenance）；重投十次一个逻辑事实 + 旧修订不可回滚；日指标
  device_minutes/active_minutes 恰等于使用区间和（35+20=55 分钟）而心跳
  （无障碍实时状态）零贡献、零事件化；未知来源种类/隐私超限/未注册类型
  逐项拒绝。

±5% 测量方法（清单第 7 项）：

- **自动可测不变量**（已入测试）：(a) 会话配对守恒——同一窗口内包切换
  交棒时长精确守恒，不丢不重（JVM 单测）；(b) 读取链路保真——日指标恰
  等于事件区间之和，开放检查点以"最新修订"被替换而非叠加，重投与陈旧
  修订不改变总量（服务端集成测试）；(c) payload 深度校验强制
  duration == end_at − start_at（±1ms）。
- **真机对照法**（冒烟/验收步骤，自动化边界之外）：同一报告日，取 WebUI
  使用页该 Android 设备轨道的设备分钟数（及应用级事件表），与系统参考
  对照——设置 > 数字健康/屏幕时间，或 `adb shell dumpsys usagestats`
  的当日每包前台时长。判定阈值 5%。
- **已知的、可解释的偏差来源**：查询时刻仍前台的会话以观测时刻截断为
  检查点（下一次同步定稿补齐，截断误差至多一个同步节奏）；系统屏显
  分钟级取整；OEM 聚合缓存与事件重放的微小出入。RESUMED/PAUSED 事件由
  系统自身写入，这正是权威性所在——旧实现（采集器自己开区间）在进程
  死亡时会永久丢会话，新实现下一轮重建即定稿。

验证（Mongo 经 `docker compose up -d` 启动，全程未 `docker compose down`）：
`npm --prefix contracts run check`；`cd server && npm run typecheck &&
npm test && npm run build`（101 条测试全部通过，新增 4 条）；`cd web &&
npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（34 条通过，重生成 C# 契约模型后编译
无回归）；`cd android && ./gradlew.bat lint test assembleDebug`（32 条
JVM 单测通过）。

## Comments

- 设计决策（区间重建而非日汇总，见 Answer）：日汇总形状无法进入既有
  时间线/指标读取路径（metrics 只消费 `activity.interval.v1` 区间），需要
  新建汇总聚合面；且 SPEC 把"历史区间由客户端生成"定为模型，UsageStats
  事件流本就是区间事件流，日汇总反而是有损投影。日总量语义由服务端裁剪
  汇总承担，源优先级的显式版本化策略对象属票据 15（届时以
  source.kind=android.usagestats 为 Android 日应用总量的权威输入，本票
  已把该语义写进契约枚举描述与 provenance）。
- 设计决策（revision 状态与安装 GUID 共文件）：若 revision 状态丢失而
  GUID 存活，重建出的 revision 1 定稿会被服务端判 stale_revision，
  开放会话的最终定稿永远落库失败；两态同文件共命运后，状态丢失 ⇒ GUID
  重生 ⇒ 全新身份纪元，宁可重传也不冲突。GUID 在首次构造即落盘，崩溃
  于首次规划前也不会分叉身份。
- 设计决策（outbox 按事件 upsert）：断网期间每 15 分钟一次的检查点递增
  会在队列里堆积同逻辑事件的多个修订；upsert 保证只发最新修订，恢复后
  一批即可追平。rejected 移出 outbox 进入失败队列（死信保留稳定错误码
  与消息），避免永久失败无限重试（对齐 Windows 的 permanent 语义与
  SPEC 实现决策 11"永久校验失败进入可见本地失败队列"）。
- 设计决策（心跳零改动即清单第 4 项的另一半）：无障碍观察的产物只有
  心跳投影（票据 07），从不生成历史事件；服务端集成测试直接断言心跳后
  事件数为 0、日指标 provenance 不含 android.accessibility，"不重复
  计算"由结构保证而非合并逻辑。
- 设计决策（源种类服务端校验）：契约 SourceKind 是封闭枚举，服务端
  ingest 此前只校验非空；补上合法值校验使存储的事件永远能回读为契约
  合法信封（与 unknown_event_type/unknown_schema_version 同一防御层）。
- 已知边界与后续：Android 真机冒烟（Usage Access 授权流、真实上传、
  WebUI 可见、与系统统计 ±5% 对照）无法在 CI 复现，属真机验收步骤；
  Health Connect 与 legacy NDJSON 队列仍走旧通道（票据 13），三份 spool
  的文件机制届时统一；每轮 48h 重建依赖 UsageStats 系统保留窗口，安装前
  的历史天然不可得（SPEC 决策 14"限于本地保留窗口"）；开放会话检查点
  在采集器长期死亡时保留最后一个 checkpoint，恢复后下一轮补发定稿；
  WorkManager 单轮最多发 500 条，首次回填超出的部分顺延到下一轮。
