<p align="center"><img src="src/MoonLauncher/Assets/icon.png" width="96" style="image-rendering:pixelated"></p>

<h1 align="center">MoonLauncher</h1>
<p align="center">开源的跨平台 Minecraft 启动器 · GPL-3.0</p>

---

MoonLauncher 是一个开源的 Minecraft 启动器，支持 Mod 管理、整合包导入导出、Forge / NeoForge / Fabric / Quilt / OptiFine 安装，以及界面个性化。启动器可运行在 Windows、macOS 和 Linux 上，覆盖 x64 与 ARM64 架构，并自动处理 Java 运行时与游戏文件的下载，不需要手动配置环境。

## 下载

从 [GitHub Releases](https://github.com/qtw114514/MoonLauncher/releases) 下载对应平台的包：

- Windows：`MoonLauncher.exe`
- macOS：`MoonLauncher.app`
- Linux：`MoonLauncher`

游戏文件默认保存在程序旁边的 `.minecraft` 目录。Windows 平台上，启动器配置保存在 `MoonLauncher` 目录；Linux/macOS 平台上，启动器配置保存在 `MoonLauncher-data` 目录。
把整个文件夹复制到另一台电脑，配置与账号都会保留。

## 功能

安装游戏、管理多个版本、账号登录、Mod 与资源包管理、整合包导入导出等常规功能均已包含。以下功能单独说明：

- **中文搜索**：Modrinth 和 CurseForge 不接受中文，输入中文俗称会先转换成搜索词；详情页附 MC 百科的中文简介
- **崩溃分析**：读取崩溃日志，指出内存不足、Java 版本不匹配、缺少前置、Mod 冲突或显卡驱动等原因，并给出处理建议
- **数据包**：安装时选择装进哪个存档
- **启动脚本**：导出 `.bat` 用于复现某次启动。脚本包含账号访问令牌，导出前会提醒
- **下载源**：前一个源 2.5 秒没有响应就同时请求下一个，谁先成功用谁；连不上的源会被跳过
- **主页**：可以填一个网址，或指向本地 HTML 文件

## 系统要求

- Windows 10 及以上
- macOS 10.15 及以上
- 主流 Linux 发行版（glibc，x64 / ARM64）

发布包已包含 .NET 运行时，不需要另外安装。如果更在意体积，也可以下载依赖 .NET 8 运行时的版本。

## 关于去中心化

启动器自身也需要更新，而更新通常依赖开发者维护的服务器。服务器关闭、域名过期，或者维护者不再继续，用户往往只能停在某个旧版本上。

MoonLauncher 的更新清单使用私钥签名，启动器内置对应的公钥。清单中记录版本号、安装包的文件名与 SHA-256，以及若干下载地址。启动器会并行请求所有更新源，用内置公钥验证签名，选用版本最高的有效清单，下载完成后再核对文件哈希。

由此带来三点：

- 没有任何一个服务器是必需的。更新清单和安装包可以由任何人转发，放在 GitHub、CDN、网盘或自建服务器上都可以。
- 镜像不需要被信任。改动其中任何一个字节，签名或哈希校验都会失败，启动器会拒绝安装并继续使用当前版本。
- 维护者不再继续时，社区可以发布由新密钥签名的清单。用户在设置中添加新的公钥后，就能继续收到更新。

游戏本体、加载器和 Mod 不走这套机制，直接来自官方源和公开镜像。

## 致谢与声明

- 本项目与 Mojang、Microsoft 无关，Minecraft 是 Mojang AB 的商标。
- 本项目基于 [OpenLauncher](https://honghemc.cn/app/openlauncher.html) 的源码继续开发。功能设计参考了 PCL、HMCL 等国内启动器，在此致谢。
- 本项目使用 AI 辅助开发，包含 AI 生成的代码。
- 镜像服务由 bangbang93（BMCLAPI）、MCIM 等社区成员公益提供，请合理使用。
