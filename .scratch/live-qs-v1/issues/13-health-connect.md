# 13 — 接入 Health Connect

**What to build:** 让 Owner 在 WebUI 查看可追溯的步数、心率和来源睡眠区间，并清楚知道数据来自哪个 Health Connect origin 以及哪些时间范围缺失。

**Blocked by:** 04 — 交付分级凭据管理; 05 — 打通 Windows 活动纵向链路.

**Status:** resolved

- [x] 步数、心率和睡眠分别使用版本化 Schema，定义单位、区间和来源字段。
- [x] 现有 Health Connect 权限流程与采集器继续工作，并使用可靠 outbox 协议。
- [x] 每个观测保留 data origin，不因值相似或时间重叠而自动删除另一来源。
- [x] WebUI 展示健康摘要、时间线、来源和缺失覆盖，而不把缺失显示为零。
- [x] 睡眠只展示来源提供的区间，不把设备空闲推断描述为睡眠事实。
- [x] Device 和 Query scopes 分别限制健康写入与读取，默认隐私级别符合敏感数据要求。
- [x] 来源记录数能够与服务端结果对账，非法或未获授权记录进入可诊断失败路径。

## Answer

契约先行：`contracts/schemas/events/` 新增三个版本化 Schema——
`health.step.sample.v1`（区间有界累计步数，payload `count:{value,unit:"steps"}`）、
`health.heartrate.sample.v1`（瞬时采样，`beats_per_minute` 整数且限制生理合理
区间 15–300，禁止 end_at——瞬时量无时长）、`health.sleep.session.v1`（来源
提供的完整区间，`duration:{value,unit:"ms"}` 必须等于 end−start）。三者共同
要求 payload `data_origin`（写入 Health Connect 的来源应用包名，服务端拒绝
路径形状），`source.record_id` 即 Health Connect 记录 ID（对账锚点），信封
`privacy_level` 默认 `sensitive`，`source.kind` 封闭枚举
`android.healthconnect`。`event.schema.json` 联合升级为四事件类型；每个
Schema 配 1 条有效样例与 3–4 条带预期错误码的无效样例；
`event-validator.mjs` 的 duration 一致性检查推广为"payload 声明了 duration
且 end_at 存在时必须匹配"（瞬时事件无 duration 不再误伤）。四端派生模型
重新生成，漂移门禁通过。

**scope 由单值升级为 scope 集合**：`CredentialScope` 枚举新增
`health:write`/`health:read`，device_token 允许持有 {events:write,
health:write} 的非空子集，query_token 允许 {events:read, health:read}，跨
主体 scope 仍然拒绝。批量上传端点改为 anyScope 准入 + **逐项** scope 强制
（activity 条目需 events:write、health 条目需 health:write，不足
`insufficient_scope` 逐项拒绝、其余条目继续推进）；`GET /api/v1/events` 对
凭据读取按 scope 授予的域过滤事件类型（无 health:read 的查询令牌永远读不到
健康观测，且 completeness 如实报 `partial`）；新增
**`GET /api/v1/health/events`**（health:read）作为健康域读取端点，其
completeness 基线是健康域自身，域外数据不会把页面误标 partial。健康观测
默认 `privacy_level: sensitive`（schema default + 注册表默认），默认
normal 隐私上限的凭据既不能上传也不能读取健康数据——敏感数据要求由隐私
天花板机制（票据 04）自然承接。

Android 重建（`android/app/src/main/java/com/ailife/android/health/`），
Health Connect 权限流程零改动（readPermissions、rationale 契约、授权门）：

- **类型化读取**（`HealthConnectCollector.readSamples`）：三条记录流产出
  sealed `HealthSample`（步数/心率/睡眠），每条保留 `record.metadata.id`
  （稳定记录标识）与 `metadata.dataOrigin.packageName`（data origin）。
- **纯函数规划器**（`HealthConnectEventPlanner`）：UUIDv5 身份 =
  `事件类型 + 设备 ID + 安装 GUID + Health Connect 记录 ID`
  （`identity/UuidNameIds` 统一 v5 算法，usage 与 health 共用同一命名空间，
  已知向量测试双向钉死）；revision 1 首报，来源应用改写记录（SHA-256 内容
  指纹变化）才升修订重发，未变记录跳过（重投是 outbox 的职责）；畸形记录
  （缺 end、区间倒置、空 origin）丢弃并计数上报，绝不静默入库；睡眠只输出
  来源区间，规划器根本不存在"空闲推断"路径。
- **耐久状态**（`HealthConnectSyncState`）：安装 GUID 与修订/指纹状态同
  文件共命运（票据 12 模式），7 天保留期裁剪；采集水印只在读取成功后推进，
  失败的 Health Connect 查询下一轮自动重读窗口。
- **队列统一**（兑现票据 12 的遗留项）：`ContractEventSpoolQueue`（NDJSON
  按 event_id upsert、进程重启存活、损坏行容错）、`ContractSyncFailures`
  （永久拒绝死信，保留稳定错误码）、`ContractEventQueueDrainer`（逐项确认：
  accepted/duplicate/stale_revision 移出队列，rejected 入失败队列永不重试，
  传输失败/响应数量不符整批保留）从 usage 包上提为共享契约队列，
  `LifeSyncWorker` 中使用与 UsageStats 完全相同的机制（不同队列文件）。
- **对账**：drainer 返回 `BatchAckCounts{sent, accepted, duplicates,
  staleRevisions, rejected}`，与规划器记录数（planned/unchanged/dropped）
  及同步页队列深度、失败计数（健康/使用分列展示）共同构成
  "来源记录数 ↔ 服务端确认"的可诊断对账面。

服务端：payload registry 新增三个健康条目（单位、区间语义、data_origin
强制与路径形状拒绝、未知字段拒绝、legal source kinds 按类型封闭），注册表
泛化为 `事件类型@版本 → {payload 形状, 合法来源, 写 scope, 隐私默认, 区间
语义}`；用量指标读取显式限制在 activity 域，健康观测永远不会进入
device/active minutes。

WebUI（`web/src/views/HealthView.vue` 重写，改读契约健康端点）：摘要卡
（步数/来源睡眠区间/平均心率/来源数，无观测显示"无数据"绝不显示 0）、
按日覆盖表（范围内每个报表时区日 × 三类观测，有观测显示条数、缺失显示
"无数据"，并注明缺失≠零）、按来源表（每个 data_origin 一行：观测数、步数、
心率、睡眠）、步数趋势与心率采样图表（只画存在观测的日期）、睡眠区间表
（UTC 全边界 + 来源 + 采集时区，明确标注"仅来源提供；系统从不把设备空闲
推断为睡眠"）、完整观测时间线（类型/来源/修订/采集时区）与游标分页；
`completeness=partial` 显示显式警告。报表时区取 Owner 设置，浏览器时区
不影响同一份查询。凭据页 Scopes 表单升级为所选主体类型的 scope 子集复选，
附健康 scope 与 sensitive 隐私上限提示。

测试（自动可测部分）：

- 服务端真实 HTTP + MongoDB 集成测试 7 条（`test/health-events.test.ts`）：
  三类健康观测带 origin 摄入与读取回显 + 重投幂等；逐项 scope 强制（单域
  凭据在混合批次中其余条目照常推进）；events:read 令牌 403 于健康端点、
  通用读取中健康观测被隐藏且 partial、health:read + sensitive 全量可见、
  health-only 令牌被通用读取 403；不同 origin 的重叠睡眠与相似步数全部
  保留（服务端无自动去重路径）；省略 privacy_level 的健康观测默认
  sensitive 并被 normal 上限逐项拒绝；八类非法/未获授权记录（错误单位、缺
  origin、缺 end、瞬时带 end、超界 bpm、duration 不匹配、非法来源种类、
  未注册类型）全部 rejected + 稳定错误码且零入库；健康观测对日指标零贡献。
- Android JVM 单测 17 条新增：身份稳定性与作用域（device/install/记录/
  事件类型四维）；规划器修订链（指纹不变跳过、变化升修订、畸形丢弃计数、
  多 origin 独立身份、敏感默认、瞬时无 end）；状态耐久（GUID 重生、修订
  跨重启、裁剪）；统一 spool 耐久/upsert/容错与 drainer 六种确认语义 +
  对账计数。
- Windows C# 34 条、contracts 6 条保持通过（生成模型重命名后的唯二源码
  触点为 SyncTests 可空 duration 断言与 CloudSyncClient 类型名）。

验证（Mongo 经 `docker compose up -d` 启动，全程未 `docker compose down`）：
`npm --prefix contracts run check`；`cd server && npm run typecheck &&
npm test && npm run build`（108 条测试全部通过，新增 7 条）；`cd web &&
npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（34 条通过）；`cd android &&
./gradlew.bat lint test assembleDebug`（44 条 JVM 单测通过，lint 与
assembleDebug 成功）。

## Comments

- 设计决策（三个 Schema 的区间语义三分法）：步数与睡眠是"区间有界"事实
  （契约强制 end_at，duration/步数按区间解释），心率是"瞬时"事实（Schema
  描述 + 服务端注册表拒绝 end_at，因为瞬时量带 end 会伪造时长并污染区间
  读取）。finalization_state 统一为 final——Health Connect 读出的记录是
  来源应用已写定的完整事实，检查点语义只属于客户端自产区间的延长流。
- 设计决策（data_origin 放 payload 而非 source）：`source.kind` 表达
  "采集机制"（android.healthconnect），`source.record_id` 表达记录身份，
  而 origin 是观测本身的属性——放 payload 使其进入 Schema 校验（必填、
  非路径形状），并随事件内容参与指纹，来源应用改写记录时随修订语义一起
  更新。
- 设计决策（scope 集合而非新增凭据类型）：SPEC 实现决策 4 的授权模型本就
  是"actor type + scopes + 隐私上限"能力集合，单 scope 1:1 绑定是票据 04
  的 V1 起点。保持 device/query 两类主体不变、把 scope 变成主体允许集合的
  非空子集，health:write/health:read 与 events:* 完全对称：写入端逐项强制
  （部分成功语义不变），读取端按域过滤（凭据永远无法读到自己 scope 之外
  的域），隐私上限继续承担敏感数据的第二道闸。
- 设计决策（新增 `GET /api/v1/health/events` 而非在 GET /events 上加多值
  过滤）：健康域有了自己的写 scope，就必须有对称的读 scope 与读取路径，
  否则 health:read 无物可读；独立端点让"逐页健康数据"不必与活动事件混页，
  completeness 基线取健康域自身（域外活动数据不会把健康页误标 partial），
  同时通用读取对凭据按域过滤保证无 health:read 的令牌在 GET /events 也读
  不到健康数据。Owner Session 两条路径都可读（Owner 不受 scope 约束）。
- 设计决策（队列统一而非第三份复制）：票据 12 明确把"三份 spool 的文件
  机制统一"留给本票。上提 `ContractEventSpoolQueue` / `ContractSyncFailures`
  / `ContractEventQueueDrainer` 后，usage 与 health 共享同一套 upsert、
  逐项确认、死信语义（微信支付旧通道仍留待票据 14 迁移）；BatchAckCounts
  把对账从"隐式的逐项 ack"提升为同步页可直接展示的计数。
- 设计决策（派生模型联合重命名 ActivityIntervalEventV1 → VersionedEvent）：
  quicktype 对对象联合做"合并展开"，EventBatchRequest.events 从单类引用
  变为四事件合并类（联合 Schema title 命名为 VersionedEvent）。这是联合
  协议从退化单成员走向真实多成员的必然后果：信封字段保持必填、各事件
  payload 字段全部可选、编码时省略 null，线上形状不变。受影响源码触点：
  Windows CloudSyncClient/两个测试（类型名与一个可空断言）、usage 事件
  类引用改名——均为机械重命名，Windows 行为零改动。
- 已知边界与后续：Android 真机冒烟（Health Connect 授权流、真实记录读取、
  与来源应用记录数对照）无法在 CI 复现，属真机验收步骤；WebUI 健康摘要的
  睡眠合计按报表范围裁剪重叠计入（区间全边界在时间线原样保留），跨范围
  睡眠两侧各计入——与服务端区间指标同一语义；来源优先级的显式版本化策略
  （多来源健康观测的规范化选择）属票据 15，本票只保证原始观测全部保留；
  未注册健康记录类型（血压等）服务端按 unknown_event_type 逐项拒绝进
  失败队列，扩展时新增 Schema 即可；Dashboard 旧健康图表仍读 legacy
  `/api/v1/health/summary|timeline`（非本票范围，随 legacy 清理票据处理）。
