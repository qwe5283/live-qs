# 16 — 交付只读 AI Skill

**What to build:** 让获得受限 Query Token 的 AI Agent 查询当前上下文、时间摘要和数据覆盖，并生成带证据的分析建议，同时在协议上完全没有修改或执行能力。

**Blocked by:** 04 — 交付分级凭据管理; 08 — 交付时区与双时间指标; 11 — 交付可审计人工修正; 15 — 交付来源优先级与覆盖度.

**Status:** resolved

- [x] Skill 只包装公开 OpenAPI 查询能力，不复制统计或来源选择逻辑。
- [x] Query Token 可分别限制活动、健康、消费和当前上下文读取。
- [x] 每次查询必须有受限时间范围、分页、速率限制、到期和撤销行为。
- [x] 响应包含报表时区、时间范围、来源、覆盖度、冲突和 corrected 状态。
- [x] AI credential 无法访问事件写入、修正、规则、凭据、删除或管理操作。
- [x] 审计记录访问主体、scope、时间范围、数据类型和结果数量，但不保存提示词正文。
- [x] 使用缺失和冲突样例验证 Skill 不会把不完整数据表达成确定事实。

## Answer

本票交付 V1 工作流的最后一环：`skill/` 成为 Monorepo 第五个活跃组件
（`@live-qs/skill`，自有 package.json + lockfile，AGENTS.md 组件清单与
`scripts/check.ps1` 已同步扩充 `skill` 一节）。它是一个薄、确定性的只读
包装层，由三部分组成：

- **`skill/src/generated/contract-models.ts`**：契约模型第五个派生目标，
  由 `contracts/scripts/generate-models.mjs` 从 `openapi.yaml` 生成并纳入
  `contracts run check` 漂移门禁（ADR-0004"generated models derive from
  contracts"）——Skill 不手抄任何端点或字段，协议漂移在 contracts 层即被拦截。
- **`src/client.ts`**：HTTP 客户端与公开查询操作一一对应（status、
  diagnostics/sync 读、events、health/events、payment/events、metrics
  usage day/week），Bearer 头注入、稳定错误码映射（`LiveQsApiError`）、
  429 透出 `Retry-After`、`iteratePages` 逐页跟随 `next_cursor`。零统计、
  零来源选择逻辑；所有数字、来源、冲突引用原样透传。
- **`src/render.ts` + `src/cli.ts` + `SKILL.md`**：确定性渲染层把 API 响应
  转成带覆盖度注记的文本（presence 语义决定措辞：`no_data` → "missing,
  not zero"，`zero` → "measured zero"，`partial` → "undercount"，冲突 →
  列出选中与被搁置来源及事件 ID，corrected 事件 → 标注人工解释）；
  CLI 提供 `status / diagnostics / usage-day / usage-week / events /
  health-events / payment-events / briefing [--json]` 八个命令；`SKILL.md`
  是 AI Agent 的入口契约——scope 对照表、诚实性九条（缺失≠零、partial
  必须承认低估、冲突保持可见、device/active minutes 不得混用、心跳不是
  历史、睡眠只是来源观测、用同步诊断解释缺口而不是猜、时区纪律、修正即
  provenance），以及硬边界声明（建议只提不执行、提示词正文永不传输存储、
  429 等待 Retry-After）。

契约先行：`CredentialScope` 新增 **`context:read`**（查询令牌专属，四个
读取域 = events:read 活动 / health:read 健康 / payment:read 消费 /
context:read 当前上下文）；票据 06/07 显式延后的两个"先紧后松"开放点就此
放宽——`GET /api/v1/status` 与 `GET /api/v1/diagnostics/sync`（读）从仅
Owner Session 扩为 Owner Session 或持 `context:read` 的 Query Token。
选两个都开而不仅开 `/status` 的原因：诊断读回答"缺失数据是没有活动、
未上传还是同步坏了"，与心跳状态一起构成覆盖度解释的完整证据链（清单第 7
项的机制前提）。心跳与诊断投影本就受契约 `additionalProperties: false`
约束、无自由文本字段，隐私上限在事件读取侧照旧执行；二者不含带隐私级别
的数据，无内容可被 ceiling 扣减。该放宽与票据 07/06 预告的兼容路径一致
（新增一个只读认证选项，不改变任何既有客户端行为）。

**速率限制落地（票据 03 起延后）**：`credentialBearerAuth` 在凭据解析成功
后立即执行每凭据固定窗口限流（默认 120 次/分钟，`RATE_LIMIT_PER_MINUTE`
可配），超限 429 `rate_limited` + `Retry-After`（契约响应早已声明、至此
才有实现）+ `credential.deny` 审计；限流在 scope 检查之前，执意高频的
凭据即使请求本会被拒也照样被节流。Owner Session 与公开端点不在限流范围
（LAN 信任边界内，公网化安全门另行重开，SPEC Out of Scope 既定边界）。
计数器为进程内存实现——V1 单进程部署成立，多实例需共享存储时再议。

**查询令牌有界时间范围**：三个事件域读取（/events、/health/events、
/payment/events）对凭据读取强制 `to - from ≤ QUERY_TOKEN_MAX_RANGE_DAYS`
（默认 366 天，测试可收紧到 2 天验证逐请求语义），超限 400
`range_too_large`；Owner Session 不受限。指标端点天然按日/周有界，无需
重复检查。分页（page_size ≤ 200 + cursor）、到期（使用时拒绝）、撤销
（即时 401）沿用票据 04 既有行为，至此 SPEC 实现决策 25 的六项凭据边界
（resources、time range、pagination、rate limit、expiry、revocation）全部
可执行。

**无提示词的读取审计**：新增 `query.read` 审计动作（`recordQueryAudit`，
凭据读取统一调用、Owner Session 读取不记），记录访问主体（credential
id/kind）、scope 集合、请求时间范围与报表时区、数据类型
（`data_types`：event_type 过滤或授权类型集 / `usage.metrics` /
`device_status` / `sync_diagnostics`）、结果数量与 completeness。提示词
正文在协议上不可达——Skill 只发送结构化查询参数，服务端没有 prompt 字段，
`sanitizeAuditDetails` 的 payload/raw 类键名脱敏兜底；测试断言审计明文
不含 prompt/window_title/authorization 等键。

**否定矩阵（清单第 5 项）**：持全部四个读 scope 的查询令牌对 events/batch
(403)、heartbeats POST (403)、diagnostics POST (403)、corrections (401)、
credentials 创建/列表/撤销 (401)、owner settings (401)、source-policy
GET/PUT (401)、ruleset GET (403，rules:read 设备专属)/PUT (401)、
reclassification (401)、admin/events/delete (401)、admin/audit-logs (401)、
legacy context/current (401) 全部被拒；撤销与过期的令牌连 `/status` 也
401（过期在使用时强制）。

**诚实性契约测试（清单第 7 项）**：13 条 fixture 测试钉死渲染层行为——
`no_data` 绝不渲染成 0（用量报告直接输出"Device minutes: unavailable …
missing data, not zero"且不打印服务端算出的 0 与分设备明细）、`zero` 与
`no_data` 措辞区分、partial 必须写明低估与原因、冲突必须列出选中/竞争
来源与完整事件 ID 且注明"竞争观测仍被保留"、open interval 不发明时长、
并发设备轨道不合并单一焦点、永久失败标注"将永远缺失"。4 条离线客户端
测试验证 Bearer 注入、逐字查询串、错误码映射与分页跟随；5 条集成测试
拉起真实服务器进程（真实 MongoDB 独立测试库）走完整链路：Owner 初始化 →
双凭据创建 → 契约批量上传种子观测 → Skill 客户端读当前上下文/有数据日
(observed, device 45/active 30)/空日(no_data 渲染成 missing)、超 366 天
范围 400 `range_too_large`、六个管理面全部 401/403。

验证（Mongo 经既有容器，全程未 `docker compose down`，测试库与 live 冒烟库
隔离）：`npm --prefix contracts run check`（lint + 6 条测试 + 五目标模型
同步）；`cd server && npm run typecheck && npm test && npm run build`
（190 条全部通过，新增 16 条）；`cd skill && npm run typecheck && npm test
&& npm run build`（22 条全部通过）；`cd web && npm run build`；
`cd windows && dotnet test
LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（67 条通过，生成模型重生成后编译无
回归）。

## Comments

- 设计决策（Skill 放置与打包）：新顶层 `skill/` 组件而非塞进 web/server
  ——它是被 AI Agent 加载/调用的独立制品，无浏览器依赖、无服务端运行时
  依赖（Node 20 内置 fetch，运行时依赖为零），自有 lockfile 符合"组件
  各自管版本、无根 npm workspace"约定。ADR-0004 的"generated models
  derive from contracts"通过第五个生成目标兑现：手抄端点是被点名要避免
  的漂移源，`contracts run check` 现在对 Skill 也有牙齿。
- 设计决策（当前上下文只开读、且 status+diagnostics 一起开）：`context:read`
  只覆盖两个只读投影端点，不触 legacy 的 `/context/current` 与 `/devices`
  （它们绕过契约信封、经全局 Owner 守卫，查询令牌一律 401）。诊断读让
  Agent 区分"没采集/没传上来/传坏了"，是把"缺失数据表达成不确定"落到
  机制上的配套；两个投影均无隐私级别数据可扣减，ceiling 语义在事件域读取
  中不受影响。
- 设计决策（限流位置与默认值）：限流放在凭据解析之后、scope 检查之前，
  作为"凭据的每分钟总预算"而非"每端点配额"——单凭据单预算最可预测，也
  让攻击者不能用换端点绕开。默认 120 次/分钟远高于真实采集器节奏（心跳
  15s = 4 次/分钟），live 冒烟不受影响；`RATE_LIMIT_PER_MINUTE` 环境变量
  供测试收紧到个位数逐请求验证 429/Retry-After。
- 设计决策（有界范围默认 366 天）：SPEC 只要求"bounded"未给数值，取与
  既有 backfill 上限（datesBetweenInclusive 366）一致的一年量级——足够
  年度分析、又把"一次查询扫全库"挡在协议外；实现为逐请求检查而非每令牌
  累计配额，后者会与限流语义重叠且不可预测。
- 设计决策（query.read 与 credential.use 分立）：`credential.use` 在鉴权
  中间件发出（尚无结果数量可记），清单要求的范围/类型/结果数只能事后补
  记；两者共用同一审计模型，`credential.use` 继续覆盖"用没用"，
  `query.read` 回答"读了什么、读了多少"。设备令牌无读 scope，实际仅
  查询令牌产生该记录。
- 评审修复：自审（Standards 轴）发现六处 `if (credential)` 审计守卫重复
  ——`recordQueryAudit` 收编为"凭据缺席即不记录"的单一入口；rate-limit
  模块的内联 `import("express").Response` 改为顶层类型导入；CLI
  `dateParams()` 单命令内重复求值收敛为一次解构。
- 已知边界与后续：限流计数器为进程内存（多实例部署需共享存储，属公网化
  门）；`QUERY_TOKEN_MAX_RANGE_DAYS`/`RATE_LIMIT_PER_MINUTE` 为全局默认，
  尚不支持按凭据定制配额（契约也未暴露），按凭据差异化的边界控制留给
  Owner 在创建时用 scope/隐私上限/有效期组合表达；Skill 渲染层为英文
  输出（与仓库文档一致），中文本地化可后续叠加；集成测试每轮新建
  `live_qs_test_skill_*` 库，容器内的测试库清理依赖既有运维节奏；android
  生成模型同批重生成（CredentialScope 仅新增枚举值，android 业务代码无
  引用，未跑全量 gradle，属本票范围外的一惯性确认项）。
