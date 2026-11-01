# AGENTS.md

MoonLauncher 开发规范。改动本仓库代码时遵守本文件。

本文件只写**约束**，不重复 `README.md` 和 `docs/` 里能查到的内容。两者冲突时以本文件为准。

**适用范围**：所有分支。唯一按分支分叉的是 UI 框架 —— `port` 用 Avalonia，其余分支用 WPF。

> 本文件应在各分支保持同一份内容。改动规范时同步到 `develop` 与 `port`，避免规范分叉。

---

## 1. 分支模型

- `main`、`port` 是集成分支：**只接受合并，禁止直接提交**。两者都从 `develop`、`feature/*` 或其他分支合并而来。
- 日常改动在 `develop` 上进行。
- 单项大型新功能才开分支，命名 `feature/<feature_name>`，从 `develop` 拉出，完成后合回 `develop`。
- 需要建立其他任何分支时，**先询问用户**再建。
- 当前在 `port` 分支时，做的是跨平台 / Avalonia 相关改动；与跨平台无关的普通功能开发回 `develop`。

## 2. 提交信息

格式固定为：

```
type(scope): subject
```

- `type` 只用这九个：`feat` `fix` `docs` `style` `refactor` `perf` `test` `chore` `revert`
- `scope` 取改动所在模块，见下表
- `subject` 用中文，说明"改了什么"，句末不加句号，不堆实现细节
- 一次提交只做一件事：格式修正、重构、功能改动不要混在同一个提交里
- 跨模块改动可省略 scope，写成 `type: subject`
- 本规范生效前的历史提交不追溯

| scope | 对应位置 |
|---|---|
| `core` | `src/MoonLauncher/Core/` |
| `game` | `src/MoonLauncher/Game/` |
| `accounts` | `src/MoonLauncher/Accounts/` |
| `content` | `src/MoonLauncher/Content/` |
| `update` | `src/MoonLauncher/Update/` |
| `ui` | `src/MoonLauncher/UI/` |
| `build` | 构建脚本、`tools/publish.sh`、`.csproj` |
| `release` | 版本号、签名清单、`release/` |
| `docs` | `docs/`、`README.md`、本文件 |
| `deps` | 依赖升级 |

示例：

```
fix(update): 修正镜像清单验签失败后未回滚的问题
feat(game): 支持 NeoForge 21.1 安装
docs(release): 补充钥匙轮换步骤
```

## 3. 跨平台约束

`port` 分支的存在意义就是让同一份代码跑在 Windows / macOS / Linux 上。**即使当前分支只在 Windows 上编译，业务代码也不得引入平台独占的东西。**

- 业务层（`Core/` `Game/` `Accounts/` `Content/` `Update/`）不使用平台独占 API。
- 平台差异收口到 `Core/Platform.cs`（运行时差异）与 `Core/PlatformEncoding.cs`（编码差异）。新增平台能力加在这两个文件里，不要在业务代码中散落 `RuntimeInformation.IsOSPlatform`、`#if WINDOWS`、`DllImport("kernel32")`。尚未建立这两个文件的分支，参照同样思路集中到单个文件，便于后续合入 `port`。
- **CA1416（平台兼容性）已在 `MoonLauncher.csproj` 的 `NoWarn` 中关闭，编译器不会提示非 Windows 上会抛异常的调用。** 改业务层代码时人工自查：
  - `Microsoft.Win32.Registry` 在非 Windows 上抛异常
  - `System.Security.Cryptography.ProtectedData`（DPAPI）在非 Windows 上抛 `PlatformNotSupportedException`
  - Shell API（回收站、资源管理器、`explorer.exe`）只有 Windows 有
  - 以上调用必须 `try/catch` 降级，或转发到 `Platform`
- 路径不硬编码 `\` 或 `:`；长路径前缀 `\\?\` 只在 Windows 生效（走 `Util.LP()`）；路径大小写比较走 `Util.PathComparison`。
- 已知债务：`Core/TaskManager.cs` 直接使用 `Avalonia.Threading.Dispatcher`，属后端反向依赖 UI。触及该文件时不要扩大这个依赖。

## 4. UI 与后端解耦

`port` 分支把 WPF 换成了 Avalonia，这项改造的前提是 UI 与后端分得开。

- 写新 UI 代码前先确认当前分支的 UI 框架（`port` = Avalonia，其余 = WPF），不要把一种框架的 API 带进另一种。
- UI 层可以调用后端；**后端不得引用任何 UI 类型**，包括两种框架各自的 `Dispatcher`。
- 业务逻辑放后端，UI 只做展示与交互。不要在事件处理里直接写文件 IO、发网络请求、起子进程。
- 新增页面时把"取数据"与"画界面"分开，前者做成后端服务；界面部分只负责绑定和布局。

## 5. 发布

### 5.1 版本号

- 唯一来源是 `src/MoonLauncher/MoonLauncher.csproj` 的 `<Version>`。
- git tag 与 Release 标题统一为 `v` + 版本号，如 `v0.0.3`。
- 格式为 SemVer 的 `MAJOR.MINOR.PATCH`，起始版本 `0.0.1`。

按 SemVer 递增：

| 改动 | 递增位 |
|---|---|
| 新增面向用户的功能 | `MINOR` +1，`PATCH` 归零 |
| 修 Bug、无新功能 | `PATCH` +1 |
| 破坏性变更（0.x 阶段） | `MINOR` +1 |

- `MAJOR` 保持 `0`：表示快速开发阶段，配置格式与内部结构可以随时改动，不承诺向后兼容。
- 到 `1.0.0` 之后破坏性变更才走 `MAJOR` +1。
- 只写三段，不出现 `0.0.1.1` 这种四段版本号。

### 5.2 发版顺序

顺序错了会废掉更新链，尤其是第 3、4 步：

1. 更新 `<Version>`
2. `.\build.ps1`（Unix 用 `sh tools/publish.sh <rid>`）产出 `dist/`
3. **需要代码签名时，先对产物做 Authenticode 签名** —— 签名会改变文件哈希
4. `.\publish.ps1 ...` 生成 `release/update.json`，工具会立即 verify
5. 上传产物到清单里的每个 URL，提交 `update.json`
6. 撰写 Release 描述

详见 `docs/发布与接手.md`。

### 5.3 Release 描述格式

固定结构，同时存一份到 `release/notes-<版本号>.md`：

````md
# MoonLauncher v0.0.3

> 一句话中文介绍

## 新增功能

- 一句话概括一个功能，不写不需要的描述或实现细节

## 问题修复

- 修复了（描述 Bug 内容）的问题。

## 下载安装

| 文件 | SHA-256 |
|---|---|
| MoonLauncher.exe | `...` |
| MoonLauncher.app | `...` |

**Full Changelog**: https://github.com/qtw114514/MoonLauncher/compare/v0.0.2...v0.0.3
````

- 没有内容的章节整节删掉，不要留空标题。
- 模板里的说明文字（如「一句话概括一个功能，不写不需要的描述或实现细节」）是格式提示，不要复制进实际描述。
- SHA-256 用构建脚本打印的小写值，多平台产物逐行列出，文件名与用户实际下载到的文件名一致。
- 一句话介绍写用户能感知的收益，不写内部改动。

## 6. 对外文字

Release 描述不是唯一出口。面向用户的一切文字都只讲**用户能感知的结果**，不讲实现过程：GitHub Release 描述、README、应用内提示与错误信息、更新日志、介绍页。

禁止出现：

- 编译与测试状态：「编译通过」「0 警告 0 错误」「已测试」「实机已验证」「代码审查完成」
- 实现细节：类名、方法名、文件路径、依赖包与版本、内部配置项、分支名、commit SHA（`Full Changelog` 链接除外）
- 开发者术语：重构、依赖升级、修复空引用、补了 try/catch、API 调整、复杂度从 O(n²) 降到 O(n)
- 未完成的内部事项：TODO、遗留问题、下一步计划

应当写用户做什么、得到什么。例如写「修复了整合包导入时内嵌文件被解压到错误目录的问题」，而不是「修复 `Content/PackImport.cs` 里 `Path.Combine` 用错分隔符」。

应用内错误提示同理：给用户可执行的说明，不把异常堆栈、行号、内部路径弹到界面上；技术细节写进日志文件。

自检一句话：普通玩家看这段文字，有没有用？看不出来就删掉。

## 7. 验证

- 改完必须编译通过，目标 0 警告 0 错误。
- 涉及更新清单、验签、自更新、安装器的改动，必须手工验证**失败路径**（断网、哈希不符、签名不符），不能只跑通成功路径。
- 只在本机平台验证过时，不得声称"跨平台已验证"；未验证的部分要明确说出来。
- 本节是开发过程中的要求，验证结论只在开发协作中说明，**不得写进 Release 描述或任何对外文字**（见第 6 节）。
- `dist/`、`bin/`、`obj/`、私钥文件不进仓库（见 `.gitignore`）。
