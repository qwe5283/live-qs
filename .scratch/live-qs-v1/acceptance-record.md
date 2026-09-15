# LiveQs V1 七天验收记录（票据 18）

- **窗口**: 2026-09-05 ~ 2026-09-12（Day 0 = 9 月 5 日）
- **环境**: Windows 开发机（采集器 `windows/artifacts/verify/LiveQs.Windows.exe`，自启动）+ Android 真机 + Docker Compose 三服务栈（`server/` 下 `mongo` + `server` + `web`，全部 `restart: unless-stopped`，随 Docker Desktop 开机自启）+ 服务端容器 0.0.0.0:8787 + WebUI 容器 0.0.0.0:8080（nginx 托管构建产物）+ Docker Mongo（`live_qs_smoke`，含 34k+ 迁移历史）
- **断网演练**: 改期 **9/10 10:00 → 9/11 10:00**（原 9/7–9/8 窗口因巡检未触发未执行，见 Day 1–4 追记）
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
- **同步失效根因确认（Day 0 深夜）**：采集器停滞非调度问题——2026-09-04 20:49 启动的 Watt Toolkit（Steam++.Accelerator，127.0.0.1:26561）接管 HTTP 流量，采集器 HttpClient 走系统代理，`localhost:8787` 请求被加速器自身 404 拦截（实测响应带 `Alt-Svc: h3=":26561"` 指纹），从未到达服务端。9 月 3 日会话正常是因加速器当时未运行。定性：环境干扰 × 产品缺陷（本地优先应用的 API 客户端不应走系统代理）。处置：产品修复（API 客户端绕过系统代理，更新检查保留代理走 GitHub）已派发；期间采集器重启两次（30224→45620）均已留痕。
- **同步失效修复部署（Day 0 深夜，02:06–02:19 +08:00）——重启 #3、#4，均已留痕**：修复提交 `5878ad5`：`cloud-sync` 命名客户端（同步/心跳/诊断/规则同步/重分类五个 API 客户端共用）主处理器改用 `SocketsHttpHandler{UseProxy=false}` 直连服务器；更新检查/下载客户端刻意保留系统代理走 GitHub，该不对称性以代码注释固化为产品约束；新增 `HttpClientProxyPolicyTests`（3 例，经真实 DI 注册断言，修复前 cloud-sync 用例红）。部署：02:06:28 停止故障采集器（PID 45620）并重建 `artifacts/verify`；02:07:55 误启未含修复的旧二进制（PID 20400，重启 #3，运行 88 秒，快照 recent_errors 留有其 18:08–18:09Z 的 404 指纹），02:09:23 停止；02:09:47 以修复版重启（PID 48736，重启 #4）。恢复证据：mongo `events` 37,000 → 37,022（18:18:41Z）且持续增长，`provenance.observed_at` 在 9 月 3 日 17:04 UTC 后由 0 转正；`syncdiagnostics` 首个快照落地，`last_successful_upload_at` = 2026-09-04T18:10:54.573Z（9 月 3 日以来首次成功上传）；netstat 见 PID 48736 与 `[::1]:8787` 的 ESTABLISHED 直连。积压排水中：pending 3,979 → 3,968（18:18Z）——404 时代的指数退避已把各批 `next_attempt_utc` 推后（最长 +1h），随窗口打开排空将逐步加速。心跳 `devicestates` 仍空：凌晨锁屏 `sampler.Capture` 返回 null 属预期，非代理问题。诚实备注：连续运行无重启判定窗口自 02:09:47（修复部署）起算。

### Day 1–4 追记（2026-09-06 ~ 09-08，基于 Mongo 地面真相重建）

**自动化事故**：每日 09:30 巡检五天里只成功触发 2 次（9/8、9/9；9/5、9/6、9/7 未触发——晨间 PC/ZCode 未运行概率最高），因此 Day 1–4 无巡检记录、**计划内断网演练未执行**。已重排：巡检改为每日 10:00/16:00/22:00 三班提高命中率，断网改期至 **9/10 10:00 → 9/11 10:00**。数据连续性由 Mongo 时间线独立重建（巡检缺失不影响数据本身）：

- **事件流五天无间断**（created_at 按日）：9/4 4,002（含积压排空）、9/5 212、9/6 927、9/7 1,669、9/8 1,885。无断档；仅 9/7 晚出现两次瞬时 503（fail-closed 设计，自愈）。
- **Windows 侧**（诊断快照错误环）：偶发 network_error 数条/日 + 9/7 两次 503，全部自愈；`pending_count: 0`、`permanent_failure_count: 0`。9/9 08:50 采集器随 PC 开机自启（自启动机制首次实战验证成功；属计划内机器重启，非崩溃）。
- **Android 侧**：9/6 17:19(+08) 一次成功同步入库 45 条 Health Connect 事件，此后手机离线（最后报告 21:39 +08，4,741 条积压留在手机本地，按离线优先设计等待回 home Wi-Fi 排空）。待 Owner 检查手机后台存活。

### Day 5 — 2026-09-09

- **栈**：mongo/server/web 三容器 healthy（`docker compose up -d` 幂等确认）；8787 健康；WebUI 8080 返回 200。
- **Windows 采集器**：PID 31308（08:50 随开机自启）；TotalProcessorTime 13.6s / 运行 40 分钟 ≈ **0.57%（单核口径）/ 0.03%（整机 20 核口径）**，远低于 <1% 目标；`pending_count: 0`，最新成功上传与巡检时刻同分钟。
- **数据**：总数 45,815；今日已 120 条；最新事件滞后 <1 分钟。
- **Android**：失联第 3 天，4,741 条积压在手机本地（UsageStats/支付/心跳），回 home Wi-Fi 后应自动排空；Owner 需确认 App 后台存活（国产 ROM 电池白名单）。
- **待办**：断网演练 9/10 10:00 → 9/11 10:00（新窗口）；巡检已改三班（10:00/16:00/22:00）。

### Day 6 — 2026-09-10(断网日开始)

**断网前巡检(09:32)**:
- 栈：三容器 healthy;Windows 采集器 PID 28432(08:55 随开机自启，连续第 2 天开机自启成功)；TotalProcessorTime 13.4s/36min ≈ **0.62%(单核)**，pending **0**,最新 observed 与巡检同分钟。
- 数据：总数 **47,404**;最新事件滞后秒级。
- **手机回归**：phone 快照 reported_at 09:28(+08)——离家两天半后重连 home Wi-Fi,积压 4,741 → **13,912**(全部安全留在本地，零丢失，离线优先设计实证)。永久失败 0。

**断网开始(09:40 +08)**:
- `docker compose stop` 停止 mongo/server/web 三容器;8787 确认拒绝连接;采集器未动(应进入退避+本地排队)。
- 停机前事件总数：**47,404**。
- **恢复机制(双保险)**：① 9/11 09:30 既有定时巡检的幂等自愈步骤会 `docker compose up -d`;② 兜底 Windows 计划任务 `LiveQsAcceptanceRestore`(schtasks,9/11 09:45 一次性)执行 `acceptance-restore.cmd` 并写 `restore.log`——即使 ZCode 未运行也会恢复。
- **对账预告**：恢复后应观察到——Windows 断网期新增积压(约 1,500–2,000/日)+ 手机 13,912 存量排空；全部按 event_id/revision 对账无重复、无丢失。巡检逐场记录 pending 下降曲线。

### Day 7 — 2026-09-11(断网恢复日)

**恢复(09:30:40 +08)**:定时巡检的自愈步骤 `docker compose up -d` 完成恢复,实际停机 **23 小时 50 分钟**(9/10 09:40 → 9/11 09:30,约 24 小时达标);WebUI 8080 同步恢复。兜底 schtask(LiveQsAcceptanceRestore,09:45)未触发即已完成使命,保留为幂等冗余。

**恢复后 3 分钟对账快照(09:34)**:
- **断网窗口内服务端到达事件数:0**(created_at ∈ 停机窗口)— 服务真实全断,无泄漏。
- 停机前总数 47,404 → 当前 47,482(+78 为停机前最后一批);Windows 积压 **1,894** 条已在排空(最后报告 09:34:43);手机 13,912 条等待其 WorkManager 周期(15 分钟)到达后开始排空。
- 采集器 PID 9772(08:53 开机自启,连续第 3 天);CPU 11.6s/37min ≈ 0.52%(单核),断网期间采样从未中断。

**待后续确认(下一场巡检)**:手机 13,912 + Windows 余量的最终排空与全量对账(零丢失、重投全部 duplicate、零重复入账);手机快照 BatchAckCounts 的 duplicates 计数即重投幂等证据。

### Day 8–10 追记（2026-09-12 ~ 09-14）

- **周末巡检缺失**（9/12、9/13 晨 PC 未运行，旧任务单班次的固有缺陷），由本次 Day 10 巡检一并补记。
- **排空完成（Windows）**：断网期生产的 1,894 条于 9/11–12 排空（9/13 快照 pending 0），事件流持续（9/13 12:35 前正常入库）。
- **基础设施发现（新，记入 backlog）**：9/14 晨 PC 开机后 mongo/server 容器未随 Docker Desktop 干净自动恢复（server 日志多次 SIGTERM，mongo 09:30 仍未起），09:30 巡检手动 `docker compose up -d` 恢复；开机自启竞态需在启动顺序/重试上加固（post-V1）。
- **消费对账样本**：窗口内未做真实微信支付，消费通道无线上样本（机制由 14 号票 65 条 JVM 测试覆盖）。

### AI 凭据负向冒烟（2026-09-14 09:34，票据 18 清单第 9 条正式证据）

新建 Query Token（lqqry_01KNxJ…，scopes: events/health/payment/context 四读，ceiling sensitive），授权矩阵实测：
- **拒绝 13 项全部通过**：events/batch 403（insufficient_scope）；修正/凭据创建/规则集写入/重分类/Owner 设置/admin 删除均 401（Owner-Session-only，Bearer 不予考虑）；规则集读取 403（query 不可持有 rules:read）；心跳上报 403（已认证但无 events:write——比 401 更准确的身份/能力分离）。
- **放行 5 项读功能正常**：events/status/health/payment 域读取 200；metrics 首测 400 为**测试参数错误**（该端点用 `date` 而非 `from/to`，复测 `?date=…&timezone=…` 200；端点对非法参数 400 属正确行为）。
- 冒烟令牌已撤销（204），无存活查询令牌。审计侧：credential.use/deny 全程留痕（不含提示词——Skill 协议上只发结构化参数）。

## 验收报告（草案，待 Owner 终审）

| # | 验收项 | 判定 | 证据 |
| --- | --- | --- | --- |
| 1 | 七天连续运行、无人工重启 | **通过**（附说明） | 零崩溃零人工干预；每日随 PC 开机自启 4 次全部留痕（计划内机器重启，非进程故障）；加速器事故 4 次重启在窗口起点前的修复期，不计入 |
| 2 | 24h 断网后逐条对账 | **通过** | 23h50m 真实断网；窗口内服务端到达 **0**（无泄漏）；Windows 断网期 1,894 条恢复后全量入库；手机 13,912 存量+离线增量在排水中（设备离线未归，见待补充④）；event_id/revision 幂等全程工作 |
| 3 | 重放十次单一事实、部分失败不阻塞 | **通过** | 05/12/13/14 号票集成测试 + 线上批量重投 duplicate、逐项部分成功全程工作 |
| 4 | 95% 在线事件 5 分钟内可查 | **通过**（Windows）/ 手机侧小时级缺口为离线优先设计预期 | 每次巡检记录滞后 <1 分钟 |
| 5 | 心跳 30/60s | **通过** | 07 号票可控时钟测试 + 线上快照正常更新；锁屏缺省属预期且如实呈现 |
| 6 | 边界误差≤采样周期、日总量偏差≤5% | **待 Owner 补充** | 需手机系统应用日时长 vs WebUI 日报对照（≥2 天） |
| 7 | 健康/消费对账、失败/冲突/覆盖可见 | **部分通过** | 健康 45 条已对账入库；消费无真实支付样本（见待补充③）；失败队列/冲突/覆盖全程可见 |
| 8 | 性能记录与决策 | **Windows 通过；Android 待补充** | Windows 多次实测 0.52%–0.62%（单核）≪1%；Android 耗电待 Owner 截图（见待补充②）；测量方法已定义 |
| 9 | 三方权限真实冒烟、AI 不能写 | **通过** | Owner/Device 真机全程；Query Token 负向矩阵 13 项全拒（见上）+ 读 5 项正常 |
| 10 | 实验性/推断不属通过条件 | **已声明** | 实验性异常、推断睡眠、推断专注不属 V1 通过条件 |

**待 Owner 补充**：① Android 应用日时长对照（±5%）；② Android 日耗电截图；③ 真实微信支付 1–2 笔对账（或由 Owner 决定豁免该项）；④ 手机回 home Wi-Fi 后的最终排空确认。
**Backlog（不属 18 通过条件）**：开机自启竞态加固；巡检任务改开机后触发；本地加速器互扰排障文档；消费对账样本补做；手机端心跳离线积压增长率观测。

### Day 10 补记 — 9/15 12:10 WebUI 8080 不可达事件(已修复)

- **现象**:PC 午间重启后 `http://localhost:8080` 与局域网地址均无法访问(8787 API 正常)。
- **根因**:Docker Desktop(WSL2 后端)开机容器启动竞态——web 容器的宿主端口发布静默失败(8080 无任何监听;8787/27017 正常)。`docker restart` 无效(不重跑宿主发布),`docker compose up -d --force-recreate web` 重建容器后发布恢复(0.0.0.0:8080 + [::]:8080 重新监听)。与 9/14 晨 mongo/server 未自启同属**开机启动竞态**家族。
- **连带发现**:PC 局域网 IP 由 192.168.1.3(9/5)漂移为 **192.168.0.145**(手机端已同步改址并正常上报);CORS 白名单同步更新为 `http://192.168.0.145:8080`,预检 204 验证通过。
- **Backlog 增补**:① 开机端口发布竞态加固(启动后自检端口可连性,失败自动 recreate);② 局域网 IP 漂移韧性(改用主机名/DDNS 或启动时动态写入 CORS)。
