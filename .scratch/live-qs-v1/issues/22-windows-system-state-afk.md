# 22 — Windows 系统状态证据与连续 AFK

Status: resolved

## Scope

依据 V1 SPEC 的 Windows 本地系统状态补充条款，实现有证据的待机/锁屏缺口合并。

- 实时监听当前用户会话锁屏/解锁与系统挂起/恢复，采样结合会话锁定状态；保留 Win32 采样和托盘生命周期。
- 幂等补查最近 30 天 Windows 系统日志，识别现代待机、传统睡眠/恢复和正常关机/启动；后续增量导入，日志不可读取时采样与本地查询继续工作。
- 系统状态事件单独存储在 SQLite，记录结构化时间、来源、事件种类和稳定标识；不保存日志正文、账户名或额外敏感字段，不上传。
- 配对证据覆盖的缺口与相邻 AFK 合并；未闭合或原因未知的缺口保留。LockApp/LogonUI 不计为应用活跃，原始记录继续可查。
- 分开显示跨度、采样覆盖、系统补充时长及证据；跨日裁剪有延续提示，分页重新合并并保留展开状态。
- 以 2026-10-07 23:46 至 2026-10-08 00:32 的真实形态作回归和只读实测：00:15:11 进入现代待机（Idle Timeout），00:18:14 出现 5 秒 AFK UnlockingWindow，00:32:07 退出待机（Input Mouse），00:32:09–00:32:14 LockApp，随后恢复桌面使用。
- 更新 Windows README 切分表；不引入 Windows 服务，不改写历史活动事实或扩大公共协议。
- 追加：传统睡眠/休眠恢复和开机日志后最多 2 分钟采集恢复窗口，仅补齐未观测时间，首条实际采样提前结束窗口；超出窗口的缺口严格独立展示。现代待机 506 → 507 不预留恢复窗口，退出之后按通常的 30 秒缺口规则处理。界面标题为「采集恢复窗口」。保留事件实际时间，将恢复缓冲与系统事实覆盖分别计量，验证恰好 2 分钟、略超 2 分钟、现代待机例外、跨日及分页。

## Verification

- `.\scripts\check.ps1 windows`，必要时使用独立输出目录。
- 系统事件 XML 解析、重复导入、状态配对、锁屏判定、未知缺口、覆盖计量、跨日及分页回归。
- 读取这台电脑的真实系统日志与 SQLite，在真实 WPF 时间线验证 10 月 7 日/8 日合并和证据展示，保存截图。
- `git diff --check`。

## Answer

已实现 Windows 本地系统状态证据与连续 AFK 投影，更新 [Windows README](../../../windows/README.md) 的切分规则表和系统状态说明。

- 实时监听会话锁屏/解锁与电源挂起/恢复，采样通过 WTS 查询当前会话状态；锁定时继续产生明确 AFK 采样。历史 LockApp/LogonUI 在本地投影中视为非活跃，原始全屏和 AFK 标记仍可查看。
- 首轮导入最近 30 天 `System` 日志，此后每 5 分钟增量补查并重读检查点前 15 分钟。识别 Kernel-Power 506/507、42/107，Power-Troubleshooter 1 的报告时间，以及 Kernel-General 13/12。结构化证据和导入检查点独立存储，稳定标识去重；失败保留原有证据和检查点，时间线显示提示并自动重试。
- 只有同一来源中闭合的状态配对才覆盖缺口。后续新的进入事件取代未闭合的旧开始，运行时来源按进程本次运行区分，防止漏日志或采集器重启将未知时间误认成持续离开。
- 时间线分别显示跨度、采样覆盖和系统补充，重叠证据按时间并集计量；原始记录不改写，系统补充不加入 outbox。仪表盘应用用量和活跃柱图采用同样的非活跃判定，仅累计实际采样。
- 跨日按日期裁剪并提示延续；分页不把未加载的采样当作系统补充，重新合并后保留展开状态及时间选区，包括仅有系统证据的段落。短 AFK ≤5 分钟仍不形成边界，轨迹留白。

### 验证

在仓库根目录执行如下检查，**223 项 Windows 测试通过**，构建 **0 警告、0 错误**：

```powershell
$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = (Join-Path $PWD '.scratch/live-qs-v1/evidence/22-system-state/outputs')
.\scripts\check.ps1 windows
```

回归覆盖真实现代待机形态、事件解析与敏感字段排除、重复导入及重启后保留、读取失败恢复、活动 revision/同步载荷不变、未知和未闭合缺口、重复进入和来源重启、重叠证据不重复计量、局部状态覆盖、仪表盘时长、跨日和分页。旧逻辑在同一真实形态中产生 5 个条目，新投影在 10 月 8 日得到 1 个连续离开段落。

使用真实 SQLite 和 Windows 系统日志进行了本机验证，导入 518 个结构化状态事件。重复导入记录数不变；导入与 WPF 渲染前后，对原始活动 ID 91791–91794 及其同步队列条目的全部字段比对，内容一致。精确结果为：

| 项目 | 本机结果（Asia/Shanghai） |
|---|---|
| 原始 AFK 开始 | 2026-10-07 23:46:13.7509845 |
| 现代待机进入 / 退出 | 2026-10-08 00:15:11.8141379 / 00:32:07.3838736，事件 506 → 507 |
| 10 月 8 日离开段落 | 00:00:00 → 00:32:14.4928394，从前一天延续 |
| 实际采样覆盖 | 923.231 秒，3 条原始记录 |
| 系统补充 | 1009.153 秒 |
| 活跃时长 | 0 秒 |
| 仍未计入覆盖的短过渡 | 2.109 秒 |

00:18 的原始 UnlockingWindow 仅持续 5 秒；LockApp 实际出现在 00:32:09–00:32:14，而非覆盖整个 00:18–00:32。现代待机日志配对仍跨过这次短暂锁屏采样，因此不会因亮屏而误拆段。

真实 WPF 冒烟验证证据模板、跨日提示、浅色和深色窄窗口，无绑定错误；截图仅展示该历史片段：

- [合并后的离开段落](../evidence/22-system-state/historical-merged-light.png)
- [系统证据与无原始采样的选区](../evidence/22-system-state/historical-evidence-light.png)
- [深色窄窗口](../evidence/22-system-state/historical-evidence-narrow-dark.png)
- [前一天的跨日延续](../evidence/22-system-state/historical-previous-day-light.png)
- [本机实测输出](../evidence/22-system-state/smoke-result.txt)

复现程序位于 [SystemStateSmoke](../evidence/22-system-state/smoke/Program.cs)，会将规范化日志证据写入当前用户的本地库，原始活动只读检查；不启动采集或同步。先完成上面的检查，再执行：

```powershell
dotnet build .scratch/live-qs-v1/evidence/22-system-state/smoke/SystemStateSmoke.csproj --artifacts-path .scratch/live-qs-v1/evidence/22-system-state/outputs -p:BuildProjectReferences=false
dotnet .scratch/live-qs-v1/evidence/22-system-state/outputs/bin/SystemStateSmoke/debug/SystemStateSmoke.dll .scratch/live-qs-v1/evidence/22-system-state
```

最终 `git diff --check` 通过。已从检查构建输出 `windows/artifacts/verify/LiveQs.Windows.exe --background` 启动新版后台，确认进程持续运行、本地采样时间推进、日志导入检查点推进且无导入警告，状态表保持 518 个唯一事件。

证据仅在 Windows 本地生效，未扩展公共协议或引入 Windows 服务。手动暂停、退出、异常断电和未闭合日志仍可能留下未知缺口；本次未执行七天连续验收。

### 追加交付：恢复/开机后 2 分钟窗口

按 Owner 追加要求，在睡眠、休眠、现代待机恢复或开机日志之后保留最多 2 分钟采集恢复窗口。

- 窗口从日志原始时间开始，到第一条实际采样或查询时刻提前结束；只补齐未观测时间，不遮盖活跃记录，不修改原始日志或活动事实。第一条采样为 AFK 时同样结束窗口，之后再次缺采样按通常规则处理。
- 恰好 2 分钟仍可合并；超过窗口的剩余缺口严格独立展示，不能再被 30 秒短缺口规则吞并。恢复后 2 分 1 秒才采样时，前 2 分钟并入离开段落，额外 1 秒独立显示「无活动记录」，其后另起活动。
- UI 分开显示「恢复缓冲」及其规则来源，不算入「系统补充」、采样覆盖、应用活跃或仪表盘采样 AFK 总量。重叠恢复报告按时间并集计量。
- 单个恢复/开机日志只支持其后的有限窗口，不解释其前方缺口；解锁事件和实时电源通知不适用日志缓冲。跨日窗口保留前序离开时段的连续性；分页保留窗口段落展开状态，不补齐尚未加载的采样。
- 更新 SPEC 和 Windows README 切分表、计量规则及开机延迟示例。

验证：`.\scripts\check.ps1 windows` **241 项测试通过，构建 0 警告、0 错误**；`git diff --check` 通过。新增边界回归覆盖四种恢复日志来源、90 秒、恰好 120 秒、121 秒、180 秒、窗口后 AFK 分段、已观测活跃保护、首条 AFK 后再次缺口、单个开机事件、查询时刻上限、重叠报告、跨日与分页/展开状态。

检查使用独立输出，避免覆盖当前运行的客户端：

```powershell
$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = (Join-Path $PWD '.scratch/live-qs-v1/evidence/22-system-state/recovery/outputs')
$env:OutDir = (Join-Path $PWD '.scratch/live-qs-v1/evidence/22-system-state/recovery/artifacts/verify') + '\'
.\scripts\check.ps1 windows
dotnet build .scratch/live-qs-v1/evidence/22-system-state/smoke/SystemStateSmoke.csproj --artifacts-path .scratch/live-qs-v1/evidence/22-system-state/recovery/outputs -p:BuildProjectReferences=false
dotnet .scratch/live-qs-v1/evidence/22-system-state/recovery/artifacts/verify/SystemStateSmoke.dll .scratch/live-qs-v1/evidence/22-system-state/recovery
```

实测 10 月 8 日历史片段仍为 00:00:00–00:32:14.4928394 的单段离开，采样覆盖 923.231 秒、系统补充 1009.153 秒；原本未计入覆盖的 2.109 秒唤醒过渡现归为恢复缓冲，未计入覆盖的剩余时间为 0。原始记录和同步队列字段仍一致，活跃时间仍为 0。

真实历史与开机延迟规则样例分别验证，WPF 浅色及深色窄窗口无绑定错误：

- [开机后 2 分 1 秒才采样的规则样例](../evidence/22-system-state/recovery/boot-delay-rule-light.png)
- [深色窄窗口及恢复窗口来源](../evidence/22-system-state/recovery/boot-delay-rule-narrow-dark.png)
- [真实历史的恢复缓冲计量](../evidence/22-system-state/recovery/historical-evidence-light.png)
- [本机实测输出](../evidence/22-system-state/recovery/smoke-result.txt)

新版已生成于 `.scratch/live-qs-v1/evidence/22-system-state/recovery/artifacts/verify/LiveQs.Windows.exe`。当前后台仍运行上一轮的 `windows/artifacts/verify` 构建；自动操作托盘退出入口未成功，未强制结束采样进程。退出当前客户端后，用该新版路径加 `--background` 启动，或重新构建源码再启动，才能在运行界面加载新规则。

### 追加更正：现代待机不预留恢复窗口

按 Owner 最新要求，现代待机 506 → 507 不适用采集恢复窗口，覆盖上述追加交付中包含现代待机的规则。配对状态仍覆盖到 507 原始时刻；之后的未观测区间按通常的 30 秒缺口规则处理，短间隔留白且不计覆盖，超过 30 秒独立显示「无活动记录」。单个 507 同样不生成缓冲。传统睡眠/休眠恢复与开机日志保留既有 2 分钟规则。

界面标题改为「采集恢复窗口」，删除「（最多 2 分钟）」子串；SPEC 和 Windows README 切分表同步明确适用来源。

验证：`.\scripts\check.ps1 windows` **246 项测试通过，构建 0 警告、0 错误**。现代待机回归覆盖恰好 30 秒、31 秒、90 秒、历史来源名称及未配对退出；正例继续验证传统恢复/开机缓冲。真实日志和 SQLite 冒烟验证 10 月 8 日仍为一条 00:00:00–00:32:14.4928394 的离开段落，采样覆盖 923.231 秒、系统补充 1009.153 秒、恢复缓冲 **0 秒**，未计覆盖的短过渡 **2.109 秒**；原始记录及同步队列字段一致。WPF 浅色和深色窄窗口无绑定错误，截图已检查。

复现命令（独立输出，不覆盖运行中的客户端）：

```powershell
$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = (Join-Path $PWD '.scratch/live-qs-v1/evidence/22-system-state/modern-standby/outputs')
$env:OutDir = (Join-Path $PWD '.scratch/live-qs-v1/evidence/22-system-state/modern-standby/artifacts/verify') + '\'
.\scripts\check.ps1 windows
dotnet build .scratch/live-qs-v1/evidence/22-system-state/smoke/SystemStateSmoke.csproj --artifacts-path $env:ArtifactsPath -p:BuildProjectReferences=false
dotnet .scratch/live-qs-v1/evidence/22-system-state/modern-standby/artifacts/verify/SystemStateSmoke.dll .scratch/live-qs-v1/evidence/22-system-state/modern-standby
```

- [缩短后的采集恢复窗口标题](../evidence/22-system-state/modern-standby/boot-delay-rule-light.png)
- [现代待机证据及零恢复缓冲](../evidence/22-system-state/modern-standby/historical-evidence-narrow-dark.png)
- [检查输出](../evidence/22-system-state/modern-standby/check-result.txt)
- [本机实测输出](../evidence/22-system-state/modern-standby/smoke-result.txt)

`git diff --check` 通过。新构建位于 `.scratch/live-qs-v1/evidence/22-system-state/modern-standby/artifacts/verify/LiveQs.Windows.exe`；运行中的客户端需使用该构建重新启动后生效。

### Code review 修复（2026-10-10）

修复本次 review 确认的四项问题：

- 恢复窗口在完整本地采样上下文中确定终点，再交给时间线按日和分页裁剪。排除应用或切换日期不会重新开启已经结束的窗口。
- 采样时已排除的应用仅保存 UTC 采样时刻，和关闭前一活动区间在同一 SQLite 事务中完成；不保存应用、标题或路径，不进入 outbox。时刻标记与活动按相同的保留/删除范围清理。
- AFK 切分与跨日延续使用先解决重叠、再裁剪日期的有效边界，不再使用已被较晚观测取代的原始估计尾部。
- 原始记录与计量切片分别保留，同起点观测即使有效覆盖被裁成零，也能在原始详情中查看；不会重复增加覆盖、活跃时长或变化次数。日期起点处同时间记录仍由 ID 排序和分页。

新增 **12 项回归用例**，先确认原有四项问题在 7 个核心/真实 SQLite 用例中失败，再修复并补充日期边界、排除采样的重启/隐私/删除及跨日延续检查。修正测试临时数据库清理，仅清理当前数据库连接池，避免并行测试互相释放资源。

验证：`..\scripts\check.ps1 windows` **258 项测试通过，构建 0 警告、0 错误**；修改文件的 `dotnet format whitespace/style --verify-no-changes` 与 `git diff --check` 通过。检查使用临时目录，不覆盖运行中的客户端。

使用合成回归数据在实际 WPF 控件中验证两条原始标题均被渲染、两分钟有效 AFK 合并正确，浅色/深色窄窗口无绑定错误；未操作用户数据库或采样进程。

- [同起点原始记录（浅色）](../evidence/22-system-state/code-review-fixes/same-start-raw-light.png)
- [同起点原始记录（深色窄窗口）](../evidence/22-system-state/code-review-fixes/same-start-raw-narrow-dark.png)
- [重叠裁剪后的短 AFK](../evidence/22-system-state/code-review-fixes/superseded-afk-light.png)

复现检查（从 `windows/` 运行）：

```powershell
$reviewFixOutput = Join-Path ([System.IO.Path]::GetTempPath()) 'live-qs-review-fixes-20261010'
$env:UseArtifactsOutput = 'true'
$env:ArtifactsPath = Join-Path $reviewFixOutput 'outputs'
$env:OutDir = (Join-Path $reviewFixOutput 'verify') + '\'
..\scripts\check.ps1 windows
```

### 系统证据删除与保留边界（2026-10-11）

按 Owner 确认的方案，将系统证据纳入本地历史清理。原删除路径只清理活动与排除采样时刻，留下仅系统证据的离开段落；只删除事件行仍会被 15 分钟重读或删除时正在进行的导入恢复，也无法阻止范围外端点重建跨日段落。

- 范围删除按 UTC 左闭右开边界清理 `device_state_events.occurred_utc`，保留清理删除截止时刻之前的系统事件；`device_state_import` 检查点保留，返回数量仍只统计活动记录。
- 同一事务持久化仅含时间边界的 `device_state_deleted_ranges`，重叠与相邻范围合并，保留截止时间只向前推进。实时事件及日志导入在写事务内检查该边界，重启、重复补查与正在进行的导入不能回填已删事件；即使全部事件被过滤，成功补查仍正常推进检查点。
- 手动删除当天或未来日期时，防回填范围截至执行时刻并包含该时刻，后续正常采集不受影响。旧版本未保存历史删除边界，残留证据需重新执行范围删除。
- 系统证据和删除边界从同一 SQLite 查询快照读取，时间线和仪表盘不跨删除范围重新配对；无法安全闭合的系统状态按未知处理，范围外完整配对继续可查。恢复窗口在首个删除边界终止；即使缺口不超过 30 秒，段落也不跨删除范围合并。
- 更新 SPEC、Windows README、删除确认与范围说明；仅系统证据被清理时也报告完成，数量明确为活动记录数。本地删除不修改服务端历史或 Windows 自身的事件日志。

新增 **12 项真实 SQLite 回归用例**，覆盖两条清理路径、物理删除、检查点保留、导入竞争与重启、跨日端点、短缺口、恢复窗口、仪表盘一致性、保留边界、范围合并和删除执行时刻。先确认初始 9 项失败，再修复；更新原有 2 项排除采样删除测试，使被删除的开机证据不再生成恢复段落。

验证：`..\scripts\check.ps1 windows` 通过 **270 项测试**，构建 **0 警告、0 错误**；全解的 whitespace/style 检查及 `git diff --check` 通过。使用合成数据在实际 WPF 设置页执行删除，确认仅系统证据的日期变为空状态、浅色和深色提示正确、无绑定错误。

- [删除前的系统证据段落](../evidence/22-system-state/deletion/before-delete-light.png)
- [删除后的空时间线](../evidence/22-system-state/deletion/after-delete-light.png)
- [清理结果与范围说明（浅色）](../evidence/22-system-state/deletion/settings-delete-light.png)
- [清理结果与范围说明（深色）](../evidence/22-system-state/deletion/settings-delete-dark.png)

从 `windows/` 复核：

```powershell
..\scripts\check.ps1 windows
dotnet format whitespace LiveQs.Windows.sln --no-restore --verify-no-changes --exclude LiveQs.Windows.Core/Generated
dotnet format style LiveQs.Windows.sln --no-restore --verify-no-changes --exclude LiveQs.Windows.Core/Generated --diagnostics IDE0011 IDE0005 IDE0161 IDE0065
git diff --check
```
