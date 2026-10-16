# 23 — Windows Dashboard 窗口恢复刷新

Status: resolved

## Scope

窗口重新显示、从最小化恢复或重新成为活动窗口且当前停留在 Dashboard 时，刷新当前日期范围的数据。

- 复用 Dashboard 现有的异步查询、日期范围及错误处理。
- 合并同一次恢复产生的窗口事件，已有查询进行中时不重复查询。
- 时间线和设置页面不触发 Dashboard 查询。
- 窗口事件刷新采用无感异步加载，已有快照时保留旧数据、日期和显示状态直至新查询成功；首次加载、切页返回及日期切换保持空态加载效果。
- 保留托盘、采集及同步生命周期。

## Verification

- Windows 组件检查。
- 实际 WPF 窗口冒烟：隐藏后显示、最小化恢复、失焦后激活、其他页面、已有查询及日期范围保留。

## Answer

`MainWindow` 在可见性恢复、从最小化恢复及 `Activated` 时安排 Dashboard 刷新。使用 Dispatcher 合并同轮窗口事件，并在安排与执行时检查当前页面、窗口是否可见／最小化以及 Dashboard 是否正在查询，避免重复加载或导航之后启动旧页面查询。刷新复用现有 `DashboardView.RefreshAsync()`，保留日期选择和原有查询／错误处理。

验证：

- `.\scripts\check.ps1 windows` 通过：270 项测试，构建 0 警告／0 错误。
- `dotnet run --project .scratch/live-qs-v1/evidence/23-dashboard-window-refresh/smoke/WindowRefreshSmoke.csproj -c Release` 通过：首次加载仅一次、无激活的隐藏后显示、托盘恢复的显示／激活合并、实际焦点切换、最小化恢复、时间线／设置页隔离、自定义日期保留、已有查询不重复及排队后切页检查。
- `git diff --check` 通过。

冒烟运行实际 `MainWindow` 和页面，通过独立临时 SQLite 和合成查询结果验证；未启动采集／同步服务或访问个人活动数据库。页面布局没有变化。

## Comments

2026-10-16：窗口事件刷新改为无感异步加载。原先共用的 `RefreshAsync()` 无条件把 `HasSnapshot` 置为 false，导致旧统计隐藏并显示转圈空页。现在仅窗口事件传入 `preserveSnapshot: true`；已有快照时保留旧数据、日期与状态文字，新结果通过现有请求版本检查后再更新。后台刷新失败保留旧统计并明确显示上次结果，沿用原有错误提示。首次加载、切页返回、日期切换和重试继续使用默认空态加载。

通过挂起实际窗口查询，冒烟命令 `dotnet run --project .scratch/live-qs-v1/evidence/23-dashboard-window-refresh/smoke/WindowRefreshSmoke.csproj -c Release -- --loading` 在修复前稳定失败于“旧内容应可见且不显示转圈”的检查，修复后通过，同时验证旧数据／状态文字保留、重复窗口事件不重查、新结果更新，以及切页回来的转圈空态与查询完成后的展示。

运行中的 Debug 客户端占用了默认可执行文件，改用 `Configuration=Release` 执行 `.\scripts\check.ps1 windows`，270 项测试通过，构建 0 警告／0 错误；`git diff --check` 通过。

实际页面截图使用合成查询结果：

- [窗口后台刷新时保留旧统计](../evidence/23-dashboard-window-refresh/background-refresh.png)
- [切页返回时保留空态加载](../evidence/23-dashboard-window-refresh/navigation-loading.png)
