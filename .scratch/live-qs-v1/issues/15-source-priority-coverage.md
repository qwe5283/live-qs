# 15 — 交付来源优先级与覆盖度

**What to build:** 让 Owner 明白每项摘要选择了哪个来源、有哪些冲突或缺口，并能依靠显式版本化策略获得稳定规范化结果，而不是静默模糊去重。

**Blocked by:** 12 — 接入 Android UsageStats; 13 — 接入 Health Connect; 14 — 接入微信支付结构化事件.

**Status:** resolved

- [x] 所有来源观测保持独立并可追溯，规范化结果引用其 source event identifiers。
- [x] 每类指标具有明确、版本化的来源优先级，不使用通用相似度删除观测。
- [x] WebUI 展示所选来源、策略版本、数据覆盖范围、冲突和待确认数量。
- [x] 查询响应区分零值、无数据、部分覆盖和来源冲突。
- [x] 更改来源策略只重建派生结果，不修改原始观测。
- [x] Android 应用时长、Health Connect 多 origin 和模糊支付候选均有确定测试样例。
- [x] 审计能够说明策略改变影响的时间范围和结果数量。

## Answer

契约先行：`openapi.yaml` 的 `QueryContext` 扩展四个可选字段——`source_policy_version`（本响应规范化结果所用的策略版本，单域读取必有、跨域通用读取不带）、`data_state`（`observed`/`zero`/`no_data` 三态存在性语义）、`source_conflicts`（`SourceConflict` 数组）、`pending_confirmation_count`（支付域待确认数量）。新增组件 `SourceConflict`（metric、policy_version、selected_source、selected_event_ids、competing_sources、competing_event_ids、冲突窗口 from/to——两侧观测全部以稳定事件标识引用，且竞争方只被搁置、从不删除）、`SourcePolicyEntry`/`SourcePolicyDocument`/`SourcePolicyImpact`/`SourcePolicyUpdateRequest`，以及 **`GET /api/v1/source-policy`** 与 **`PUT /api/v1/source-policy`**（均仅 Owner Session；PUT 返回带 `impact` 的文档）。四端派生模型重新生成，漂移门禁通过。

**策略对象**（`server/src/modules/source-policy/`）：`policy.ts` 为纯函数模块（与票据 08 的 interval-metrics 同型），定义五个指标键的显式优先级注册表——`usage.app_minutes`（默认 `windows.foreground` → `android.usagestats` → `android.accessibility`）、`health.step_total`/`health.sleep_minutes`/`health.heartrate_average`（按 Health Connect data_origin 排序，默认空表 = 全部按名称确定性排序）、`payment.transaction_totals`（默认 `android.wechatpay`）。**默认策略即版本 1**，在 Owner 首次修改前生效——已对 live 冒烟库（37000 条 `windows.foreground` 区间、单设备单来源、无存储策略文档）做过只读验证：默认策略下使用指标与升级前逐字节同行为。选择语义：排名函数把"列出的来源按策略顺序、未列出的按名称"排成全序，策略从未提及的来源也总有确定位置；**不存在任何按相似度删除观测的代码路径**。

**冲突检测范围（按域设计，通用机制）**：

- 使用（`usage.app_minutes`）：按**设备**选择——该设备上观测到的最高优先级来源种类对**全天**权威（SPEC 实现决策 21"UsageStats 权威于 Android 日应用总量，无障碍只支持当前与上下文"），竞争种类的区间全部搁置并按重叠聚类成冲突窗口上报（含不与选中区间重叠的搁置区间，避免静默丢失）；跨设备重叠不是冲突，那是 device_minutes 的既定语义。
- 健康（step/sleep）：按**指标类型内 origin 区间重叠**选择，合并重叠段为冲突窗口；不重叠的多 origin 观测互补、全部计入；心率瞬时样本仅在同一时刻出现多 origin 时冲突。
- 支付：待确认候选即冲突面（票据 14 的设计），以 `pending_confirmation_count` 呈现而非 `source_conflicts`——语义是"等待 Owner 裁决"而非"策略已替你选择"；合计仍覆盖全部观测以维持来源记录数对账。

**查询响应语义（清单第 4 项）**：`completeness`（complete/partial，票据 08/13 既有）承载部分覆盖；`data_state` 承载零值与无数据——`zero` = 范围内有观测但规范化贡献为 0（如零长区间；开放检查点本就不进区间读取），`no_data` = 范围内无任何观测，缺失绝不渲染成 0；`source_conflicts` 承载来源冲突。事件分页读取按升序分页的既有事实免费判定（首页为空 ⟺ 范围为空）。

**更改策略只重建派生结果（清单第 5 项）**：指标与摘要延续票据 08 的"计算即读取、不设汇总缓存"——策略是每次计算的输入，不存在可被策略污染的缓存身份，PUT 之后下一次读取即按新策略输出，原始观测从不被触碰。集成测试直接断言：PUT 前后全部事件文档（revision/updated_at/payload）逐字节相等且 EventRevision 零新增。

**审计（清单第 7 项）**：PUT 以 Owner 报表时区评估影响——对每个优先级实际变化的指标，重算新旧策略下的选择集合并取对称差，得到受影响观测及其触及的报表日（跨午夜区间两侧各计入），写入 `source_policy.update` 审计记录：`{ from_version, to_version, timezone, metrics_changed, affected_ranges, result_count }`，并在 PUT 响应的 `impact` 字段原样返回。确定测试样例断言两天范围、result_count = 2。无变化或无竞争数据的更新 bump 版本但 impact 为空；校验失败不升版本。

WebUI：新增共享组件 `SourcePolicyPanel`（来源策略版本、数据状态标签、来源种类、待确认数量、来源冲突表——指标/策略选中/竞争来源/事件数与完整 ID 悬浮/UTC 窗口），接入使用/健康/消费三个视图；使用页活动区间表对被策略搁置的观测加「未计入（来源策略）」标记；健康页睡眠/步数规范化合计排除竞争观测（保留展示）并在睡眠表加「策略状态」列；三域观测表新增紧凑事件 ID 列（悬浮显示完整 UUID）承载逐条可追溯性。

测试（自动可测部分）：

- 纯函数单测 10 条（`test/source-policy-selection.test.ts`）：排名全序；使用选择按设备、冲突聚类与合并、开放检查点/零长区间不参与、策略反转只影响目标设备；健康多 origin 重叠冲突与显式 origin 优先级、不重叠无冲突、瞬时同刻冲突；priorityFor 回退默认。
- 服务端真实 HTTP + MongoDB 集成测试 12 条（`test/source-policy.test.ts`）：默认策略文档与 Owner-only 准入；合成双来源样例（usagestats + accessibility 同设备）下指标只取权威来源、冲突条目引用事件标识、单来源报告零行为变化；zero/no_data 区分与事件页 data_state；健康双 origin 重叠双侧保留 + 冲突条目 + 无 pending 字段；支付待确认计数；PUT 升版本/impact/审计/观测不可变、空 impact、四类非法入参 + 混合条目逐项校验、Query Token 401。

验证（Mongo 经 `docker compose up -d` 启动，全程未 `docker compose down`）：
`npm --prefix contracts run check`；`cd server && npm run typecheck && npm test && npm run build`（136 条测试全部通过，新增 22 条）；`cd web && npm run build`；`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（34 条通过，生成模型重生成后编译无回归）；`cd android &&
./gradlew.bat lint test assembleDebug`（生成模型重生成，构建成功）。

## Comments

- 设计决策（策略按指标类型而非按域）：清单第 2 项要求"每类指标"有显式优先级，域级单条优先级无法表达"健康步数与睡眠可用不同 origin 偏好"。指标键是稳定标识符（`usage.app_minutes` 等），冲突条目、策略文档、审计影响共用同一套键；使用/支付指标的优先级值限定为契约 `SourceKind` 封闭枚举，健康指标的优先级值为 package 形状 origin（路径形状拒绝），与各域冲突的实际竞争维度一致。
- 设计决策（默认策略 v1 与 live 兼容）：默认优先级刻意保持现行为——每设备只有一种来源时选择退化为"全部保留"，故 34k+ 迁移区间（单设备单来源）在升级前后指标完全一致，live WebUI 无感。默认策略初始版本即 1，Owner 首次 PUT 后升为 2。
- 设计决策（使用域按设备全天权威、健康域按重叠窗口）：使用指标回答"这台设备今天用了多久"，权威来源必须对整天发言，否则无障碍区间会在 UsageStats 缺口处漏进总量造成重复；健康摘要回答"这段时间睡了多少"，不重叠的 origin 观测互补、只有重叠处才需要裁判。两域语义不同，机制（版本化优先级 + 冲突条目引用事件 ID + 竞争方零删除）完全通用。
- 设计决策（data_state 独立于 completeness）：零值/无数据描述"观测是否存在、值是否为 0"，部分覆盖描述"凭据限制扣减了什么"，来源冲突描述"多来源竞争如何裁决"——三者正交，单枚举表达不了组合（partial + zero、conflict + observed 等），故扩展现有 QueryContext 而非发明平行结构。
- 设计决策（影响评估按报表时区日归属）：审计"影响多少结果"以规范化日结果为计量单位（周报由日结果派生）；跨午夜区间两侧各计入，与票据 08 的指标裁剪语义一致。payment 优先级可记录偏好但单来源现实下永不改变结果，影响恒为空并如实上报。
- 设计决策（PUT 语义）：请求 entries 是对所列指标的全量替换，未列出的指标保持默认；每次成功调用都升版本（含无变化更新，便于 Owner 标记审阅），失败校验不升版本。GET/PUT 仅 Owner Session——Query Token 通过查询上下文的 `source_policy_version` 与冲突条目获得全部透明度，策略文档本身属管理面。
- 评审修复：自审（Standards 轴）发现 validateEntries 在首个健康条目后提前 return、跳过对剩余条目的校验（混合条目回归测试当场钉死）；影响评估中日归属循环里逐条 `rows.find` 为 O(n²)（37000 条 live 数据下策略变更会卡顿），抽取 `daysOfFlippedObservations` 消除；同时把三域观测表补上事件 ID 列（Spec 轴清单第 1 项的可追溯性显性化）。
- 已知边界与后续：真实采集数据目前不存在使用域冲突（无障碍只产心跳、票据 12 结构性保证），冲突样例为合成构造——这正是机制存在意义的演练；冲突窗口数量与事件 ID 列表随竞争数据规模线性增长（当前真实数据为 0 冲突，未设上限以保显式性）；周报冲突按整周窗口上报，按日分解不带冲突明细；WebUI 暂无策略编辑界面（PUT 端点与 impact 语义已就绪，管理 UI 随 Owner 设置扩展交付）；待确认交易的确认/驳回操作流属票据 11 的可审计修正；健康摘要按 origin 的策略选择在重叠处生效，跨 origin 全局去重（如双设备步数合并）属 V1 后语义。
