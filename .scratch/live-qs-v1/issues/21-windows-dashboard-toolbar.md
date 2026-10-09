# 21 — Windows Dashboard 日期筛选顶栏

Status: resolved

## Scope

根据 Owner 确认的布局，精简 Windows 本地 Dashboard 顶栏。

- 日期范围入口在前，「今天／近 7 天／近 30 天」分段选择固定在最右侧；日期文字长度不移动分段选择，窄窗口整组换行。
- 复用 WPF Calendar 的 SingleRange 封装日期范围弹层，两次点选起止，支持同日、反向、跨月／年和键盘操作；Esc 或点击外部放弃未完成选择。
- 快捷范围和完整自定义范围自动查询，无常驻日期框和查询按钮。
- 查询放在后台；连续切换只有最新请求可以更新页面，旧错误不弹窗，当前错误提供重试入口。
- 保持本地自然日边界、现有图表、采集和同步行为。

## Verification

- Windows 组件检查及日期范围／查询并发回归。
- 实际 WPF 控件冒烟：鼠标与键盘、跨月／年、同日、取消、固定对齐、窄窗口、浅／深色和绑定错误检查。
- 使用合成数据保存页面及日期弹层截图。

## Answer

顶栏使用右对齐的日期入口与 RadioButton 分段选择。日期入口在前，其文字向左扩展，分段选择在今天、近 30 天及跨年范围下保持相同位置；内容宽度小于 700 时筛选整组移动至标题下方。

`DateRangePicker` 复用 WPF Calendar / SingleRange 和 Popup，封装两次点选、悬停范围预览、同日、反向、跨月／年与键盘选择。完整范围通过事件一次提交；Esc、关闭弹层及卸载页面取消未完成选择。保留原生月份／年份导航，使用应用主题和 WPF-UI 图标字体。

Dashboard 查询在后台执行，取消旧请求并按请求版本拒绝迟到的结果与错误。加载或失败时隐藏旧统计，当前错误可重试。滚动预设在跨午夜刷新时移动，历史自定义范围保持原日期；本地日界和图表统计口径保持原实现。

验证：

- `Configuration=Release .\scripts\check.ps1 windows` 通过：159 项测试、构建 0 警告／0 错误。使用 Release 配置避免覆盖正在运行的 Debug 采集器。
- 新增 11 项 Dashboard 回归，覆盖日界／滚动预设、完整自定义提交、跨年标签、迟到结果／错误、加载状态及失败重试。
- `dotnet run --project .scratch/live-qs-v1/evidence/21-dashboard-toolbar/smoke/DashboardSmoke.csproj -c Release` 通过：实际 WPF 控件的鼠标与键盘事件、跨月导航、同日／反向／跨年、取消、固定对齐、窄窗口、主题、字体及文字颜色，无绑定错误。
- `git diff --check` 通过。

截图由实际 DashboardView、DateRangePicker 和应用主题渲染，使用独立合成数据，没有访问个人活动数据库或启动采集／同步服务。

- [今天／浅色](../evidence/21-dashboard-toolbar/today-light.png)
- [近 30 天／浅色](../evidence/21-dashboard-toolbar/month-light.png)
- [自定义／浅色](../evidence/21-dashboard-toolbar/custom-light.png)
- [跨年／浅色](../evidence/21-dashboard-toolbar/cross-year-light.png)
- [近 7 天／深色](../evidence/21-dashboard-toolbar/week-dark.png)
- [窄窗口／深色](../evidence/21-dashboard-toolbar/narrow-dark.png)
- [日期弹层／浅色](../evidence/21-dashboard-toolbar/picker-light.png)
- [日期弹层／深色](../evidence/21-dashboard-toolbar/picker-dark.png)
- 顶栏裁剪：[浅色](../evidence/21-dashboard-toolbar/toolbar-light.png)、[深色](../evidence/21-dashboard-toolbar/toolbar-dark.png)、[窄窗口](../evidence/21-dashboard-toolbar/toolbar-narrow-dark.png)。

## Comments

2026-10-10：按 Owner 要求保留 `Calendar24` 图标，将日期入口改为 WPF-UI `FontIcon`，通过 `char.ConvertFromUtf32((int)SymbolRegular.Calendar24)` 设置字形。当前 WPF-UI 4.3.0 的 `SymbolExtensions.GetString()` 把整数原始字节直接按 UTF-16 解码，不能正确转换 `U+F0285`；显式生成代理对绕过该缺陷，继续使用原有图标字体和主题颜色。

Windows 组件检查通过：159 项测试，构建 0 警告／0 错误。实际 WPF 冒烟增加渲染字符检查，确认日历字形为有效代理对、码点 `U+F0285`，原有日期选择、固定对齐、窄窗口及主题检查全部通过，无绑定错误。浅／深色日期入口截图已人工核对：[浅色](../evidence/21-dashboard-toolbar/calendar-icon-light.png)、[深色](../evidence/21-dashboard-toolbar/calendar-icon-dark.png)。
