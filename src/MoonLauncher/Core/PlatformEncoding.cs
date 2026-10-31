using System;
using System.Text;

namespace MoonLauncher.Core
{
    /// <summary>
    /// 编码差异的唯一出口。
    ///
    /// 同一份数据在不同平台上用的"系统默认编码"并不一样：
    /// Windows 是本地 ANSI 代码页（简中为 GBK），macOS / Linux 是 UTF-8。
    /// 业务层不要直接写 Encoding.Default —— 它在 net48 上等于 ANSI，
    /// 换到 Unix 就会把中文读成乱码或写出别的程序认不出的文件。
    /// </summary>
    public static class PlatformEncoding
    {
        /// <summary>
        /// 系统本地编码：写出去要让其它程序（尤其是 Java）按系统约定打开时用它。
        /// Windows 上是 ANSI 代码页，Unix 上是 UTF-8。
        /// </summary>
        public static Encoding SystemDefault =>
            Platform.IsWindows ? Encoding.Default : new UTF8Encoding(false);

        /// <summary>UTF-8，不带 BOM</summary>
        public static Encoding Utf8 => new UTF8Encoding(false);

        /// <summary>
        /// 先按严格 UTF-8 读，失败再按系统本地编码读。
        /// 用于读来路不明的日志文件：新版工具写 UTF-8，老版或系统工具可能写 ANSI。
        /// </summary>
        public static string DecodeText(byte[] buffer, int offset, int count)
        {
            try { return new UTF8Encoding(false, true).GetString(buffer, offset, count); }
            catch (DecoderFallbackException) { return SystemDefault.GetString(buffer, offset, count); }
        }
    }
}
