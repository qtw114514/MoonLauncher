namespace OpenLauncher
{
    /// <summary>
    /// 换名字、换仓库、换维护者，只改这一个文件。
    /// 任何人 fork 之后改掉这里 + 换成自己的签名公钥，就是一个独立发行版。
    /// </summary>
    public static class Branding
    {
        public const string AppName = "MoonLauncher";
        public const string DisplayName = "MoonLauncher";
        public const string Tagline = "去中心化 Minecraft 启动器";

        /// <summary>主页 / 源码下载（仅用于"关于"页面和 User-Agent，不是更新的唯一来源）</summary>
        public const string RepoUrl = "https://github.com/qtw114514/OpenLauncher";

        /// <summary>
        /// 内置的更新清单地址。任意一个能访问到、且签名验证通过就行，
        /// 用户也可以在 设置 → 更新 里自己加镜像。顺序不代表优先级：会全部尝试并取版本最高的有效清单。
        /// </summary>
        public static readonly string[] DefaultUpdateSources =
        {
            "https://raw.githubusercontent.com/qtw114514/OpenLauncher/main/release/update.json",
            "https://cdn.jsdelivr.net/gh/qtw114514/OpenLauncher@main/release/update.json",
            "https://fastly.jsdelivr.net/gh/qtw114514/OpenLauncher@main/release/update.json",
            "https://ghfast.top/https://raw.githubusercontent.com/qtw114514/OpenLauncher/main/release/update.json",
        };

        /// <summary>
        /// 内置信任的发布公钥（RSA，.NET XML 格式）。用 tools/Release 的 keygen 生成。
        /// 更新清单必须由其中任意一把私钥签名才会被接受。
        /// 维护者失联时，社区可以发布新公钥，由用户在 设置 → 更新 → 信任的发布者 里手动添加。
        /// </summary>
        public static readonly string[] TrustedKeys =
        {
            // 指纹 12c461c38f08483a（OpenLauncher 初始发布钥匙）
            "<RSAKeyValue><Modulus>ugyFBDPVmXZcK68n80vPAgS5wrhhCwOgmlZuvUZULR1tGN67q7fShd4BV/iyjC0t4Qn9moE0NGY9O1KRMnlWPd2wnGzinX+536lrkmba43dbBL733tUhRbLjRa0WWGjV1CoABlljVU4XoUYgyuAOWnTWYwBq+dNeCr1nq5CUH8F+3KNUMrA5HGM50OiIRkM7j3IDpwxB8wQMngP5ObPLDMBpz94Zqn/+ZFFpXhlw81vZACe/7KrnEtIXHkVusDtZFuVAN7M6XLwaTyDHkEYshudCnj80sXBi1dbC3beKmcBSSd3oH1lDRmdEen+vU5aDOi9mY4V2RstgCdZKPt3xJ/G98Nm6t8khnrlpitqyAD60DHWhtDCLDUlAaaWsfda+PeGt1s8novdpnaiSJO9Fcaywiz+WdMHjvBN+Z1u18Adiu9MdYACY/Rg70pnk37drYp4GD8o+tZEfRxW32RfFV5jDI4INrFWdlT33VLmTpPtWQItW6ibXDgOIP23cnGAh</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
            // 指纹 4b05c0b3730e3fc9（当前维护者发布钥匙，私钥离线保存于维护者处）
            "<RSAKeyValue><Modulus>ysT/Z/UMh4EVyKHE6Xe1nDoF/5G9MyPFvopmrQQewq6qNg9JT/flrt1X4G4pxMIegu5mgookSXsfMxEEcrRtsX41W6oCvVWnKBzUFlrfUSoZbz2QPC0K2vEwBjh4dSF/WzriyDafBqT5JazM+pUi3W7BzZqUkbRPFzh0HF8oduYDx+0zjN2D9iDGWalWM8RFDNEDLUbCkWiTs1AsFocejcyNeRbCSyJ0zjfIHU7uTC6hGEHMXcLZ/BbrX7UAAD0XK1Mqn0ayX92DXdQjYEtrMHAxAbvInxPVg6DFzXwkC05Zyj2zE0Y2T24wy8HKNm8HKliZFF1Q0iV8FISDRSbAfynsXuX3nauQ3+JAjhGGmzgG7NPItqfdXFIBBOIxWhubKB8ol6KsRjAfz/vv+bSXGC/gPcaA2/BjwEtic3QLbvKWuckSHu2MbxacJClrP/xP8pE1h85EDWGrZ27S83KJDepPOzWFPIl5gH7jx7O+RQSqAAI8gv3YZmEnIFOVDzMZ</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>",
        };

        /// <summary>
        /// 微软正版登录用的 Azure 应用 Client ID。
        /// 需要自己在 Azure 注册应用并向 Mojang 申请 Minecraft API 权限后填入；
        /// 留空时用户可在 设置 → 账户 里填自己的。
        /// </summary>
        public const string DefaultMicrosoftClientId = "";

        /// <summary>CurseForge API Key（可选）。留空时自动走无需 Key 的镜像。</summary>
        public const string DefaultCurseForgeApiKey = "";
    }
}
