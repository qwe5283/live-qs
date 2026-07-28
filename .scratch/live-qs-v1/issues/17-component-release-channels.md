# 17 — 建立独立组件发布渠道

**What to build:** 让各组件在同一 Monorepo 中独立版本和发布，使 Windows 与 Android 能发现属于自己的新版本，而不依赖仓库级模糊 latest release 或额外 OSS。

**Blocked by:** 06 — 交付同步诊断与失败队列; 07 — 交付跨设备实时状态; 10 — 交付显式历史重分类; 11 — 交付可审计人工修正; 15 — 交付来源优先级与覆盖度.

**Status:** ready-for-agent

- [ ] Windows、Android、Server 和 Web 使用独立组件版本与可识别标签命名。
- [ ] 路径和标签触发只构建、检查和发布相关组件。
- [ ] Windows 与 Android 各有稳定更新清单，包含版本、发布日期、下载地址、SHA-256 和最低兼容版本。
- [ ] 客户端查询自己的更新清单，不使用无法区分组件的通用 latest release。
- [ ] Release Assets 可作为安装包分发来源，不要求新增 OSS 服务。
- [ ] 不同组件先后发布不会使另一客户端误报更新。
- [ ] 清单和制品校验失败时客户端拒绝更新并显示可诊断状态。
