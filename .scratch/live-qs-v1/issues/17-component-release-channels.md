# 17 — 建立独立组件发布渠道

**What to build:** 让各组件在同一 Monorepo 中独立版本和发布，使 Windows 与 Android 能发现属于自己的新版本，而不依赖仓库级模糊 latest release 或额外 OSS。

**Blocked by:** 06 — 交付同步诊断与失败队列; 07 — 交付跨设备实时状态; 10 — 交付显式历史重分类; 11 — 交付可审计人工修正; 15 — 交付来源优先级与覆盖度.

**Status:** resolved

- [x] Windows、Android、Server 和 Web 使用独立组件版本与可识别标签命名。
- [x] 路径和标签触发只构建、检查和发布相关组件。
- [x] Windows 与 Android 各有稳定更新清单，包含版本、发布日期、下载地址、SHA-256 和最低兼容版本。
- [x] 客户端查询自己的更新清单，不使用无法区分组件的通用 latest release。
- [x] Release Assets 可作为安装包分发来源，不要求新增 OSS 服务。
- [x] 不同组件先后发布不会使另一客户端误报更新。
- [x] 清单和制品校验失败时客户端拒绝更新并显示可诊断状态。

## Answer

本票交付 ADR-0002 的落地：一个 Monorepo、六个活跃组件（contracts、
server、web、skill、windows、android）各自独立版本、独立标签、独立
发布与更新通道，无仓库级模糊 latest release，无新增 OSS。

**CI 与发布拓扑（清单第 1、2 项）**：`.github/workflows/` 下 12 个自包含
工作流，每组件一对。CI 工作流按组件路径过滤（`<component>/**` + 自身
工作流文件），push main 或 PR 只构建/检查受影响组件——contracts 变更
只跑 contracts 检查，而其 `check:generated` 漂移门禁本就覆盖五个生成
目标，生成模型失同步在 contracts CI 即被拦截。发布工作流由标签
`<component>/vX.Y.Z` 触发，只发布对应组件，并在发布前校验标签与组件
版本源一致（csproj `<Version>`、`versionName`、package.json）。各组件
版本源：windows csproj `<Version>0.1.0</Version>`、android
`versionName`、其余 npm package.json，均为 0.1.0 起点。

**更新清单（清单第 3 项）**：`scripts/release/generate-manifest.mjs` +
`manifest.mjs` 是生产端——发布工作流对制品就地计算 SHA-256（清单永远
与制品字节一致），生成 `manifest_version/component/version/released_at/
download_url/sha256/min_compatible_version` 七字段清单并自校验
（`buildManifest` 拒绝非法输入，含 min > version 的不可能组合）。
`windows/release.config.json` 与 `android/release.config.json` 持有
`min_compatible_version`（0.1.0），发布工作流读取。

**分发与稳定频道（清单第 5 项）**：版本化 Release（`windows/v0.2.0`）
携带自包含 win-x64 zip / debug 签名 APK + 清单资产；同时把清单资产
`--clobber` 上传到常驻 `windows/stable` / `android/stable` 频道 Release
（标记 `--prerelease`，永不污染 GitHub 的 latest 语义）。频道资产 URL
就是客户端默认轮询的唯一稳定地址，资产名固定，发布即覆盖。全部走
GitHub Release Assets，无新增 OSS。

**客户端检查（清单第 4、6 项）**：Windows 新增 `LiveQs.Windows.Core/
Update`（模型、核心 semver 比较、严格解析器、评估器、哈希）与
`LiveQs.Windows.Infrastructure/Update`（AppVersion 取入口程序集版本、
HTTP 客户端、6 小时周期的 UpdateCheckWorker、JSON 状态存储、状态服务）；
只评估 `UpdateComponents.Windows`，默认清单地址为 windows/stable 资产
（可在设置中改）。Android 新增 `com.ailife.android.update` 包（同构的
解析/评估/检查器 + WorkManager 12 小时周期 + 检查后状态持久化），只评估
android 组件。**隔离性质**在三层钉死：(a) 结构上只有 windows-release /
android-release 会写各自的 stable 资产，server/web/contracts/skill 发布
根本不产出任何客户端清单；(b) 评估器把组件不符判为拒绝
（`manifest_component_mismatch`），即使误喂他组件清单也不会误报；
(c) `manifest.test.mjs`（14 条）、Windows `UpdateTests`/`UpdateWorkerTests`、
Kotlin `UpdateEvaluatorTest`/`UpdateCheckerTest` 用同一组已知向量互相
钉死三份实现的决策语义（semver 数值比较、同版本不重装、严格解析、
隔离、min-compatible 边界）。

**拒绝与可诊断（清单第 7 项）**：全部失败走稳定码——
`manifest_fetch_failed`、`manifest_parse_failed`、
`manifest_component_mismatch`、`min_compatible_not_met`、
`artifact_download_failed`、`artifact_hash_mismatch`。Windows：托盘新增
`UpdateAvailable` 蓝色图标态（仅 SHA-256 校验通过后才出现；云不可达 >
更新提示的既有优先级不变），设置页新增"更新"选项卡展示状态、上次检查
时间、已校验安装包路径与错误码；只有校验通过的包会留在
`updates/` 目录，重启不重复下载，安装永远由 Owner 手动触发。下载物流式
计算哈希，不符即删临时文件并拒绝。Android：状态页"应用更新"卡片展示
状态与稳定码，结果持久化到状态文件；**V1 仅通知不下载不安装**——Owner
点"打开下载页"手动安装，因此不存在未知来源安装流。

验证：`npm --prefix contracts run check`；`cd server && npm run typecheck
&& npm test && npm run build`（190 通过）；`cd web && npm run build`；
`cd skill && npm run typecheck && npm test && npm run build`（22 通过）；
`cd windows && dotnet test LiveQs.Windows.Tests/LiveQs.Windows.Tests.csproj
-p:BaseOutputPath=artifacts/check/`（117 通过，新增 49 条）；
`cd android && ./gradlew.bat lint test assembleDebug`；`node --test
scripts/release/manifest.test.mjs`（14 通过）。

## Comments

- 恢复说明：本票实现由前一次被模型 API 网络错误中断的会话遗留的未提交
  工作树恢复而来。恢复会话对全部新增/修改文件做了逐文件审计，补齐了
  六处缺陷（generate-manifest 引用不存在的文档、Kotlin 解析器对字符串
  `"manifest_version"` 的宽松、DashboardScreens import 乱序、Windows
  worker 把关机取消误报为检查失败、哈希不符之外的异常留下 `.download`
  半截文件、大安装包复用 100 秒短超时客户端），并新增关机传播与字符串
  manifest_version 拒绝的测试向量。最终以上述验证全绿后提交。
- 设计决策（标签与频道命名）：`<component>/vX.Y.Z` 作版本标签、
  `<component>/stable` 作常驻频道标签。tag 名带组件前缀使任何组件的
  发布在仓库标签命名空间里自解释，且频道 Release 用 `--prerelease`
  标记，GitHub 的"最新 Release"逻辑永远轮不到它——客户端没有可用
  latest 端点，是结构性的而不是纪律性的。
- 设计决策（清单七字段）：票面五字段（版本、发布日期、下载地址、
  SHA-256、最低兼容版本）之外加了 `manifest_version`（schema 版本，
  未来格式演进的可判别入口，未知版本拒绝）与 `component`（组件身份，
  隔离性质的载体：清单自己声明属于谁，评估器据此拒绝错组件清单）。
  解析端对未知字段一律拒绝，清单没有藏未校验内容的地方。
- 设计决策（三份实现而非共享代码）：Node 工具、C#、Kotlin 各自实现同一
  评估语义——客户端安装包无法依赖 Node 工具链，更新清单也不是 OpenAPI
  协议的一部分（它是发布通道载荷，ADR-0002 域）。漂移风险由三端
  已知向量测试互相钉死控制，任何一端改语义都会先红。
- 设计决策（Windows 下载与校验一体）：SHA-256 在下载流内计算，校验通过
  才落正式文件名并写入状态存储，因此"可安装"与"已校验"不可分；不同
  HTTP 客户端分短超时（清单）与 10 分钟（安装包），大包慢链路不会误报
  校验失败。检查周期固定（Windows 6 小时、Android 12 小时 + WorkManager
  约束），V1 不开放配置。
- 设计决策（Android 仅通知）：Android 侧静默下载 APK 再诱导安装会引入
  未知来源权限流与自动安装语义，V1 票面只要求"发现属于自己的新版本"，
  故检查到 AVAILABLE 即止，下载交给 Owner。若未来要应用内升级，评估器
  与清单协议无需改动，只需扩展下载器。
- Owner 运维步骤（当前仓库无远端发布触发，工作流处于休眠状态，代码与
  文档即交付）：(1) 在 GitHub 建 `qwe5283/live-qs` 远端（客户端默认
  清单 URL 与测试向量均按此仓库写死）并 `git push`；(2) 之后每次发布
  单个组件：升该组件版本源 → `git tag <component>/vX.Y.Z` → push 标签
  （可大陆网络代理 push）；工作流自动测试、打包、创建版本化 Release、
  生成清单并覆盖 `<component>/stable` 频道资产；(3) Windows/Android
  客户端在下一个检查周期（或手动"检查更新"）发现新版本；(4) 首次真实
  发布后建议做一次手动冒烟：核对 Release 资产 SHA-256 与清单一致、
  客户端托盘/状态页呈现 AVAILABLE；(5) 提升 `min_compatible_version`
  时改对应 `release.config.json`，低于它的旧客户端将显示 INCOMPATIBLE
  并提示先手动升级，永远不会自动装坏。
- 已知边界与后续：Android 发布 APK 为 debug 签名（release notes 已注明，
  正式签名需 Owner 配置 keystore 后扩展工作流，属发布运营而非协议）；
  客户端默认清单 URL 硬编码 `qwe5283/live-qs`，换仓库需改两处常量；
  `min_compatible_version` 目前由 config 手工维护，未做"自动取上一
  发布"的推导；真实端到端升级流只能在首个真实 Release 运行后冒烟验证
  （本票交付以确定性测试覆盖全部判定与拒绝路径）；`check.ps1` 的
  windows 构建步骤仍指向 `windows\artifacts\verify`（既有行为，live
  冒烟环境验证时请用 `-p:BaseOutputPath=artifacts/check/` 直跑测试，
  本票提交未改动该步骤）。
