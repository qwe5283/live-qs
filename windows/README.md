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
