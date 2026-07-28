# 01 — 建立 Monorepo 基线

**What to build:** 将现有组件纳入一个可统一理解和验证的 Monorepo，同时保留各组件独立版本与已经可用的平台代码，为后续跨端契约变更提供原子提交边界。

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] 根目录成为唯一仓库边界，现有组件源码和有效历史不会因整合而丢失。
- [x] Server、Web、Windows 和 Android 仍可分别执行其现有构建或检查命令。
- [x] 根级入口能够说明或执行各组件的标准检查，不要求所有组件共享同一版本号。
- [x] 建立 V1 领域词汇，并记录单 Owner、Monorepo、服务端权威和无旧 API 兼容等已批准架构决策。
- [x] 生成物、本地数据库、密钥和平台私有配置不会进入版本控制。

## Answer

根目录现为统一 Git 仓库，并通过 `legacy/windows-main` 保留迁移前的 Windows `main` 历史。根 workspace 提供全量和分组件检查，四个活动组件均已验证。领域词汇、架构决策和统一忽略规则已加入仓库；Windows 嵌套 Git 元数据已移出工作区。
