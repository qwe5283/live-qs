# 19 — Windows Dashboard 活跃时间分布

Status: resolved

## Scope

根据 Owner 请求调整 Windows 本地统计页：第 2 行左侧新增「活跃时间占比」柱状图，右侧为「应用时间占比」；「应用排行」独占第 3 行并横跨两列。

- 单日（包括自定义单日）显示 0–23 点共 24 个小时桶。
- 近 7 天、近 30 天及自定义两天以上范围按本地自然日分桶，保留零值日期。
- 非 AFK 时长按小时/日界拆分并裁剪到查询范围；柱高显示活跃时长，纵轴单日使用分钟、多日使用小时，悬停显示日期/小时和活跃时长。
- 支持现有浅色/深色主题、空数据状态和较长日期范围。
- 使用现有 SQLite 查询，不改变采集、同步或公共协议。
- 追加请求：应用时间环形图和图例最多显示排行前 5 名，保留原始占比、全部应用总时长及完整排行。
- 追加请求：指针停留在非零柱子上时，Tooltip 显示完整时间段及包含秒的具体活跃时长，相邻柱子切换时更新。
- 追加请求：应用排行中昵称前的圆点替换为应用图标，使用本地记录的程序路径，读取失败时显示通用应用图标，昵称修改不影响图标。
- 追加请求：应用时间占比添加「其他」图例，颜色与环形图底色一致；前 5 名使用不同颜色，图表、图例及排行进度条对应一致。

## Verification

- Windows 组件检查。
- 对时间分桶、AFK/排除应用、查询边界和夏令时行为做回归验证。
- 渲染实际 WPF 页面，保留单日、多日及主题截图。

## Answer

- Dashboard 第 2 行左侧为活跃时间柱状图，右侧为原应用时间环形图；应用排行横跨第 3 行。排行进度条使用自动行高及主题背景色。
- SQLite 的同一次 Dashboard 查询返回活跃分桶，ViewModel 随日期查询和预设一起更新图表。单日为 24 个小时桶，多日保留范围内每个本地日期，包括零值日期。
- 时段按真实 UTC 边界裁剪，AFK 和已排除应用不计入；小时桶合并夏令时重复小时，缺失小时保留零桶，比例分母使用真实时段长度。常规单日/周/月范围自动适配卡片宽度，更长区间可横向滚动。
- 图表使用主题颜色，悬停显示日期/小时、活跃时长和比例；保留现有空数据状态。

验证：`.\scripts\check.ps1 windows` 通过，132 项测试通过，构建 0 警告、0 错误；`git diff --check` 通过。

新增分桶测试覆盖跨小时、跨午夜、查询边界、2/7/30/40 天区间、AFK、排除应用、空数据、完整小时和半小时夏令时切换，以及统计总数与分桶总数一致。

截图使用独立临时 SQLite 的合成活动数据，加载实际 DashboardView 和应用主题，通过 WPF RenderTargetBitmap 渲染；未使用个人活动数据库或启动采集/云同步服务。

- [单日浅色](../evidence/19-dashboard/day-light.png)
- [近 7 天](../evidence/19-dashboard/week-light.png)
- [近 30 天深色](../evidence/19-dashboard/month-dark.png)
- [自定义两天](../evidence/19-dashboard/custom-two-days.png)
- [窄窗口深色图表](../evidence/19-dashboard/day-narrow-dark.png)
- [空数据](../evidence/19-dashboard/empty-light.png)

## Follow-up: 环形图最多显示前 5 名

2026-10-07：ViewModel 提供独立的 `TopApps`（排行前 5 项），环形图和图例共同绑定该集合。完整排行仍绑定全部 `Apps`；保留原始占比和总活跃时长，剩余比例显示为环形图底色。

验证：当前运行中的客户端占用默认输出目录，因此使用独立目录执行以下等价 Windows 检查，132 项测试通过，构建 0 警告、0 错误；`git diff --check` 通过。

```powershell
dotnet test windows/LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=D:/Projects/FreeCoding/live-qs/windows/artifacts/top-five-check/
dotnet build windows/LiveQs.Windows/LiveQs.Windows.csproj -o windows/artifacts/top-five-verify
```

使用独立临时 SQLite 的 7 个示例应用渲染实际 WPF 页面，确认环形图和图例各有 5 项，完整排行仍有 7 项，前 5 名保留占全部活跃时长的 93.1%。

- [前 5 名浅色](../evidence/19-dashboard/top-five-light.png)
- [前 5 名深色](../evidence/19-dashboard/top-five-dark.png)

## Follow-up: 纵轴改为活跃时长

2026-10-07：柱状图的纵轴、柱高及悬停提示统一显示活跃时长。单日以分钟标注（常规刻度 0、15、30、45、60 分钟）；周、月和自定义多日以小时标注（常规刻度 0、6、12、18、24 小时）。超出常规范围的时长会扩展为整数时间刻度，例如夏令时重复小时累计超过 60 分钟时不会截断。副标题改为「每小时活跃时长」或「每日活跃时长」。

使用独立输出目录验证，132 项测试通过，构建 0 警告、0 错误；`git diff --check` 通过。

```powershell
dotnet test windows/LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=D:/Projects/FreeCoding/live-qs/windows/artifacts/time-axis-check/
dotnet build windows/LiveQs.Windows/LiveQs.Windows.csproj -o windows/artifacts/time-axis-verify
```

已使用独立示例数据渲染并检查单日、周及月视图，分钟/小时标签完整可见，环形图及图例仍仅包含前 5 项，完整排行保留全部应用。

- [单日分钟轴](../evidence/19-dashboard/time-axis-day-light.png)
- [单日深色](../evidence/19-dashboard/time-axis-day-dark.png)
- [周视图小时轴](../evidence/19-dashboard/time-axis-week-light.png)
- [月视图深色](../evidence/19-dashboard/time-axis-month-dark.png)

## Follow-up: 柱子悬停提示时间段及具体数值

2026-10-07：柱子使用 WPF 原生 Rectangle 元素，各自预先绑定 Tooltip，替换自绘图表上依赖鼠标移动临时赋值的提示逻辑。原生命中区域只覆盖非零柱子，提示显示小时或自然日的完整起止时间段，以及包含秒、必要时包含毫秒的活跃时长。跨午夜和跨年时显示结束日期；毫秒舍入会正确进位到分钟和小时。首次悬停延迟 250ms，相邻柱子切换使用原生快速显示窗口；离开柱子、刷新数据或卸载图表时关闭旧提示。

使用独立输出目录验证，132 项测试通过，构建 0 警告、0 错误；`git diff --check` 通过。

```powershell
dotnet test windows/LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=D:/Projects/FreeCoding/live-qs/windows/artifacts/chart-tooltip-check/
dotnet build windows/LiveQs.Windows/LiveQs.Windows.csproj -o windows/artifacts/chart-tooltip-verify
```

WPF 冒烟检查验证了实际柱子元素的命中、空白区域不命中、小时/自然日区间文本、秒和毫秒值、跨年午夜边界、舍入进位、模式切换和空数据刷新。示例页面与原生 Tooltip Popup 分别渲染后按柱子位置合成预览，未移动桌面指针或读取个人数据。

- [小时区间 Tooltip 浅色](../evidence/19-dashboard/tooltip-hour-light.png)
- [自然日 Tooltip 深色](../evidence/19-dashboard/tooltip-day-dark.png)

## Follow-up: 应用排行显示应用图标

2026-10-07：应用排行昵称前的彩色圆点改为 20px 应用图标。SQLite 本地查询将已有的程序路径传递到本地统计模型，按应用保留查询范围内最近一次非空路径；修改昵称不会改变图标来源。WPF 转换器通过 Windows Shell 提取图标并缓存已成功读取的图标，复制并冻结位图后释放原生图标句柄。路径为空、程序不存在或图标提取失败时显示通用应用图标；路径及图标保持在本机。

使用独立输出目录验证，133 项测试通过，构建 0 警告、0 错误；`git diff --check` 通过。

```powershell
dotnet test windows/LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=D:/Projects/FreeCoding/live-qs/windows/artifacts/app-icons-check/
dotnet build windows/LiveQs.Windows/LiveQs.Windows.csproj -o windows/artifacts/app-icons-verify
```

新增 SQLite 回归测试覆盖最近非空路径、昵称修改、空路径应用，以及分页/完整本地查询返回一致的路径。独立 WPF 冒烟检查验证真实图标提取、冻结图像、路径大小写不敏感的缓存、空路径和已卸载程序的默认图标；实际 Dashboard 浅色/深色渲染包含 5 个真实应用图标和 2 个默认图标，确认修改昵称的浏览器仍显示正确图标。截图使用独立示例活动数据库。

- [应用图标浅色](../evidence/19-dashboard/app-icons-light.png)
- [应用图标深色](../evidence/19-dashboard/app-icons-dark.png)

## Follow-up: 其他图例及前 5 名独立配色

2026-10-07：应用时间占比添加「其他」图例，直接绑定环形图的 `TrackBrush`，使图例颜色与剩余占比的底色保持一致。前 5 名按排行使用蓝、橙、绿、紫、红五种不同颜色；同一组显示数据用于环形图、应用图例和排行进度条，避免应用标识哈希造成的颜色重复。应用时长、原始占比、排行顺序及本地图标信息保持原值。

使用独立输出目录执行 Windows 检查，133 项测试通过，构建 0 警告、0 错误；`git diff --check` 通过。

```powershell
dotnet test windows/LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj -p:BaseOutputPath=D:/Projects/FreeCoding/live-qs/windows/artifacts/share-legend-check/
dotnet build windows/LiveQs.Windows/LiveQs.Windows.csproj -o windows/artifacts/share-legend-verify
```

WPF 冒烟检查覆盖 7 个应用、3 个应用、空数据及重新查询，确认前 5 项颜色互不相同，且时长、占比、顺序及程序路径不变。实际浅色/深色页面验证应用图例与排行进度条对应一致，并采样环形图各色块及剩余底色的实际像素；「其他」与底色共用画刷，底色修改时图例随之更新。截图使用独立示例活动数据库。

- [其他图例及独立配色浅色](../evidence/19-dashboard/share-legend-light.png)
- [其他图例及独立配色深色](../evidence/19-dashboard/share-legend-dark.png)
