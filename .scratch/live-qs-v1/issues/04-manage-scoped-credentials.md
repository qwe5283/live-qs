# 04 — 交付分级凭据管理

**What to build:** 让 Owner 从 WebUI 创建、查看和撤销用途明确的 Device Token 与 Query Token，使每个设备或 Agent 只获得完成任务所需的能力。

**Blocked by:** 03 — 交付局域网 Owner 登录.

**Status:** ready-for-agent

- [ ] Owner 能为凭据设置名称、主体类型、scopes、允许事件类型、隐私上限和到期时间。
- [ ] Token 明文只在创建时展示一次，持久化存储仅包含安全摘要和可识别前缀。
- [ ] Device Token 只能上报获准事件，不能查询个人数据或管理凭据。
- [ ] Query Token 只能读取获准数据域，不能访问任何写入或管理操作。
- [ ] Owner 能查看创建时间、到期时间、最后使用时间和撤销状态，并可立即撤销。
- [ ] 创建、使用、拒绝和撤销行为产生不包含秘密值的审计记录。
- [ ] HTTP 集成测试覆盖 scope 拒绝、事件类型限制、隐私上限、过期和撤销。
