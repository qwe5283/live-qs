# 09 — 交付本地语义分类

**What to build:** 让 Owner 在 WebUI 定义跨平台服务和项目语义，由 Windows 在本地使用标题规则分类并只上传可解释标签，使浏览器与原生应用可以按同一 subject 汇总。

**Blocked by:** 04 — 交付分级凭据管理; 05 — 打通 Windows 活动纵向链路.

**Status:** resolved

- [x] Owner 能管理语义实体、应用或包名规则、标题关键词或正则、优先级和规则版本。
- [x] Device Token 只能读取适用规则；客户端缓存最后成功版本并可离线执行。
- [x] Windows 在本地匹配原始标题，上传结果只包含 subject、类别、规则标识、版本和置信度。
- [x] 未获批准的项目名称不会作为云端标签上传，而以不透明标识表示。
- [x] Edge 中命中哔哩哔哩的活动可与 Android 包名映射到同一服务 subject，同时保留应用和设备来源。
- [x] Rider 可按批准别名区分项目，原始项目标题仍留在本地。
- [x] 如需上传标题指纹，必须使用设备秘密 HMAC；普通未加盐摘要不满足要求。
- [x] 黄金样例测试覆盖规则优先级、无匹配、冲突、版本和跨平台映射。

## Answer

契约先行：`openapi.yaml` 新增 **Classification** 标签与 **`GET/PUT /api/v1/classification/ruleset`**。`ClassificationRuleSet` 文档 = `rule_set_version`（0 = 从未发布，每次成功 PUT +1）+ `entities`（`SemanticEntity { entity_id, kind: service|project, name }`，全部为 Owner 批准的语义主体）+ `rules`（`ClassificationRule`，按优先级降序、rule_id 升序的执行序分发）。规则三种匹配方式：`application`（应用名/包名，Windows 侧大小写不敏感）、`title_keyword`（标题包含，大小写不敏感）、`title_regex`（标题正则，唯一能提取候选项目名的形式）；`priority` 冲突时数值大者胜、同值按 rule_id 确定性排序；`confidence` 按匹配方式给默认（application=1、title_regex=0.9、title_keyword=0.8）；每条规则携带**服务端管理的 per-rule `version`**（创建或任何字段变更时 +1，未变规则重发保持版本号），让历史上传永远可对照"当时的规则版本"解释。**scope 集合扩员**：`CredentialScope` 新增 **`rules:read`**，仅 device_token 可持有（规则分发是采集器关切，管理面留在 Owner Session；Query Token 不可持有），GET ruleset 即 `sessionOrCredentialAuth(scope: rules:read)`，PUT 仅 `ownerAuth()`。四端派生模型重新生成，漂移门禁通过。

服务端（`server/src/modules/classification/`）：沿用票据 15 的"GET 文档 + PUT 全量替换 + 版本递增"模式。PUT 以 zod strictObject 校验整份文档——entity_id/rule_id 为小写 slug、规则目标恰好一个（subject_entity_id 或 dynamic=true 二选一）、目标实体必须同批存在（删除实体必须同批删改其规则，发布文档永远自洽）、dynamic 只能是 title_regex 且必须含捕获组（空替代技巧 `new RegExp(pattern + "|").exec("")` 数组长度校验）、正则必须可编译——设备永远不会收到它执行不了的规则；失败统一 400 稳定码（`invalid_rule_set` / `duplicate_entity` / `duplicate_rule` / `invalid_rule_target` / `unknown_entity` / `invalid_dynamic_rule` / `invalid_pattern`）且**版本不递增**。每次成功发布写 `classification_rules.update` 审计：from/to 版本 + rules_added/updated/removed（updated 只计定义实际变化的规则）+ entities_removed，不含任何凭据材料与本地文本。事件摄入零改动——`payload.subject_id` + `payload.classification` 契约字段（票据 02/05 早已就位）由注册表校验原样入库。

Windows（`LiveQs.Windows.Core/Classification/`）：`ClassificationEngine` 为纯函数——按分发序（priority 降序、rule_id 升序）取第一条命中的规则，产出 `ClassificationOutcome { SubjectId, RuleId, RuleVersion, Confidence }`；平台过滤（windows 规则在 Android 无效）、.NET 正则 try/catch 降级为无匹配（跨平台正则方言差异的安全退化）、AFK 区间不分类（无活动即无可命名的主体）。**未批准项目的动态发现**：`dynamic` 规则命中时取正则捕获组 1 的候选项目名，上传 `subject_id = "unapproved-" + HMAC-SHA256(设备秘密, "project:"+名称) 前 128 位十六进制`——同一项目在同一设备上聚合于同一稳定标识，名称与任何未加盐摘要都不上传；设备秘密为 SQLite `sync_state.classification_secret`（库内生成 32 随机字节，永不离开设备、永不轮换）。规则缓存落 SQLite `classification_cache`（整份 JSON + 版本 + 抓取时间），`ClassificationRuleSync` 以 15 分钟 TTL 经 `GET /ruleset`（Device Token）刷新，**传输失败/非 2xx/空响应一律保留上一成功版本**，离线分类照常执行；`CloudSyncClient` 在上传时对非 AFK outbox 项以缓存规则集分类，把 subject 与 classification 附加进既有信封。本地的 `application_rules` 表（别名/类别/排除）保持原职责不动。

Android（`com.ailife.android.classification/`）：SPEC 实现决策 17"客户端下载、缓存并在本地执行规则"对 Android 同样成立，选择**设备端打标**而非服务端分组——用量规划器（`UsageStatsEventPlanner`）新增纯函数参数 `subjectOf`，对包名应用 application 规则（标题规则在 Android 不可执行）产出 subject + classification；`DeviceClassifier` 与 Windows 引擎同一匹配/排序语义，`RuleSetCache` 以文件缓存（`classification-rules.json`，含抓取时间戳）承载同样的 TTL 刷新与离线保留语义，`ReportClient` 新增 `getRuleSet()`。

WebUI：新增 **`/classification`「语义分类」**页——规则集版本卡（当前版本、发布时间、未发布修改警示）、语义主体表（entity_id、服务/项目类型、批准别名 + 已批准徽标、被引用规则）、分类规则表（规则标识、平台、匹配方式、模式、分类目标——指定主体或"动态发现（不透明标识）"、优先级、置信度、版本），主体/规则各自的新建与编辑弹窗（编辑时标识不可改、动态发现仅标题正则可选），草稿-发布两段式提交（一次 PUT 发布全量）。页首固定说明未批准名称以设备密钥 HMAC 不透明标识出现在云端。凭据页 scope 复选补齐 device_token 的 `payment:write`/`rules:read` 与 query_token 的 `payment:read`（后两者为票据 14 遗留的展示缺口）。

测试（自动可测部分，黄金样例全覆盖）：

- 服务端真实 HTTP + MongoDB 集成 8 条（`test/classification-rules.test.ts`）：空文档 v0 分发与设备读取；发布 v1 + per-rule 版本物化（默认平台/置信度）+ 审计；未变规则保版本、变更规则 +1、新规则 v1、删除规则消失（文档 v2 + 审计计数）；九类非法更新逐项稳定拒绝且版本不变；无 rules:read 的设备/查询令牌 403、Bearer 写入 401；**跨平台黄金样例**——Edge 会话与 Android 包名两台设备两条观测映射同一 `svc.bilibili` 而设备/平台/来源各异且设备身份独立；无匹配区间不上传 subject/classification、`unapproved-*` 不透明标识原样入库；Owner 修正把 subject_id 改指其他主体（高位修订）而 classification 自动解释原样保留。
- Windows 13 条（`ClassificationTests.cs`）：包名/可执行名大小写不敏感匹配、标题关键词、优先级冲突与同优先级 rule_id 决胜、无匹配四形态、平台过滤、批准别名映射项目、动态发现的 HMAC 不透明标识（稳定、名敏感、随秘密变化、非明文 SHA-256）、不可执行正则降级、SQLite 缓存往返与秘密稳定、刷新成功替换/失败保留/TTL 内不重抓、上传信封含可解释标签且**原始标题不出现在线上 JSON**。
- Android 9 条（`classification/` 包）：包名规则映射共享主体、无匹配静默、冲突/决胜、Windows 规则不作用于 Android；缓存存取、失败保留、TTL 内不重抓、坏响应保留、损坏文件退化为无规则而非崩溃。

验证（Mongo 经 `docker compose up -d`，全程未 `docker compose down`；测试全部使用独立 `live_qs_test_*` 库）：
`npm --prefix contracts run check`（lint + 6 条测试 + 四端模型同步）；`cd server && npm run typecheck && npm test && npm run build`（**153 条测试全部通过**，新增 8 条；新增 `vitest.config.ts` 关闭测试文件并行——19 个集成文件共享同一真实 MongoDB，并行时 dropDatabase 钩子互竞争超时，改为顺序执行后全套稳定）；`cd web && npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（**47 条通过**，新增 13 条）；`cd android &&
./gradlew.bat lint test assembleDebug`（**74 条 JVM 单测通过**，新增 9 条，lint 与
assembleDebug 成功）。dev 服务（8787/5173）全程在线，未触碰 live 冒烟库与采集器。

## Comments

- 设计决策（规则集文档 + per-rule 版本双层版本化）：备选是仅为整个规则集维护单一版本（每次 PUT 全体规则版本齐涨）。双层让"可解释性"落到最细粒度——历史事件引用的 `rule_version` 精确指向该规则的当次定义，Owner 微调一条规则不会虚增其他规则的版本；文档级 `rule_set_version` 只服务缓存一致性（设备据此判断要不要替换缓存）。代价是 PUT 服务端要做"规则签名比对"（归一化字段逐项相等才保版本），实现集中在一个纯函数里。规则被删除后历史引用仍可对照审计与旧版本解释；当前文档不保留已删除规则的历史清单（审计含 added/updated/removed 计数），专门的规则历史查询属后续诊断票据。
- 设计决策（PUT 全量替换而非逐实体/逐规则 CRUD 端点）：与票据 15 的 source-policy 同构，一个原子操作保证"规则引用的实体必须存在"这类跨集合不变量天然成立（分端点 CRUD 则需要级联校验与部分失败补偿）；单 Owner 场景下并发编辑不构成现实问题。WebUI 以草稿-发布两段式呈现，体验等价于 CRUD 而一致性由服务端保证。
- 设计决策（`rules:read` 仅 device_token 可持有）：设备需要拉规则离线执行（SPEC 实现决策 17），Query Token 的规则感知（如 AI 读取时解析 subject 名称）不在本票范围，持有规则集文档只会扩大管理面泄露面；KIND_SCOPES 保持"设备 = 写域 + 分发读，查询 = 读域"的清晰分层。若后续 AI Skill 需要 subject 字典，届时显式扩 scope 并重新评估。
- 设计决策（未批准项目名 = 设备秘密 HMAC 不透明标识，而非"什么都不传"）：SPEC 实现决策 18 的原文是"保持在本地并使用不透明标识"——完全不传会让同项目活动在批准前完全无法聚合，丢失票据 10（历史重分类）到来前的可改进性。标识以设备生成秘密（`sync_state.classification_secret`）键控 HMAC-SHA256，仅取前 128 位：单向（服务端无法还原名称）、跨重启稳定（同一项目同一标识）、跨设备天然不同（各设备秘密独立，聚合边界以设备为准）。这也顺带落实了清单第 7 项——凡上传的"标题类指纹形态"只有这一个，它用的是设备秘密 HMAC，而非普通未加盐摘要；本地段合并用的 SHA-256 `window_title_hash` 只存 SQLite 从不上传（黄金测试断言线上 JSON 既无原始标题也无其未加盐摘要）。
- 设计决策（清单第 3 项"类别"的落位）：活动事件的契约载荷没有独立的 category 字段（ADR-0004：契约是唯一权威），语义结果即 `subject_id`（服务或项目主体），加上 `classification{rule_id, rule_version, confidence}` 三件套恰好构成"subject、类别、规则标识、版本、置信度"的可解释集合；Windows 本地 `application_rules.category`（未分类/工作等展示分类）属本地时间线 UI，保持本地职责不上传。
- 设计决策（Android 端打标而非服务端/WebUI 分组）：SPEC 实现决策 17 明确"客户端下载、缓存并在本地执行适用规则"，Android 是客户端；且原始上下文（包名）在设备上，本地执行让"分类结果永远来自本地可见的事实"。Android 用量观测只有包名无窗口标题，故只执行 application 规则，标题/发现规则按平台过滤静默跳过。分类在规划时进行：规则变更影响下一次规划产生的新修订（检查点流），不改写已上传事实——与 Windows 的"规则变更只影响未来观测"（SPEC 实现决策 19）同语义。
- 设计决策（Windows 分类在上传时而非采样时执行）：原始标题本就存 SQLite 并随 outbox 流转，上传时对最新缓存规则集分类使"最后一次成功拉取的规则版本"一致地作用于整批待传观测，且无需改动 activity_segments 表结构与本地合并指纹逻辑；长会话检查点每次 +1 修订重传时若规则已变，新修订引用新规则版本、旧修订在服务端归档可查——恰好是"规则变更影响未来观测、历史不受影响"的字面实现。
- 设计决策（刷新失败保留缓存 + 15 分钟 TTL）：SyncWorker 每 10 秒一轮，规则刷新不逐轮发起（文档小但没必要）；TTL 从"发起刷新"时刻起算，失败也等下一窗口，避免对故障服务端形成每 10 秒的重试风暴。代价是首次部署恰逢服务端不可达时设备最多 15 分钟内以空规则集运行（无匹配、不上传 subject）——隐私上是最安全的退化方向。
- 评审修复：自审（Standards 轴）消除了 mergeRules 中"重发即计为 updated"的计数缺陷（更新数现只计定义实际变化的规则，混合场景审计计数被测试钉死）；删除了分类引擎上无用的无参重载与 Android 端多余的响应包装类型；cross-platform 黄金测试改用两枚独立设备凭据，使"设备身份服务端绑定、各观测保留独立设备通道"成为被断言的事实而非巧合。
- 已知边界与后续：规则删除后的历史 rule_id/version 解析依赖审计与版本说明，暂无"规则历史"查询界面；正则跨平台方言差异仅以"服务端 JS 编译校验 + 设备端 .NET try/catch 降级无匹配"双保险兜底，未做语法白名单；WebUI 暂不展示设备端的未批准候选名（它们在 HMAC 之后，设备 UI 的本地时间线是唯一可见处），批准流 = Owner 自建主体 + 指定主体规则；规则变更的历史重分类（对本地保留原始上下文重跑规则并提交高修订）属票据 10；payment.category 的规则版本化管理（票据 14 预留）未在本票重建——其分类表是设备端解析器的常量，属不同机制，待支付规则化需求出现时另行设计。
