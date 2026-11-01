using System.Text;

namespace MoonLauncher.Core
{
    public static partial class Platform
    {
        static Encoding _ansi;
        static bool _codePagesRegistered;

        /// <summary>
        /// .NET Core 默认只带 UTF-8，GBK(936) 这类代码页要显式注册提供器，
        /// 否则 GetEncoding(936) 会抛异常、被下面的 catch 吞掉，GBK 回退静默失效。
        /// </summary>
        static void EnsureCodePages()
        {
            if (_codePagesRegistered) return;
            _codePagesRegistered = true;
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { }
        }

        /// <summary>
        /// 系统 ANSI 代码页编码。.NET Core 的 Encoding.Default 恒为 UTF-8，
        /// 不能再像 .NET Framework 那样拿来当 GBK 用（Java 8 的输出就是 GBK）。
        /// </summary>
        public static Encoding LegacyAnsiEncoding
        {
            get
            {
                if (_ansi != null) return _ansi;
                if (!IsWindows) return _ansi = new UTF8Encoding(false);
                EnsureCodePages();
                try { return _ansi = Encoding.GetEncoding(936); }
                catch { return _ansi = new UTF8Encoding(false); }
            }
        }

        /// <summary>读 Java 子进程输出用的编码：Java 8 用系统代码页，Java 9+ 是 UTF-8</summary>
        public static Encoding ConsoleEncoding(int javaMajor) =>
            javaMajor > 8 ? new UTF8Encoding(false) : LegacyAnsiEncoding;

        /// <summary>Java 9~17 的 @argfile 按平台默认编码读取，不能用 UTF-8</summary>
        public static Encoding ArgFileEncoding => LegacyAnsiEncoding;
    }
}
