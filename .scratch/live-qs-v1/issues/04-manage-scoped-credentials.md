# 04 — 交付分级凭据管理

**What to build:** 让 Owner 从 WebUI 创建、查看和撤销用途明确的 Device Token 与 Query Token，使每个设备或 Agent 只获得完成任务所需的能力。

**Blocked by:** 03 — 交付局域网 Owner 登录.

**Status:** resolved

- [x] Owner 能为凭据设置名称、主体类型、scopes、允许事件类型、隐私上限和到期时间。
- [x] Token 明文只在创建时展示一次，持久化存储仅包含安全摘要和可识别前缀。
- [x] Device Token 只能上报获准事件，不能查询个人数据或管理凭据。
- [x] Query Token 只能读取获准数据域，不能访问任何写入或管理操作。
- [x] Owner 能查看创建时间、到期时间、最后使用时间和撤销状态，并可立即撤销。
- [x] 创建、使用、拒绝和撤销行为产生不包含秘密值的审计记录。
- [x] HTTP 集成测试覆盖 scope 拒绝、事件类型限制、隐私上限、过期和撤销。

## Answer

契约先行在 `contracts/openapi.yaml` 新增 Credentials 标签与三个端点：
`POST /api/v1/credentials`（创建，201 返回 `{credential, token}`，明文仅此一次）、
`GET /api/v1/credentials`（列表，含创建/到期/最后使用/撤销状态，无任何令牌材料）、
`POST /api/v1/credentials/{credential_id}/revoke`（立即撤销，重复撤销幂等 204，
未知凭据 404，新增 `NotFound` 响应组件）。新 schema：`CredentialKind`
（device_token/query_token，对应 SPEC 的 actor type）、`CredentialScope`
（events:write/events:read，scope 与主体类型绑定）、`CredentialPrivacyCeiling`
（normal < sensitive < private）、`CredentialCreateRequest`、`CredentialView`、
`CredentialCreated`、`CredentialList`。四端派生模型已重新生成并通过漂移检查。

服务端新增强据模型 `Credential`：数据库只存令牌的 SHA-256 摘要（`token_hash`
唯一索引）和可识别前缀（`token_prefix`，如 `lqdev_ab12cd…`/`lqqry_…`），
明文 `randomBytes(32)` 生成后不落库不落日志（测试断言响应、数据库、审计记录
均不含明文）。创建时校验 scope 必须与主体类型一致（设备令牌恰好持有
events:write，查询令牌恰好持有 events:read），杜绝跨主体越权 scope。

授权中间件 `credentialBearerAuth` 统一处理 Bearer 凭据：未知 401
`unknown_token`、过期 401 `token_expired`、已撤销 401 `token_revoked`
（过期在每次使用时强制执行，不用 Mongo TTL 清理以保留 Owner 可见的生命周期
历史）；scope 不足 403 `insufficient_scope`。`GET /api/v1/events` 由
`sessionOrCredentialAuth` 守卫（Bearer 凭据优先，否则 Owner Session Cookie），
查询令牌的隐私上限以过滤方式执行（高于上限的数据从结果中排除，
`context.completeness` 如实报告 `partial`），允许事件类型列表同样过滤读取；
Owner Session 读取不受上限约束。`POST /api/v1/events/batch` 仅接受设备令牌：
逐项校验契约信封结构（未知事件类型 `unknown_event_type`、未知 schema 版本
`unknown_schema_version`、结构错误 `invalid_event`、owner_id 不匹配拒绝），
逐项强制允许事件类型（`event_type_not_allowed`）与隐私上限
（`privacy_ceiling_exceeded`，逐项拒绝而非整批失败），已获准条目持久化并按
event_id 幂等（重投返回 `duplicate`）。设备身份服务端绑定：持久化的
`device_id` 取自凭据 ID 而非客户端声明的 `device.id`，单一凭据即单一采集器，
被攻陷凭据无法伪造其他设备。

按 ADR-0005 移除旧令牌路径：`DEVICE_TOKEN_*` 环境变量解析、env 型
`deviceAuth` 中间件与 `/api/v1/ingest/*` 旧路由整体删除（旧路由不在契约中，
且属无 scope、无审计、明文驻留环境的通道），Windows/Android 同步在票据 05
按新批量协议重建前暂时中断。`AuditLog.actor_type` 增加 `query`，
`recordAuditLog`/`sanitizeAuditDetails` 从 admin 模块提升到 `shared/audit.ts`；
`credential.create`/`credential.use`/`credential.deny`/`credential.revoke`
审计记录只含凭据 ID、类型、前缀、原因码等非秘密字段。`last_used_at` 以每凭据
每分钟至多一次的节流写避免每请求写放大。EventModel 增加信封列
（schema_version、revision、finalization_state、provenance、capture_timezone、
capture_offset_minutes、invalidated、source_kind、source_record_id、
device_platform），读取端重建契约信封；契约无法表达的 legacy `private` 行
不进入契约响应。

WebUI 新增「凭据管理」页（`/credentials`，侧边栏入口，会话守卫之内）：
创建表单支持类型、名称、允许事件类型、隐私上限、到期时间；创建成功弹窗
明文展示一次并提供复制，关闭后不可再查看；表格展示名称与前缀、类型、scopes、
允许事件类型、隐私上限、创建/到期/最后使用时间与状态（有效/已过期/已撤销），
支持确认后立即撤销。

验证：`npm --prefix contracts run check`；`cd server && npm run typecheck &&
npm test && npm run build`（49 条测试全部通过，其中 `test/credentials.test.ts`、
`test/events-read.test.ts`、`test/token-authorization.test.ts` 共 26 条 HTTP
集成测试对真实 MongoDB 覆盖创建明文一次性、摘要与前缀落库、scope 拒绝、
事件类型限制、隐私上限（上传逐项拒绝 + 读取过滤与 partial 标记）、过期、
撤销、幂等撤销、last_used_at、审计无秘密等；Mongo 经 `docker compose up -d`
启动）；`cd web && npm run build`。

## Comments

- 设计决策（env 令牌去留）：选择立即删除 `DEVICE_TOKEN_*` 环境令牌（选项 b）
  而非保留为过渡桥。SPEC 实现决策 4 要求服务端只存令牌哈希、能力化授权，
  环境令牌是无 scope、无审计、明文驻留的通道；SPEC Out of Scope 明确排除
  "legacy tokens"，ADR-0005 禁止为已删除的行为新增兼容代码。任务给出的选项 a
  （显式过渡桥）与 ADR-0005 "New work must not add compatibility behavior"
  直接冲突。代价：Windows/Android 同步在票据 05 按契约批量协议重建前中断，
  V1 未发布故可承受。
- 设计决策（隐私上限的执行方式）：写入（批量上传）逐项拒绝
  `privacy_ceiling_exceeded`；读取（GET /events）过滤高于上限的数据并把
  `context.completeness` 置为 `partial`，让只读 Agent 明确知道数据被策略隐藏
  而非数据不存在——比整条查询 403 或静默过滤更诚实（契约 Forbidden 描述的
  "lacks privacy ceiling" 对查询语义不成立：服务端无法在不查询的情况下预知
  范围内是否存在超限数据）。
- 设计决策（设备身份绑定）：事件持久化的 `device_id` 取自凭据 ID，客户端
  信封里的 `device.id` 仅校验非空。每个采集器一个独立令牌（SPEC story 4），
  令牌即设备身份，可防凭据被盗后伪造其他设备的时间线；未来若需一凭据多设备
  再引入显式绑定表。
- 设计决策（过期不使用 Mongo TTL）：TTL 删除会连撤销状态一起抹掉，违背
  "Owner 能查看撤销状态"；过期改为每次使用时强制拒绝（`token_expired`），
  过期凭据继续留在列表中供 Owner 审计。
- 设计决策（batch 语义边界）：本票交付授权与信封结构校验 + 按 event_id 幂等
  重复（`duplicate`）；`stale_revision`/修订覆盖语义与逐项部分成功的完整
  协议行为属票据 05（其清单明确包含 revision 覆盖测试），避免在本票预建
  半成品修订机制。逐 schema 的 payload 深度校验（payload registry）同样留给
  票据 05。
- 设计决策（Query Token 读取域）：V1 契约中 QueryToken-eligible 的读取端点
  只有 `GET /api/v1/events`；其余 `/api/v1/*` 全部在 Owner Session 全局守卫
  之后，凭据 Bearer 一律 401，满足"不能访问任何写入或管理操作"。
- 已知边界：`shared/event-merge.ts`（心跳合并纯逻辑）及其测试保留，供票据 07
  实时边界重建复用；admin 的隐私规则 CRUD 端点保留（预先存在、本票不动），
  其在新协议下的应用方式由分类控制面票据（09）决定；legacy `private` 事件行
  无法用契约信封表达，读取时排除（契约本就禁止 private 入站）。
- 未实现 429 速率限制与 `COOKIE_SECURE` 生产硬校验（同票据 03，延后处理）。
