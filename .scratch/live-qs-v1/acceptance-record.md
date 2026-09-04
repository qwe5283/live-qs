# LiveQs V1 七天验收记录（票据 18）

- **窗口**: 2026-09-05 ~ 2026-09-12（Day 0 = 9 月 5 日）
- **环境**: Windows 开发机（采集器 `windows/artifacts/verify/LiveQs.Windows.exe`，自启动）+ Android 真机 + Docker Compose 三服务栈（`server/` 下 `mongo` + `server` + `web`，全部 `restart: unless-stopped`，随 Docker Desktop 开机自启）+ 服务端容器 0.0.0.0:8787 + WebUI 容器 0.0.0.0:8080（nginx 托管构建产物）+ Docker Mongo（`live_qs_smoke`，含 34k+ 迁移历史）
- **断网演练**: 9 月 7 日 21:00 停服务（定时），9 月 8 日 21:00 自动恢复，随后对账
- **判定基线**: 票据 18 十条验收清单；SPEC 性能目标 Windows <1% 后台 CPU、Android <3% 日耗电；实验性异常/推断睡眠/推断专注不属通过条件

## 测量方法

| 指标 | 方法 |
| --- | --- |
| Windows 采集进程 CPU | 每日巡检：`Get-Process LiveQs.Windows` 的 TotalProcessorTime 相邻两次巡检差值 ÷ 间隔秒数 ÷ 逻辑核数 ≈ 平均后台 CPU 占比（采样粒度 ~24h，辅以会话内即时抽查） |
| Windows 连续运行 | 巡检进程存在性；崩溃/重启必须留痕（时间 + 现象），静默重启视为失败 |
| 查询新鲜度 | 当日事件 `MAX(provenance.observed_at)` 与当前时间差；目标 95% 在线事件 5 分钟内可查 |
| 心跳新鲜度 | `GET /api/v1/status` 的 `age_seconds`；正常 ≤30s，停止后 60s 内离线 |
| 队列排空 | 同步诊断面板 pending 计数随时间下降至 0（断网恢复后重点观察，不碰数据库） |
| Android 日总量偏差 | Android 设置/数字健康中应用日时长 vs WebUI 日报，偏差 ≤5% |
| 健康对账 | Health Connect 记录数 vs 服务端 accepted+duplicate 计数（`BatchAckCounts`） |
| 消费对账 | 真实微信支付笔数/金额 vs WebUI 消费页事实（Owner 提供账单数字） |
| 权限负向 | Query Token 调用全部写入/修正/管理端点必须 401/403 |

## 每日记录（巡检自动追加 + 人工补充）

### Day 0 — 2026-09-05

- 启动序列完成：main 推送远端（`10e15db..585271d`）；采集器重建并启动（PID 30224）；服务端以 `node dist/src/main.js` 稳定模式运行；WebUI 5173。
- **发现缺陷 [已修]**：`server` 的 `npm start` 指向不存在的 `dist/main.js`（产物实际在 `dist/src/main.js`），start 脚本从未可用。修复提交 `585271d`。
- 首日推送触发 GitHub Actions 首轮运行，状态待观察。
- Owner 待办：Android 真机安装 APK 并授权（UsageStats / 通知监听 / Health Connect / 电池优化豁免）；确认微信支付通知在通知栏可见。
- **持久化栈切换（Day 0 深夜，01:35 +08:00）**：为解决裸 `node`/`vite` 进程不随重启存活的问题，`server/compose.yaml` 扩展为 `mongo + server + web` 三服务（全部 `restart: unless-stopped`），新增 `web/Dockerfile`（多阶段构建 → nginx:8080，含 SPA 回退）。切换前核对 `events.countDocuments` = **37000**（01:34），停掉裸 node（127.0.0.1:8787）后 `docker compose up -d`，切换后复测 = **37000**（零丢失，mongo 容器未重建，数据卷 `server_mongo-data` 原样保留）。验证全通过：容器三服务 healthy；Owner 登录（POST /api/v1/owner/login）204；WebUI 8080 加载 + SPA 回退 200；来自 `http://192.168.1.3:8080` 的 CORS 预检放行（Allow-Origin + Allow-Credentials）；用采集器自身 Device Token 发一次心跳（204，仅临时投影、不入历史）证明容器接受采集器凭据。01:45 停掉旧 Vite（5173）进程。
- **切换时发现（非切换引入）**：采集器自 9 月 3 日 17:04 UTC 起无成功同步，`sync_queue` 积压 3936 条，今日日志 0 字节、`devicestatuses` 为空——即切换前裸 node 时代也已停滞（今日审计日志仅 23:36 的凭据创建记录）。排查方向：SyncWorker 退避时间戳 / HeartbeatWorker 采样返回 null（AFK/锁屏）。待 Owner 白天观察队列是否自行排空。
- **网络备注**：Docker 构建容器内访问 npmmirror/npmjs 被本机 SteamTools（Watt Toolkit）MITM 证书拦截（宿主机信任其根证书，容器不信任）。`server/Dockerfile` 与 `web/Dockerfile` 现支持可选 CA：将 `.pem` 放入 `server/npm-ca/` 或 `web/npm-ca/`（已 gitignore，本机已放置 SteamTools 根证书）即可在构建期信任。
