# LiveQs

LiveQs is a single-Owner quantified-self system for collecting activity, screen use, health, and spending facts from Windows and Android devices.

The V1 source of truth is `.scratch/live-qs-v1/spec.md`. Components share one repository while retaining independent versions and release channels.

## 快速开始

面向单 Owner 在家庭局域网内把整套系统跑起来。下文以 Owner PC 的局域网 IP `192.168.1.3` 为例，请替换为实际 IP（`ipconfig` 查看）。

### 0. 前提条件

- Windows（Owner 机器）+ [Docker Desktop](https://www.docker.com/products/docker-desktop/)（WSL2 后端，开机自启）
- Node.js 22（WebUI 构建、`contracts`/`server` 开发检查用）
- .NET 9 SDK（构建 Windows 采集器）
- JDK 17 + Android SDK（构建 Android 采集器）
- 局域网内手机/其他设备可访问 Owner PC 的 8787（API）与 8080（WebUI）端口

### 1. 一键启动服务栈

```powershell
cd server
Copy-Item .env.example .env   # 首次运行；把 HASH_SECRET 改成 ≥32 位随机字符串
docker compose up -d
```

`docker compose up -d` 会同时启动三个服务，全部 `restart: unless-stopped`，随 Docker Desktop 开机自启（重启后无需手工拉起）：

| 服务 | 端口 | 说明 |
| --- | --- | --- |
| `mongo` | 27017 | 数据卷 `mongo-data` 持久化全部历史 |
| `server` | 8787 | Express API，容器内连 `mongodb://mongo:27017` |
| `web` | 8080 | nginx 托管 WebUI 构建产物（含 SPA 回退） |

验证：`curl http://localhost:8787/api/v1/owner/status` 返回 `{"initialized":...}` 即 API 正常。

说明：

- `server/.env` 提供密钥等基础配置；compose 为容器强制覆盖 `HOST=0.0.0.0`、`MONGODB_URI=mongodb://mongo:27017/live_qs_smoke` 和 `CORS_ORIGINS`（含 `http://localhost:5173`、`http://localhost:8080`、`http://192.168.1.3:8080`）。更换 LAN IP 时同步修改 compose 中的 `CORS_ORIGINS`。
- 国内网络若 Docker 构建期 `npm ci` 被本机代理/加速器（如 SteamTools 的 MITM 证书）拦截，把对应根证书导出为 `.pem` 放入 `server/npm-ca/` 或 `web/npm-ca/`（已 gitignore）即可；无此问题的机器该目录留空。

### 2. 首次 Owner 初始化（WebUI）

1. 浏览器打开 `http://localhost:8080`（手机用 `http://192.168.1.3:8080`）。
2. 首次访问按提示设置 Owner 密码（仅存 scrypt 哈希，密码不可找回）。
3. **手机浏览器务必到 WebUI 的 设置页 把 API 地址改为 `http://192.168.1.3:8787`**。API 地址保存在浏览器 localStorage，默认值是 `http://localhost:8787`（只在本机有效）。

### 3. 创建设备/查询凭据

Owner 登录 WebUI 后在凭据页创建；令牌明文只在创建时显示一次。Scope 取值（受凭据类型限制）：

| 用途 | 类型 | Scopes |
| --- | --- | --- |
| Windows 采集器 | Device Token | `events:write`、`payment:write`、`rules:read` |
| Android 采集器 | Device Token | `events:write`、`health:write`、`payment:write`、`rules:read` |
| 只读查询 / AI Skill | Query Token | `events:read`、`health:read`、`payment:read`、`context:read` |

Query Token 调用任何写入/修正/管理端点都会被拒绝（401/403）。

### 4. Windows 采集器

```powershell
cd windows
dotnet publish LiveQs.Windows\LiveQs.Windows.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts\win-x64
```

- 运行 `artifacts\win-x64\LiveQs.Windows.exe`，托盘内设置：
  - 服务器地址：本机可填 `http://localhost:8787`，其他机器填 `http://192.168.1.3:8787`
  - 粘贴 Device Token；Owner ID 填 `local`
  - 勾选开机自启与关闭到托盘
- 原始窗口标题只存本地 SQLite；上云的只有标题哈希。本地数据在 `%LocalAppData%\LiveQs\Windows\liveqs.db`。

### 5. Android 采集器

```powershell
cd android
.\gradlew.bat assembleRelease
```

- 安装产物：`android/app/build/outputs/apk/release/app-release.apk`（项目密钥库签名）。
- **签名备份（重要）**：`android/keystore/liveqs-release.jks`、`android/keystore.properties` 与 `android/keystore/BACKUP-IMPORTANT.txt` 均不入库。必须离线备份这三份文件——换机器构建时缺少它们就只能改用 debug 签名，而不同机器的 debug 签名不一致，会导致 APK 无法覆盖安装（只能卸载重装并丢失设备设置与积压队列）。
- 首次启动权限清单：
  - 使用情况访问权限（UsageStats）
  - 通知使用权（监听支付通知）
  - Health Connect 读写授权
  - 电池优化豁免（保持后台采样）
- 设置页 API 地址填 `http://192.168.1.3:8787`（不要用 localhost）。

### 6. 更新通道

Windows 采集器的自动更新清单（`windows/stable` 标签下的 `liveqs-windows-update.json`，票据 17）已实现但处于休眠状态：仓库尚未推送任何 release 标签。验收期间不要打标签/发 release。

### 7. 验收与运维记录

七天验收（票据 18）的每日巡检与重大切换记录在 `.scratch/live-qs-v1/acceptance-record.md`。

## Checks

Install Node dependencies within each component:

```powershell
npm --prefix server ci
npm --prefix web ci
npm --prefix contracts ci
```

Run all checks from the repository root:

```powershell
.\scripts\check.ps1
```

Pass `contracts`, `server`, `web`, `windows`, or `android` to check one component,
for example `.\scripts\check.ps1 contracts`. The repository root is not a Node
package; each component owns its dependencies, version, and release channel.

## Components

- `server/`: Express and MongoDB API
- `contracts/`: OpenAPI 3.1, versioned event schemas, examples, and model generation
- `web/`: Vue Owner dashboard
- `windows/`: WPF collector
- `android/`: Android collector
- `.scratch/`: local V1 specification and tickets

Deprecated directories are read-only references and are not API compatibility targets.
