# 20 — Windows 分层时间线

Status: resolved

## Scope

按 Owner 确认的设计，将 Windows 本地时间线调整为「活动段落 → 应用泳道 → 原始记录」。

- 默认折叠标题和短时多应用切换，显示起止、活跃/覆盖时长、应用构成、切换色带及两种变化次数。
- 连续同应用使用（包括频繁标题变化）形成一个段落；连续至少 5 分钟的单应用使用独立展示；其他相邻短时应用切换聚合为最多约 60 分钟的多应用段落，只在记录边界拆分。
- 超过 5 分钟的连续 AFK 与超过 30 秒的无记录间隔分段；不超过 5 分钟的 AFK 不单独形成边界，在应用轨迹中留白，原始记录与覆盖时长保留。无记录不冒充 AFK；短间隔保留轨迹留白和覆盖时长差异。
- 泳道共享真实时间轴；点击轨迹或选择五分钟选区查看原始记录，完整保留本地标题、秒级时间和状态。
- 查询按日裁剪，重叠前台区间以较晚开始的记录优先计时和绘图；原始区间保持可查看。
- 保留现有游标分页、异步查询与虚拟化；跨页重建段落并保留展开状态，最早未完整加载的段落提示其边界尚不完整，提供加载更多按钮。
- 支持现有浅色/深色主题、空数据状态与窄窗口；不自动推断开发任务或专注。
- 本次仅修改本地展示，无契约、采样、持久化或同步变更。

领域术语缺口记录：本票的「活动段落」（TimelineEpisode）仅是 Windows 展示投影，不是 CONTEXT.md 的 Event 或新的历史事实；项目 Subject 分组待本地查询提供已记录的分类结果后扩展。

## Verification

- `.\scripts\check.ps1 windows`。
- 用确定性样例验证连续全屏使用、密集切换、同应用标题变化、AFK、缺口、查询裁剪、重叠计时与跨页重组。
- 渲染真实 WPF 页面并验证段落展开、选区、浅色/深色、空数据和窄窗口，保存截图。
- `git diff --check`。

## Answer

- 默认时间线使用活动段落，展示应用构成、按真实时间绘制的切换色带、活跃时长、两类变化次数，以及未完整加载的边界提示。连续应用标题变化折叠，AFK、长时间单应用使用和无记录间隔独立展示。
- 展开段落显示共享时间轴的应用泳道，原生色块 Tooltip 展示具体观测区间；点击轨迹打开对应五分钟选区的原始记录，也可用下拉框选择。原始窗口标题、起止、秒级时长、AFK/全屏/音频状态和本地类别继续可查看。
- 段落和原始记录模板按展开状态创建；保留虚拟化、后台查询及游标分页。跨页重组去重，保留展开状态、时间选区和滚动位置，始终提供手动加载入口。
- 展示计算集中在 Core 的 TimelinePresentation，裁剪查询范围、处理前台重叠、保留真实间隔，并为界面返回展示投影；无持久化、采样、同步、契约或云端分类变更。

验证：`.\scripts\check.ps1 windows` 通过，**148 项测试通过**（新增 15 项），构建零警告、零错误；`git diff --check` 通过。真实 WPF 冒烟验证折叠/展开、泳道绑定与比例宽度、轨迹点击、下拉选区、原始记录、窄窗口、浅色/深色、缺口和空数据，无绑定错误。截图样例使用独立的内存活动记录，没有启动采集或同步服务。

- [默认段落浅色](../evidence/20-timeline/collapsed-light.png)
- [展开应用泳道](../evidence/20-timeline/expanded-light.png)
- [原始记录与时间选区](../evidence/20-timeline/raw-records-light.png)
- [窄窗口深色](../evidence/20-timeline/expanded-narrow-dark.png)
- [默认段落深色](../evidence/20-timeline/collapsed-dark.png)
- [缺口](../evidence/20-timeline/gap-light.png)
- [空数据](../evidence/20-timeline/empty-light.png)

随后启动验证后的客户端并打开时间线，现有数据首批 500 条原始记录显示为 18 个活动段落；其中一段 14 分钟的多应用活动包含 63 条原始记录。通过单独的 WPF 视图只查询现有 SQLite，保留了 [现有数据界面](../evidence/20-timeline/live-data.png)，截图中原始记录保持折叠。更新后的客户端保持运行在时间线页。

## Comments

2026-10-10 Owner 采纳标题图标设计：图标放在卡片内 Title 文本前，使用固定 20 × 20 图标槽，垂直居中并保留 8 px 间距。无活动记录使用 EyeOff24 Regular，离开电脑使用 DrinkCoffee24 Regular，多应用活动使用 Apps24 Regular；单应用复用 Dashboard 的本地图标与通用回退。状态图标使用次要文字色，应用图标保留原色，左侧时间轴保留现有布局。

2026-10-10 Owner 追加：持续不超过 5 分钟的 AFK 不再因离开电脑切开前后活动，预览轨迹与应用泳道中间留白；超过 5 分钟仍独立成段。活跃时长、应用占比和变化次数排除 AFK，原始记录继续可查看；现有单应用长段、无记录间隔、约 60 分钟与按日裁剪规则保留。将更新后的段落切分规则表写入 `windows/README.md`。

2026-10-10 Owner 采纳改造：活动段落独立卡片嵌入滚动内容流，左侧纵轴保持连续；取消固定底部的手动加载按钮，将进度、失败重试、全部加载提示及弱化的手动兜底入口放入列表末尾。接近底部及不足一屏时自动补页，失败后停止自动重试，保留虚拟化、展开状态、选区及滚动位置。

2026-10-09 Owner 追加：活动段落左边的起止时间改成类似 Git 的时间轴。左侧采用连续竖轴和起止圆点，结束时间在上、开始时间在下；段落展开后轴线随内容延伸，相邻段落保持共轴。继续使用现有主题颜色、虚拟化、分页及展开交互。

2026-10-09 Owner 追加：把滚动条放到页边距外侧。将时间线列表的滚动条移到右侧页面留白、靠近窗口边缘，保留卡片的页面边距及内容内边距；继续使用列表自身的 ScrollViewer，保留虚拟化、滚动事件和分页位置恢复。

### 追加改动交付

- 活动段落的起止时间分别对齐纵轴圆点，结束时间在上，开始时间在下；段落内使用强调色轴线，相邻段落以中性轴线连接，展开时随内容高度延伸。
- 卡片保留 28 px 页面边距和 18 px 内容内边距；滚动条位于右侧独立的页面留白，靠近窗口边缘。列表保留自身 ScrollViewer、像素滚动、虚拟化及分页事件，没有增加嵌套滚动容器。
- `.\scripts\check.ps1 windows` 通过：148 项测试，构建零警告、零错误；`git diff --check` 通过。
- `dotnet run --project .scratch/live-qs-v1/evidence/20-timeline/smoke/TimelineSmoke.csproj -p:BuildProjectReferences=false -- --git-axis` 通过；实际 WPF 控件验证滚动条位于卡片外、拖动滚动、虚拟化、展开/折叠、原始记录选区、窄窗口、浅色/深色、缺口及空数据，无绑定错误。使用已经检查过的项目程序集，避免覆盖正在运行的客户端文件。
- [折叠段落与纵轴](../evidence/20-timeline/git-axis/collapsed-light.png)、[展开轨迹](../evidence/20-timeline/git-axis/expanded-light.png)、[浅色外侧滚动条](../evidence/20-timeline/git-axis/raw-records-light.png)、[深色窄窗口外侧滚动条](../evidence/20-timeline/git-axis/raw-records-narrow-dark.png)。

### 2026-10-10 内容流卡片与内联分页交付

- 移除固定背景卡片，每个活动段落在列表模板内使用独立卡片；卡片随像素滚动移动并在视口边缘裁剪，左侧时间轴继续贯穿段落，右侧滚动条保留独立页面留白。
- 分页状态作为虚拟列表最后一项显示，取消固定底栏和悬浮加载框；加载时显示内联进度，失败时保留已加载内容并提供「重试」，加载完毕显示「已显示当天全部记录」。正常状态的手动入口使用弱化文字按钮作为末尾兜底。
- 距底部 240 px 内及内容不足一屏时自动补页；请求完成后再次检查布局，支持首批记录折叠成单个段落时连续加载。失败后停止自动重试，隐藏页面不自动加载。
- 分页开始前保存滚动偏移及日期，结束后在布局稳定时恢复，避免分页提示高度变化引起跳动；跨页仍保留展开状态和原始记录选区，旧日期的分页失败不会影响新日期。

验证使用独立输出目录：176 项 Windows 测试通过（新增 3 项分页失败/重试、迟到失败及并发加载回归），构建 0 警告、0 错误，`git diff --check` 通过。

### 2026-10-10 日期查询回到顶部

Owner 追加：切换日期重新查询时滚动条回到顶部。时间线在完整查询开始时清除分页的位置恢复并回顶，首批结果完成布局后再次确认顶部；查询版本隔离已排队的旧分页恢复，加载更早记录继续保留当前位置。

### 2026-10-10 短 AFK 段内留白交付

- 连续 AFK 段跨度不超过 5 分钟时并入相邻活动，恰好 5 分钟也合并；同应用前后长段可跨短 AFK 合并，短时多应用切换继续聚合。超过 5 分钟的连续 AFK 仍单独成段；单应用长段、超过 30 秒的无记录间隔和多应用约 60 分钟的边界保留。
- AFK 原始记录保留，记录覆盖包含已记录的 AFK；活跃时长、应用构成、预览色带、应用泳道与变化次数排除 AFK。包含短 AFK 的活动段落明确显示 AFK 时长，点击留白选区仍可查看原始 AFK 状态。
- `windows/README.md` 增加活动段落定义、更新后的完整切分规则表、时长口径及示例；同步 V1 SPEC。

验证：在独立输出目录执行 `.\scripts\check.ps1 windows`，**193 项测试通过**（新增 17 项短 AFK 回归），构建零警告、零错误；`git diff --check` 通过。回归覆盖 1 秒、3 分钟、恰好 5 分钟、5 分钟零 1 秒、连续多条 AFK、短间隔、缺口、同应用长段、多应用分配、查询两端和跨页合并。

使用 [WPF 冒烟程序](../evidence/20-timeline/short-afk/smoke/Program.cs) 和独立内存样例验证真实时间线页面：同应用使用 10 分钟、AFK 3 分钟、再使用 10 分钟，展示一个跨度 23 分钟、活跃 20 分钟的段落；预览与泳道中间留白，原始 AFK 记录可展开查看，浅色和深色窄窗口无绑定错误。程序使用已检查的程序集，没有启动采集或同步。

- [浅色展开与短 AFK 留白](../evidence/20-timeline/short-afk/expanded-light.png)
- [原始 AFK 记录](../evidence/20-timeline/short-afk/raw-records-light.png)
- [深色窄窗口](../evidence/20-timeline/short-afk/expanded-narrow-dark.png)

复现截图：

```powershell
dotnet build .scratch/live-qs-v1/evidence/20-timeline/short-afk/smoke/ShortAfkSmoke.csproj --artifacts-path .scratch/live-qs-v1/evidence/20-timeline/short-afk/outputs -p:BuildProjectReferences=false
dotnet .scratch/live-qs-v1/evidence/20-timeline/short-afk/outputs/bin/ShortAfkSmoke/debug/ShortAfkSmoke.dll .scratch/live-qs-v1/evidence/20-timeline/short-afk
```

复现前须将 Windows 项目测试构建到同一个 `--artifacts-path`，供冒烟程序引用。

### 2026-10-10 卡片标题图标交付

- 卡片内标题前增加固定 20 × 20 图标槽，与文字垂直居中、间距 8 px；无活动记录、离开电脑、多应用活动分别使用 EyeOff24、DrinkCoffee24、Apps24 的 Regular 图标，颜色随主题的次要文字色变化。
- 单应用段落复用 Dashboard 的 `ApplicationIconConverter`，按非 AFK 记录中最近可用的程序路径显示应用原色图标；路径缺失、不可用或提取失败时使用已有通用应用图标。短 AFK 中的前台程序路径不会影响活动应用图标。
- 保留卡片标题文字、窄窗口省略显示、展开/折叠及左侧时间轴；同步 SPEC。变更仅影响 Windows 本地展示。

验证：独立输出目录执行 `.\scripts\check.ps1 windows`，**193 项测试通过**，构建零警告、零错误；`git diff --check` 通过。真实 WPF 冒烟覆盖三种 Regular 状态图标、可提取的应用图标、缺失路径的通用回退、跨短 AFK 的程序路径选择、20 px 图标槽、8 px 间距、垂直对齐、浅色折叠及深色窄窗口展开，无绑定错误。

- [浅色四类标题图标与通用回退](../evidence/20-timeline/title-icons/collapsed-light.png)
- [深色窄窗口与展开轨迹](../evidence/20-timeline/title-icons/expanded-narrow-dark.png)

截图使用独立内存样例和已验证的应用程序集，未启动采集或同步。复现前将 Windows 项目测试构建到下面的 `--artifacts-path`，然后执行：

```powershell
dotnet build .scratch/live-qs-v1/evidence/20-timeline/short-afk/smoke/ShortAfkSmoke.csproj --artifacts-path .scratch/live-qs-v1/evidence/20-timeline/title-icons/outputs -p:BuildProjectReferences=false
dotnet .scratch/live-qs-v1/evidence/20-timeline/title-icons/outputs/bin/ShortAfkSmoke/debug/ShortAfkSmoke.dll .scratch/live-qs-v1/evidence/20-timeline/title-icons --title-icons
```
