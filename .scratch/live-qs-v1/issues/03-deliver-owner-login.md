# 03 — 交付局域网 Owner 登录

**What to build:** 让唯一 Owner 能在可信局域网首次设置密码、登录受保护 WebUI 并注销，为所有个人数据与管理功能建立清晰的人类认证边界。

**Blocked by:** 02 — 建立可执行契约基础.

**Status:** ready-for-agent

- [ ] 未初始化实例只允许通过受控首次设置流程创建 Owner 密码，不要求用户名。
- [ ] 密码使用内存困难型算法保存，数据库和日志中不存在明文密码。
- [ ] 登录成功后使用可撤销的 HttpOnly、SameSite Session Cookie，WebUI 不在本地存储管理 Token。
- [ ] 注销后原 Session 无法继续访问受保护接口。
- [ ] 未认证请求获得契约规定的拒绝响应，WebUI 返回登录流程而不是泄露数据。
- [ ] 局域网运行支持配置监听地址和明确 CORS 来源；开发 HTTP 与未来 HTTPS Cookie 策略可区分。
- [ ] HTTP 对真实 MongoDB 的测试覆盖初始化、成功登录、错误密码、受保护访问和注销。
