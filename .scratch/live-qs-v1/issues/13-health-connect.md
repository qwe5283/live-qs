# 13 — 接入 Health Connect

**What to build:** 让 Owner 在 WebUI 查看可追溯的步数、心率和来源睡眠区间，并清楚知道数据来自哪个 Health Connect origin 以及哪些时间范围缺失。

**Blocked by:** 04 — 交付分级凭据管理; 05 — 打通 Windows 活动纵向链路.

**Status:** ready-for-agent

- [ ] 步数、心率和睡眠分别使用版本化 Schema，定义单位、区间和来源字段。
- [ ] 现有 Health Connect 权限流程与采集器继续工作，并使用可靠 outbox 协议。
- [ ] 每个观测保留 data origin，不因值相似或时间重叠而自动删除另一来源。
- [ ] WebUI 展示健康摘要、时间线、来源和缺失覆盖，而不把缺失显示为零。
- [ ] 睡眠只展示来源提供的区间，不把设备空闲推断描述为睡眠事实。
- [ ] Device 和 Query scopes 分别限制健康写入与读取，默认隐私级别符合敏感数据要求。
- [ ] 来源记录数能够与服务端结果对账，非法或未获授权记录进入可诊断失败路径。
