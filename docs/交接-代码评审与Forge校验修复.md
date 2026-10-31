# 交接文档：代码评审与修复记录

> 最近更新：2026-10-25（第三批）
> 范围：`src/MoonLauncher` 全部源码（约 17,100 行，78 个文件）
> 关联文档：[`发布与接手.md`](发布与接手.md) · [`微软登录.md`](微软登录.md) · [`功能对照与路线图.md`](功能对照与路线图.md)

## 〇、改动总览

| 批次 | 内容 | 状态 |
|---|---|---|
| 第一批 | Forge / NeoForge 安装器下载增加 SHA-1 校验 | 已编译；实机验证 5 步**待做**（见第一节） |
| 第二批 | 安全/正确性/资源修复 9 项 + 液态玻璃 + 断网暂停续传 | 已编译（0 警告 0 错误），未逐一实机验证 |
| 第三批 | 整合包详情页按 MC 版本排序 + Content/UI 深入评审（9 条新发现） | 排序改动已编译；9 条新发现**未修**，见第四节 |

三批改动相互独立，均为纯新增/局部修改，可单独回滚（回滚方式见各节）。

---

## 一、第一批：Forge / NeoForge 安装器 SHA-1 校验

**问题**：`Installer.Forge.cs` 中安装器 jar 下载后不做任何哈希校验即解压并执行其内 processors。processors 是以用户权限运行的任意 Java 代码，原信任链仅有 TLS 一层；镜像被劫持或 CDN 投毒时即中招。

**修法**：Maven 仓库每个构件旁挂 `.sha1` 副车文件（镜像 BMCLAPI 的 maven 代理同样提供）。下载 jar 前先取副车，把 sha1 喂给 `DownloadItem.Sha1`——下载器本就会在落盘前校验哈希，不符自动删临时文件并换下一个来源。

**改动文件**：`src/MoonLauncher/Game/Installer.Forge.cs`

- `PrepareForgeAsync`：URL 算出后新增 `FetchInstallerSha1Async(url, ct)`，下载项由 `new DownloadItem(url, path)` 改为 `new DownloadItem(url, path, sha1)`。
- 新增 `FetchInstallerSha1Async`：先按镜像设置竞速拉取（复用 `Http.GetStringAsync` 的 validate 重载），失败再强制官方源；两路都拿不到合法的 40 位十六进制校验值则**拒绝安装**（宁可装不上，不跑来源不明的安装器）。
- 新增 `LooksLikeSha1File`：校验副车内容是 `<40位hex> 文件名` 而非镜像出错时常见的 200+网页。

**回滚**：`git checkout -- src/MoonLauncher/Game/Installer.Forge.cs`（如已建仓库）；改动为纯新增、不触碰其他逻辑。

**实机验证清单**：

1. ~~`.\build.ps1` 编译通过~~ ✅ 已完成。
2. 装一个 Forge 版本（如 1.20.1-47.2.0），确认正常。
3. 装一个 NeoForge 版本（如 1.21.1-21.1.x），确认正常。
4. 断网测试：应报"拿不到 SHA-1 校验值，已停止安装"，而非静默继续。
5. 老 Forge（如 1.12.2、1.7.10）各装一个，确认 `.sha1` 副车在老路径下同样存在（Forge 老 maven 亦提供）。

---

## 二、第二批：P0/P1/P2 修复 + 两项新功能

环境：用 dotnet-install 脚本把 .NET SDK 8.0.426 装到项目内 `.dotnet\`（已加入 `.gitignore`），`build.ps1` 会优先使用它；Release 编译 0 警告 0 错误。以下改动**均已编译通过、未逐一实机验证**。

### 2.1 评审问题修复（9 项）

| 原评审# | 改动 | 文件 |
|---|---|---|
| P0-2 | 外置登录新增 `RequireSecureApi`：非本地地址一律要求 https，http 明文直接抛错（回环 / 10.x / 172.16-31.x / 192.168.x / 169.254.x / fc00::/7 内网自建站放行）；`ResolveApiAsync` 所有返回点 + `LoginAsync` / `SelectProfileAsync` / `RefreshAsync` / `ValidateAsync` 均已接线 | Accounts/YggdrasilAuth.cs |
| P0-3 | 核实导出 .bat 处**已有**确认弹窗（InstancePage.ExportScript_Click），无需再改 | — |
| P0-4 | `MaskToken` 正则补上老版本 `--session`（1.5.2 及更早写法） | Game/LaunchService.cs |
| P0-5 | 整合包解压无上限（zip 炸弹）：解压总计 8GB / 单文件 2GB / 10 万条目 / 超 100:1 压缩率，超限拒绝整个包 | Content/ModpackImporter.cs |
| P0-6 | CurseForge API Key 明文存 config.json：改 DPAPI CurrentUser 加密存储（`dpapi:` 前缀），旧明文读到即自动迁移 | Core/Settings.cs |
| P1-11 | Java ≤8 游戏输出按系统 ANSI（GBK）解码，9+ 仍 UTF-8，修日志乱码与崩溃分析失配 | Game/LaunchService.cs |
| P1-16 | `GameRepo.ListVersionIds` 逐级 try/catch，单个坏目录不再让版本列表全空 | Game/GameRepo.cs |
| P1-19 | Mojang Java 运行时文件清单下载后先验自身 sha1 再解析，不符即中止 | Game/JavaManager.cs |
| P1-20 | `ForgeContext` 改为 finally 释放：安装失败不再泄漏 installer jar 和临时目录 | Game/Installer.Core.cs |
| P2-21 | 游戏进程在 Exited 里 `Dispose`；标题守望循环改为对 `HasExited` 访问容错 | Game/LaunchService.cs |
| P2-23 | 启动时后台清理 Temp 残留（`.oltmp` / `forge-*` / `modpacks` / `launch-*.txt`，已知模式 1 小时、其余 24 小时） | Core/Paths.cs + App.xaml.cs |
| P0-8 | Settings 全部读路径加锁，`GetNode` 改为返回快照 `Clone()`，消除后台线程并发踩枚举 | Core/Settings.cs |

### 2.2 新功能（2 项）

| 功能 | 说明 | 涉及文件 |
|---|---|---|
| 液态玻璃（仿 macOS / iOS） | `UI/Glass.cs` 用 `SetWindowCompositionAttribute` 开亚克力模糊（失败回落普通模糊），运行时替换 App 级资源（半透明卡片/窗口底、渐变玻璃描边、半透明主题色标题栏），设置 → 个性化即时开关（`ui.glass`，默认关）；配套苹果式动画：BackEase 弹性页面切换、弹窗缩放回弹、Toast 上浮、按钮悬停微放大/按压回缩；**个性化菜单内置效果调试**：四个滑杆（卡片透明度/窗口背景透明度/描边亮度/背景图浓度）+ 亚克力/普通模糊开关，拖动即时重刷 | UI/Glass.cs（新增）、Core/Settings.cs、MainWindow.xaml.cs、UI/Styles.xaml、Pages/SettingsPage.xaml(.cs) |
| 断网暂停 / 联网续传 | `Core/NetWatch.cs` 探测连通性（网卡 + 国内外双 HTTP 探针）；下载中网络类异常不再记为失败，任务挂起显示"等待网络恢复…"，联网后自动继续；`FetchToFile` 支持 HTTP Range 断点续传（临时文件按任务固定命名，进度保留在盘上；416 交由校验兜底） | Core/NetWatch.cs（新增）、Core/Downloader.cs |

### 2.3 按需求回退的改动

内容搜索列表的加载器徽章、详情页文件列表排序两项改动已回退，恢复 provider 原始顺序。（第三批又重新引入了"整合包按版本排序"，见第三节，仅作用于整合包。）

---

## 三、第三批：整合包详情页排序

**背景**：Mod 详情页与整合包详情页共用 `ProjectPage`，均按**发布日期新→旧**排序。对 Mod 来说"新发布 ≈ 适配新版本 MC"，恰好表现为高版本在上；对整合包不成立（老 MC 版本的维护更新发布日期也很新），顶部出现的可能是低版本文件。应需求：整合包详情页改为**按支持的 MC 版本从高到低**排列。

**修法**：`ProjectPage.Apply()` 中，当 `_type == ContentType.Modpack` 时，按文件标注的最高 MC 版本（`ContentService.CompareGameVersion`）降序排列；未标注版本的文件排最后；版本相同再按发布日期新→旧。**Mod 详情页行为不变**。

**改动文件**：`src/MoonLauncher/UI/Pages/ProjectPage.xaml.cs`（新增 `HighestMcVersion` 辅助方法 + Apply 内排序分支）

**验证状态**：已编译（0 警告 0 错误）。**实机验证待做**：分别打开一个 Modrinth 整合包和一个 CurseForge 整合包的详情页，确认列表顶部为最高 MC 版本、未标注版本的文件在底部。

---

## 四、第三批评审：Content / UI 深入（9 条新发现，均未修）

此前评审对 UI 仅为抽样；本轮深入 `Content/` 全部与 `UI/Pages/` 全部，新发现如下。已剔除误报候选（对每一条都做了源码级核实）。

| # | 级别 | 问题 | 位置 | 建议修法 |
|---|---|---|---|---|
| N1 | P1 | 游戏运行中可再次启动：启动任务结束后启动按钮恢复可用，`AttachSession` 直接覆盖 `_session` 且不退订旧会话的 `Line`/`Exited`——两个游戏日志混写同一缓冲区；游戏 1 退出时会把游戏 2 的运行卡片隐藏、误弹崩溃分析、错误 `Show()` 主窗口；"结束游戏"也只杀最新会话 | UI/Pages/LaunchPage.xaml.cs `LaunchAsync`/`AttachSession`/`OnGameExit` | 启动前 `_session != null` 时禁止或先确认；至少退订旧会话事件、按会话区分日志与运行卡片 |
| N2 | P1 | 版本重命名两个问题：① 只改被重命名版本自己的 `id`，不更新其他版本 json 里 `inheritsFrom: "<旧id>"` 的引用，继承它的子版本重载后即损坏；② 先 `Directory.Move` 再改 json，任一步失败留下"文件夹已改名但 id 未改"的半成品且无回滚，弹出的"重命名失败"与实际状态相反 | UI/Pages/InstancePage.xaml.cs `Rename_Click` | 先改 json 再 Move；扫描并同步更新所有 `inheritsFrom==旧id` 的版本；失败按逆序回滚 |
| N3 | P2 | 下载/更新静默删除他人文件：下载成功路径无条件 `File.Delete(dest)` 再 `File.Move`，若目标文件名被**另一个项目**（未入索引、手动放入）占用会被静默删除；`UpdateAsync` 更新 `.disabled` 的 Mod 时同样 | Content/ContentService.cs `InstallWithReportAsync`、Content/LocalMods.cs `UpdateAsync`、Core/Downloader.cs L187 | 替换前查 ContentIndex 归属，非本项目先提示或移回收站 |
| N4 | P2 | POST 候选链首个 4xx 即终止：`PostJsonAsync` 遇第一个候选返回 4xx 立即抛错，不尝试其余候选；Modrinth 的 POST 走镜像优先，镜像对 `/version_files` 类端点返回 404 时，本地文件识别 / Mod 更新检查 / 整合包导出会整体失败（官方源可用也没用），且该 4xx 不计入镜像失败统计 | Core/Http.cs `PostJsonAsync`、Content/ModrinthProvider.cs `PostAsync` | 对 4xx 也继续下一个候选，仅在官方地址认定为终态（对齐 `GetAsync` 的 404 处理） |
| N5 | P2 | CurseForge 文件列表 200 条截断 + 误导性提示：CF 最多翻 4 页 × 50 = 200 条，ProjectPage 又以 `null` 筛选拉全量再客户端过滤；文件超过 200 的项目旧版本文件彻底不可见，此时提示"关掉右上角的筛选看看全部文件"是误导（关掉也看不到） | Content/CurseForgeProvider.cs `GetFilesAsync`、UI/Pages/ProjectPage.xaml.cs `LoadFilesAsync` | 筛选生效时把 `_mc`/`_loader` 传给服务端；或提高分页上限并明示"仅显示最近 200 个文件" |
| N6 | P3 | 整合包导出覆盖旧文件非原子：`File.Delete(outFile); File.Move(tmp, outFile)` 两步之间崩溃会丢掉用户原有的 .mrpack | Content/ModpackExporter.cs L151 | 同盘用 `File.Replace(tmp, outFile, null)` |
| N7 | P3 | 详情页加载失败永不重试：`_loaded = true` 在 await 前置位，加载失败后再进入该页面不再发起请求，只能返回重进 | UI/Pages/ProjectPage.xaml.cs `OnShown` | 失败时复位 `_loaded` 或提供"重试"按钮 |
| N8 | P3 | ZhNames 死代码与竞态：`Reload()` 全项目无调用方；`_map`/`_cjkKeys` 无 `volatile`，`Translate` 锁外直读，若将来从后台线程触发 `Reload` 会读到中间态 | Content/ZhNames.cs | 删除 `Reload` 或字段加 `volatile` / 锁内取局部引用 |
| N9 | P3 | 玻璃效果重复订阅：句柄未就绪时每次 `Glass.Apply` 都 `+= SourceInitialized`，调试滑杆拖动会累积多个处理器，句柄就绪后重复执行 DWM 调用 | UI/Glass.cs `Apply` | 用一次性标记确保只订阅一次 |

---

## 五、遗留改进建议（前两批评审，未修部分）

> 已修项不在此重复列出，见第一、二节。

### P0（安全，建议尽快）

| # | 问题 | 位置 |
|---|---|---|
| 7 | 更新源可借被盗钥匙吊销合法钥匙（revoke 需多签） | Updater.CheckAsync |
| 9 | 自更新替换 exe 非原子（old→新之间崩溃则启动器消失） | Updater.DownloadAndApplyAsync |
| 10 | 签名用 PKCS#1 v1.5，宜升 PSS（格式 2） | UpdateCrypto |

### P1（bug / 正确性）

| # | 问题 | 位置 |
|---|---|---|
| 12 | Rules 按系统架构判库，与实跑 JVM 架构可能不符 | Rules.Allowed |
| 13 | 版本继承链深度限 8 静默截断 | VersionInfo.Load |
| 14 | Library.Artifact 老原生库无主构件时可能拼出错误 URL | Library.cs:76 |
| 15 | 下载前校验阶段单文件 IO 异常炸整批 | Downloader.RunAsync |
| 17 | 外置 validate 2xx 非 JSON 当有效 | YggdrasilAuth.ValidateAsync |
| 18 | Mojang Java 运行时索引 URL 硬编码 hash，上游一换即废 | JavaManager.RuntimeIndexUrl |

### P2（资源 / 并发 / 性能）

| # | 问题 | 位置 |
|---|---|---|
| 22 | GameSession 事件挂住已重建页面（泄漏） | LaunchPage 订阅 Exited |
| 24 | legacy assets 每次启动全量串行 File.Exists | GameFiles.PrepareLegacyAssets |
| 25 | 启动时对全部镜像发 HEAD，弱网拖慢启动 | Mirrors.ProbeAsync |
| 26 | Settings.Changed 后台线程触发，订阅方须自切 UI | Settings.ScheduleSave |
| 27 | UI async void 二十余处，靠全局兜底 | 各 Page 事件 |
| 28 | HashCache / 皮肤缓存 / launcher.log 均无上限 | ContentService / Skins / Log |

### P3（可维护性，长期）

| # | 问题 |
|---|---|
| 29 | 全项目零测试——建议先给 Downloader / JNode / VersionInfo.Merge / UpdateCrypto 写单测 |
| 30 | 自造 JNode 解析宽松（收非法数字、收注释），评估换 System.Text.Json |
| 31 | UI 硬编码中文，无资源文件；Settings 字符串键散落，宜强类型化 |
| 32 | LauncherTask 中文文案在 Core 层，宜上移 UI |
| 33 | LaunchService.BuildArguments / JavaManager / CrashAnalyzer 均为巨型函数/类，宜拆 |
| 34 | Magic string 遍地（"on"/"off"/"modded" 等），宜 const/enum |
| 35 | DemoCapture 430 行混在产品程序集（虽 #if DEBUG），宜独立项目 |

### P4（锦上添花）

| # | 问题 |
|---|---|
| 36 | 镜像失败计数无衰减；限速器宜令牌桶；下载宜加单文件体积上限 |
| 37 | 依赖解析 200 上限与 CF 搜索 10000 上限均静默截断，宜提示 |
| 38 | 前置识别只查 Modrinth，CF 文件认不出——已知取舍，文档写明 |
| 39 | authlib -javaagent 路径含 '=' 会破；设备轮询 slow_down 无 cap |
| 40 | 无代理设置页；版本删除不查"游戏运行中"；日志无级别开关 |

---

## 六、项目地图（接手人速览）

```
Core/       地基。Http（镜像错峰竞速）、Downloader（多线程多源+校验+限速）、
            Mirrors（去中心化核心：规则表+失败降级）、JNode（自造 JSON）、
            Settings（全局设置，JSON 单文件）、TaskManager（后台任务+INPC）
Game/       安装（Installer*.cs：原版/Fabric/Quilt 走 profile JSON 合并；
            Forge/NeoForge 自解析 install_profile 并自跑 processors）、
            LaunchService（参数构造、进程管理、日志、脚本导出）、
            JavaManager（发现/挑选/下载 Mojang 运行时）、CrashAnalyzer（1373 行规则匹配）
Accounts/   微软设备代码登录（RefreshAsync 全链原子写回）、离线、外置登录、
            authlib-injector（带 sha256 校验）、Skins（头像缓存）
Content/    Modrinth/CurseForge 双 provider（Key 只发官方）、PathGuard（防路径穿越）、
            整合包导入导出、ContentIndex（.openlauncher-index.json 记文件来源）
Update/     签名清单自更新。UpdateCrypto（RSA-3072 PKCS#1 v1.5 签 payload）、
            Updater（多源拉清单→验签→取最高版本→sha256 验 exe→自替换）
UI/         WPF。MainWindow（导航/任务/Toast/对话框栈）、Pages×10、Styles、Theme
tools/Release  发布工具：keygen/publish/verify/fingerprint（ol-release.exe）
```

关键约定：

- 所有下载必须过 `Downloader.RunAsync`，哈希校验、换源、限速都在里面。
- 所有相对路径落盘必须过 `PathGuard.Resolve` / `PathGuard.FileIn`。
- 镜像规则三张表叠加：用户自定义 > 签名清单远程下发 > 内置（Mirrors.Rules）。
- 令牌只在 `Accounts/*.cs` 流转；日志侧已有 MaskToken / Redact，新增日志别泄漏令牌。

---

## 七、接手后建议动作顺序

1. **验证第一、三批修复**（Forge 安装 5 步 + 整合包详情页排序实机各过一遍），通过后再动别的。
2. 第四节新发现 N1、N2（都是 P1，用户可感知的状态错乱），优先级高于第五节旧清单。
3. 旧清单 P0 的 7、9、10。
4. 给 Downloader / UpdateCrypto / VersionInfo 补最小单测框架（xUnit + net48），后续改动才有网。
5. P1 / P2 按表从上往下。

---

## 八、环境备忘

- 编译：`.\build.ps1`（需 .NET SDK 8+；本机已将 SDK 8.0.426 装到项目内 `.dotnet\`，`build.ps1` 自动优先使用）。
- 本机可用：Git Bash、VS2022 Community、dotnet runtime + 项目内 .NET SDK 8。
- 发布流程、钥匙轮换、社区接手：见 [`发布与接手.md`](发布与接手.md)。
