# 12 — 接入 Android UsageStats

**What to build:** 让 Android 将系统 UsageStats 作为每日应用使用的权威来源可靠上传，并在跨设备时间线与摘要中展示而不重复计算无障碍实时状态。

**Blocked by:** 05 — 打通 Windows 活动纵向链路; 08 — 交付时区与双时间指标.

**Status:** ready-for-agent

- [ ] Android 使用事件 Schema 定义应用包名、区间或日汇总、单位、来源和时区语义。
- [ ] 现有 UsageStats 权限与采集逻辑继续使用，并迁移到稳定事件标识、revision 和 durable outbox。
- [ ] 断网、重复同步和进程重启不会丢失或重复每日应用事实。
- [ ] 规范化策略以 UsageStats 贡献 Android 日应用时长，无障碍观察只贡献实时或上下文信息。
- [ ] WebUI 将 Android 与 Windows 放在同一时间体系中，同时保留设备来源。
- [ ] private 事件在客户端阻止上传，服务端拒绝不获准类型或隐私级别。
- [ ] 与 Android 系统统计对照的每日总量偏差目标不超过百分之五，并记录测量方法。
