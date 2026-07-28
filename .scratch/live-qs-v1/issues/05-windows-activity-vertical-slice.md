# 05 — 打通 Windows 活动纵向链路

**What to build:** 让一个真实 Windows 前台活动区间从现有采样器进入持久 outbox，经版本化批量协议写入 MongoDB，最终在 Owner Web 时间线中可见。

**Blocked by:** 02 — 建立可执行契约基础; 04 — 交付分级凭据管理.

**Status:** ready-for-agent

- [ ] 前台活动 Schema 明确定义区间、应用标识、AFK、来源、finalized 和 revision 语义。
- [ ] 现有 Win32 采样、SQLite 和托盘生命周期继续工作，协议迁移不要求重写平台适配器。
- [ ] Windows 为逻辑区间保持稳定事件标识，并在区间延长或结束时递增 revision。
- [ ] 批量上传逐项返回 accepted、duplicate、stale revision 或 rejected，允许部分成功。
- [ ] 客户端只在对应 revision 获得确认后移除 outbox 项，永久拒绝不会无限重试。
- [ ] 原始窗口标题和完整执行路径不会出现在上传请求或服务端事件中。
- [ ] Owner 能在 WebUI 时间线看到来自 Windows 的区间、应用、设备和同步时间。
- [ ] 真实 HTTP 与 MongoDB 测试证明重复发送十次仍只有一个最新逻辑事件，旧 revision 无法覆盖新 revision。
