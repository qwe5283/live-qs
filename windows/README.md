# LiveQs Windows

Windows 本地优先的前台应用时间统计工具。应用以单用户托盘进程持续运行，前台窗口采样、查询和维护只依赖本地 SQLite；云同步为可选功能，服务端故障不会中断本地能力。

## 技术与结构

- .NET 9、WPF、C#
- Microsoft.Data.Sqlite，WAL 模式
- Win32 API 前台窗口、空闲时间和全屏检测
- Core：模型与服务契约
- Infrastructure：采样、SQLite、云同步、维护和开机自启
- App：WPF 页面、图表、主题和托盘生命周期

本地数据默认位于 `%LocalAppData%\LiveQs\Windows\liveqs.db`。窗口标题可配置为本地原文、哈希或不记录；云同步始终只发送标题哈希。

## 时间线活动段落

一条活动段落是一组时间上相邻的本地活动记录，合并为一张时间线卡片。段落表达观测到的应用使用连续性，保留底层原始记录；项目、任务或专注需要另有语义依据。

| 情况 | 段落边界 |
|---|---|
| 同一应用连续使用，窗口标题变化 | 合并，标题变化只累计次数 |
| 多个应用短时来回切换 | 合并为「多应用活动」 |
| 同一应用连续使用累计 **≥5 分钟** | 独立成段，与前后其他应用活动分开；按实际非 AFK 记录累计时长 |
| 连续 AFK 状态持续 **≤5 分钟** | AFK 本身不形成边界，前后活动按其余规则合并；预览色带和应用泳道留白，AFK 不计入活跃时长或应用占比 |
| 连续 AFK 状态持续 **>5 分钟** | 切分，AFK 单独成段 |
| 两条记录之间没有记录 **>30 秒** | 切分，中间显示「无活动记录」 |
| 无记录间隔 **≤30 秒** | 不因间隔切分，但轨迹留白，空白时间不计入覆盖或活跃时长 |
| 多应用段落接近 **60 分钟** | 在记录边界拆分；连续单应用长段可以超过 60 分钟 |

AFK 阈值按连续 AFK 段的起止跨度判断，多条相邻 AFK 记录合并后再判断。短 AFK 优先并入前一应用连续段，只有后一段可相邻合并时并入后一段；没有相邻活动可合并时仍单独展示。

段落起点是第一条记录的开始，终点是最后一条记录的结束。跨度包含段内短 AFK 和短间隔；记录覆盖包含已记录的 AFK；活跃时长只累计非 AFK 记录。预览色带、应用泳道和应用时长均排除 AFK，展开原始记录仍可查看 AFK 的起止和状态。

例如同一应用使用 10 分钟、AFK 3 分钟、继续使用 10 分钟，合并为一个跨度 23 分钟、活跃 20 分钟的段落，中间 3 分钟留白。不同应用的长段、无记录缺口和多应用时长上限仍可形成边界。

时间线按所选日期裁剪，单设备前台重叠按较晚开始的记录优先绘图和计时。分页加载更早记录后会重新合并段落，尚未加载完整的最早段落有提示。同一浏览器内从开发页面切到小红书的标题变化，仍按同应用记录折叠。

## 开发

```powershell
dotnet restore LiveQs.Windows.sln
dotnet test LiveQs.Windows.sln
dotnet run --project LiveQs.Windows\LiveQs.Windows.csproj
```

后台启动：

```powershell
dotnet run --project LiveQs.Windows\LiveQs.Windows.csproj -- --background
```

## 发布

```powershell
dotnet publish LiveQs.Windows\LiveQs.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts\win-x64
```

云同步兼容现有服务端的 `POST /api/v1/ingest/events`，使用 Bearer Device Token。同步采用 SQLite outbox、幂等键和指数退避。
