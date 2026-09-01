# 03 — 交付局域网 Owner 登录

**What to build:** 让唯一 Owner 能在可信局域网首次设置密码、登录受保护 WebUI 并注销，为所有个人数据与管理功能建立清晰的人类认证边界。

**Blocked by:** 02 — 建立可执行契约基础.

**Status:** resolved

- [x] 未初始化实例只允许通过受控首次设置流程创建 Owner 密码，不要求用户名。
- [x] 密码使用内存困难型算法保存，数据库和日志中不存在明文密码。
- [x] 登录成功后使用可撤销的 HttpOnly、SameSite Session Cookie，WebUI 不在本地存储管理 Token。
- [x] 注销后原 Session 无法继续访问受保护接口。
- [x] 未认证请求获得契约规定的拒绝响应，WebUI 返回登录流程而不是泄露数据。
- [x] 局域网运行支持配置监听地址和明确 CORS 来源；开发 HTTP 与未来 HTTPS Cookie 策略可区分。
- [x] HTTP 对真实 MongoDB 的测试覆盖初始化、成功登录、错误密码、受保护访问和注销。

## Answer

契约先行在 `contracts/openapi.yaml` 新增 Owner 标签与五个端点：
`GET /api/v1/owner/status`（公开报告初始化状态）、`POST /api/v1/owner/setup`
（仅未初始化时创建密码并建立首个会话，已初始化返回 409）、
`POST /api/v1/owner/login`（密码错误返回 401）、`POST /api/v1/owner/logout`
（服务端撤销会话并清除 Cookie）、`GET /api/v1/owner/session`（WebUI 会话探针）。
错误响应对齐契约 `ErrorResponse`（`error.code`/`error.message` + `request_id`），
四端派生模型已重新生成并通过漂移检查。

服务端以 Node 内置 `crypto.scrypt`（N=16384、r=8、p=1、64 字节密钥，NFKC 规范化）
保存密码，KDF 参数与盐值随哈希入库，数据库与日志无明文密码。会话为服务端管理：
数据库仅存令牌的 SHA-256 哈希，Cookie 为 `liveqs_session`（HttpOnly、SameSite=Lax、
`Secure` 由 `COOKIE_SECURE` 决定、Max-Age 与 `SESSION_TTL_HOURS` 一致）；注销写入
`revoked_at`，旧令牌立即失效，过期会话由 MongoDB TTL 索引清理。人类接口的
`USER_TOKEN` Bearer 认证已移除（ADR-0005），`/api/v1/ingest/*` 的设备令牌不受影响。

WebUI 新增登录与首次设置页面、路由守卫与全局 401 处理：未认证访问受保护页面
重定向到登录/设置流程，任意 API 401 触发回登录页；设置页移除 User Token 输入，
Pinia store 不再保存管理令牌，并清理旧版本遗留的 localStorage 令牌。服务端
`CORS_ORIGINS` 为显式来源允许列表（默认 `http://localhost:5173`，带凭据），
`HOST` 支持配置监听地址。

验证：`npm --prefix contracts run check`；`cd server && npm run typecheck &&
npm test && npm run build`（含 `test/owner-auth.test.ts` 对真实 MongoDB
`mongodb://127.0.0.1:27017/live_qs_test` 的 11 条 HTTP 测试，覆盖初始化、成功登录、
错误密码、受保护访问、注销撤销；Mongo 不可达时显式警告并跳过）；`cd web &&
npm run build`。

## Comments

- 设计决策：选择 Node 内置 `crypto.scrypt` 而非原生 argon2，避免 Windows 原生构建
  风险；scrypt 同为内存困难型 KDF，参数记录在 `kdf`/`kdf_params` 字段中便于未来升版。
- 设计决策：会话文档以令牌哈希作为唯一 `id`，撤销用 `revoked_at` 标记而非删除，保留
  撤销状态供后续凭据管理（票据 04）审计展示。
- 设计决策：数据库未连接时认证中间件一律 503 `service_unavailable`（fail-closed），
  而非伪造 401；无数据库的 app 级测试据此断言 503。
- 设计决策：全局错误响应统一为契约 `ErrorResponse` 形状（含设备令牌 401 与 404），
  使契约拒绝语义在整个 API 一致。
- 未实现 429 速率限制（契约已预留响应定义），属后续安全加固（SPEC 部署边界）。