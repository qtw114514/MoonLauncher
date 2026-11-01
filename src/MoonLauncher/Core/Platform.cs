using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MoonLauncher.Core
{
    /// <summary>
    /// 平台差异的唯一出口。原来散在 Util / Paths / JavaManager / GameFiles / Rules 里的
    /// Windows 假设（注册表、DPAPI、explorer.exe、javaw.exe、natives-windows、回收站）
    /// 全部收拢到这里，三种平台各自实现。
    /// </summary>
    public static partial class Platform
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>Minecraft 版本 JSON 里 rules.os.name 的取值</summary>
        public static string OsName => IsWindows ? "windows" : IsMacOS ? "osx" : "linux";

        public static string OsDescription =>
            $"{RuntimeInformation.OSDescription.Trim()} {OsArch}";

        // ================= 架构 =================
        static string _arch;
        /// <summary>系统真实架构：x64 / x86 / arm64</summary>
        public static string OsArch => _arch ?? (_arch = DetectArch());

        static string DetectArch()
        {
            try
            {
                if (IsWindows)
                {
                    // Windows：IsWow64Process2 能区分 ARM64 上的 x64 模拟
                    try
                    {
                        if (IsWow64Process2(Process.GetCurrentProcess().Handle, out _, out var native))
                        {
                            if (native == 0xAA64) return "arm64";
                            if (native == 0x8664) return "x64";
                            if (native == 0x14c) return "x86";
                        }
                    }
                    catch { }
                    GetNativeSystemInfo(out var si);
                    return si.wProcessorArchitecture switch { 9 => "x64", 12 => "arm64", _ => "x86" };
                }

                // macOS / Linux：OSArchitecture 报告的是操作系统架构（Rosetta 下也是 arm64）
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm64 => "arm64",
                    Architecture.X64 => "x64",
                    Architecture.X86 => "x86",
                    _ => "x64",
                };
            }
            catch { return "x64"; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture, wReserved;
            public uint dwPageSize; public IntPtr lpMin, lpMax, mask;
            public uint num, type, gran; public ushort level, rev;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

        [DllImport("kernel32.dll")] static extern void GetNativeSystemInfo(out SYSTEM_INFO info);

        // ================= 内存 =================
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("libSystem.dylib", SetLastError = true)]
        static extern int sysctlbyname(string name, out long oldp, ref IntPtr oldlenp, IntPtr newp, IntPtr newlen);

        static long _macTotalMb;

        /// <summary>物理内存总量 / 当前可用量（MB）。拿不到时给保守值。</summary>
        public static (long totalMb, long availMb) Memory()
        {
            try
            {
                if (IsWindows)
                {
                    var m = new MEMORYSTATUSEX();
                    if (!GlobalMemoryStatusEx(m)) return (8192, 4096);
                    return ((long)(m.ullTotalPhys / 1048576), (long)(m.ullAvailPhys / 1048576));
                }

                if (IsLinux)
                {
                    long total = 0, avail = 0;
                    foreach (var line in File.ReadAllLines("/proc/meminfo"))
                    {
                        if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = ParseKb(line);
                        else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) avail = ParseKb(line);
                        if (total > 0 && avail > 0) break;
                    }
                    if (total > 0) return (total, avail > 0 ? avail : total / 2);
                    return (8192, 4096);
                }

                if (IsMacOS)
                {
                    var total = MacTotalMb();
                    if (total > 0)
                    {
                        // 可用量拿不到 sysctl 的可靠值，用 .NET 提供的可用内存估计
                        var gc = GC.GetGCMemoryInfo();
                        var avail = gc.TotalAvailableMemoryBytes > 0
                            ? gc.TotalAvailableMemoryBytes / 1048576
                            : total / 2;
                        return (total, (long)Math.Min(avail, total));
                    }
                    return (8192, 4096);
                }
            }
            catch (Exception ex) { Log.Warn("读取内存信息失败：" + ex.Message); }
            return (8192, 4096);
        }

        static long ParseKb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb / 1024 : 0;
        }

        static long MacTotalMb()
        {
            if (_macTotalMb > 0) return _macTotalMb;
            try
            {
                var len = (IntPtr)8;
                if (sysctlbyname("hw.memsize", out var bytes, ref len, IntPtr.Zero, IntPtr.Zero) == 0 && bytes > 0)
                    _macTotalMb = bytes / 1048576;
            }
            catch { }
            return _macTotalMb;
        }

        // ================= 系统版本 =================
        public static Version OsVersion
        {
            get
            {
                if (IsWindows)
                {
                    // Environment.OSVersion 在没有 manifest 声明时会撒谎，读注册表更准
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        {
                            var major = k?.GetValue("CurrentMajorVersionNumber");
                            var build = k?.GetValue("CurrentBuildNumber") as string;
                            if (major != null && build != null)
                                return new Version(Convert.ToInt32(major), Convert.ToInt32(k.GetValue("CurrentMinorVersionNumber") ?? 0), int.Parse(build));
                        }
                    }
                    catch { }
                }
                return Environment.OSVersion.Version;
            }
        }

        // ================= 可执行文件命名 =================
        /// <summary>控制台版 Java 可执行文件名（bin 目录下）</summary>
        public static string JavaExeName => IsWindows ? "java.exe" : "java";

        /// <summary>无控制台窗口的 Java 可执行文件名；Unix 上没有 javaw，就是 java</summary>
        public static string JavawExeName => IsWindows ? "javaw.exe" : "java";

        /// <summary>从 javaw 路径推出控制台版 java 路径（探测版本、跑安装器、导出脚本用）</summary>
        public static string ConsoleJavaPath(string javawPath)
        {
            if (string.IsNullOrEmpty(javawPath)) return javawPath;
            var dir = Path.GetDirectoryName(javawPath);
            var candidate = Path.Combine(dir ?? "", JavaExeName);
            return File.Exists(candidate) ? candidate : javawPath;
        }

        // ================= 原生库命名 =================
        /// <summary>natives classifier 的平台段：windows / linux / macos</summary>
        public static string NativesOs => IsWindows ? "windows" : IsMacOS ? "macos" : "linux";

        /// <summary>Mojang 运行时索引里的平台名，如 windows-x64 / linux / mac-os-arm64</summary>
        public static string RuntimePlatform(string arch)
        {
            arch = arch ?? OsArch;
            if (IsWindows) return "windows-" + (arch == "arm64" ? "arm64" : arch == "x86" ? "x86" : "x64");
            if (IsMacOS) return arch == "arm64" ? "mac-os-arm64" : "mac-os";
            return arch == "arm64" ? "linux-arm64" : arch == "x86" ? "linux-i386" : "linux";
        }

        /// <summary>该平台原生库的文件扩展名</summary>
        public static bool IsNativeLibrary(string fileName)
        {
            var ext = Path.GetExtension(fileName);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".so", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jnilib", StringComparison.OrdinalIgnoreCase);
        }

        // ================= 目录 =================
        /// <summary>官方启动器的 .minecraft 位置</summary>
        public static string OfficialGameDir
        {
            get
            {
                if (IsMacOS)
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support", "minecraft");
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                // Windows: %APPDATA%\.minecraft；Linux: ~/.minecraft（ApplicationData 是 ~/.config）
                return IsWindows
                    ? Path.Combine(appData, ".minecraft")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
            }
        }

        /// <summary>用户级数据目录（exe 旁不可写时的回退）</summary>
        public static string UserDataDir(string appName)
        {
            if (IsWindows)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName);
            if (IsMacOS)
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", appName);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", appName);
        }

        // ================= 清单路径 =================
        /// <summary>本机路径分隔符</summary>
        public static readonly char PathSeparator = Path.DirectorySeparatorChar;

        /// <summary>把清单里以 / 分隔的相对路径落成当前平台的本地路径</summary>
        public static string FromManifestPath(string relative) =>
            string.IsNullOrEmpty(relative) ? relative : relative.Replace('/', Path.DirectorySeparatorChar);

        /// <summary>把本地路径转成清单里惯用的 / 分隔形式</summary>
        public static string ToManifestPath(string path) =>
            string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

        /// <summary>路径是否在某个目录之下（两边都应已是完整路径）</summary>
        public static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            var sep = Path.DirectorySeparatorChar;
            var d = dir.TrimEnd(sep) + sep;
            return path.StartsWith(d, Util.PathComparison);
        }

        /// <summary>
        /// 解开符号链接 / 目录联接后的真实路径，用于判断两个路径是不是同一个目录。
        /// 拿不到时返回 null，调用方自行回退到原路径。
        /// </summary>
        public static string RealPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IsWindows) return WindowsRealPath(path);
            // Unix 上不需要这层：调用方本来就是拿完整的真实路径，软链由文件系统自己解开
            return null;
        }

        // ================= 窗口标题 =================
        /// <summary>改游戏窗口标题；没有对应系统能力的平台什么也不做</summary>
        public static void SetWindowTitle(IntPtr handle, string title)
        {
            if (!IsWindows || handle == IntPtr.Zero) return;
            try { WindowsSetWindowText(handle, title); }
            catch (Exception ex) { Log.Warn("改窗口标题失败: " + ex.Message); }
        }

        // ================= 注册表 =================
        /// <summary>
        /// 遍历注册表里若干根键下的指定值名，把字符串值交给回调。
        /// 只有 Windows 有注册表；其他平台直接返回，不会调用回调。
        /// 用回调是为了不让 Win32 类型漏进业务层。
        /// </summary>
        public static void VisitRegistryValues(
            IEnumerable<string> roots,
            IEnumerable<string> valueNames,
            IEnumerable<string> skipSubKeys,
            int maxDepth,
            Action<string> onValue)
        {
            if (!IsWindows || onValue == null) return;
            var skip = new HashSet<string>(skipSubKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var names = new List<string>(valueNames ?? Array.Empty<string>());
            var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            var views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Default };

            foreach (var hive in hives)
                foreach (var view in views)
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
                            foreach (var root in roots ?? Array.Empty<string>())
                                using (var k = baseKey.OpenSubKey(root))
                                    if (k != null)
                                    {
                                        int budget = 2000;
                                        WalkRegistry(k, 0, names, skip, maxDepth, ref budget, onValue);
                                    }
                    }
                    catch (Exception ex) { Log.Warn($"读取注册表 {hive}/{view} 失败：{ex.Message}"); }
                }
        }

        static void WalkRegistry(RegistryKey key, int depth, List<string> valueNames,
            HashSet<string> skip, int maxDepth, ref int budget, Action<string> onValue)
        {
            if (--budget < 0) return;
            foreach (var name in valueNames)
            {
                try
                {
                    if (key.GetValue(name) is string v && !string.IsNullOrWhiteSpace(v)) onValue(v);
                }
                catch { }
            }
            if (depth >= maxDepth) return;
            string[] subs;
            try { subs = key.GetSubKeyNames(); } catch { return; }
            foreach (var s in subs)
            {
                if (skip.Contains(s)) continue;
                try
                {
                    using (var sk = key.OpenSubKey(s))
                        if (sk != null) WalkRegistry(sk, depth + 1, valueNames, skip, maxDepth, ref budget, onValue);
                }
                catch { }
            }
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
        static extern bool WindowsSetWindowText(IntPtr hWnd, string text);

        static string WindowsRealPath(string path)
        {
            try
            {
                // 0 = 只查询属性；7 = 允许读/写/删共享；3 = OPEN_EXISTING；0x02000000 = FILE_FLAG_BACKUP_SEMANTICS
                using (var h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    var sb = new StringBuilder(1024);
                    var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity) return null;
                    var s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
                    if (s.StartsWith(@"\\?\")) return s.Substring(4);
                    return s;
                }
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        // ================= 打开文件夹 / 网址 =================
        /// <summary>用系统文件管理器打开目录</summary>
        public static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                if (IsWindows)
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                else if (IsMacOS)
                    Process.Start(new ProcessStartInfo("open", "\"" + path + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("xdg-open", "\"" + path + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { Log.Warn("打开文件夹失败: " + ex.Message); }
        }

        /// <summary>在文件管理器里定位到某个文件</summary>
        public static void RevealInFolder(string path)
        {
            try
            {
                if (IsWindows)
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (IsMacOS)
                    Process.Start(new ProcessStartInfo("open", "-R \"" + path + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo("xdg-open", "\"" + Path.GetDirectoryName(path) + "\"") { UseShellExecute = false });
            }
            catch (Exception ex) { Log.Warn("定位文件失败: " + ex.Message); }
        }

        public static void OpenUrl(string url)
        {
            try
            {
                if (IsWindows || IsMacOS)
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            }
            catch (Exception ex) { Log.Warn("打开网址失败: " + ex.Message); }
        }

        // ================= 回收站 =================
        /// <summary>删除到回收站；不支持回收站的平台直接删除。返回是否成功。</summary>
        public static bool MoveToRecycleBin(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (IsWindows) return WindowsRecycle(path);
                if (IsMacOS) return MacRecycle(path);
                return LinuxRecycle(path);
            }
            catch (Exception ex) { Log.Warn("移入回收站失败，改为直接删除：" + ex.Message); }
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) { Log.Warn("删除失败：" + ex.Message); return false; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        const uint FO_DELETE = 3;
        const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;

        static bool WindowsRecycle(string path)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
            };
            return SHFileOperation(ref op) == 0;
        }

        static bool MacRecycle(string path)
        {
            // Finder 的“移到废纸篓”：通过 osascript 调用，失败则直接删除
            var script = $"tell application \"Finder\" to delete POSIX file \"{path.Replace("\"", "\\\"")}\"";
            var psi = new ProcessStartInfo("osascript", "-e " + QuoteForShell(script))
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit(8000);
                return p.ExitCode == 0;
            }
        }

        static bool LinuxRecycle(string path)
        {
            var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Trash");
            var files = Directory.CreateDirectory(Path.Combine(trash, "files")).FullName;
            var info = Directory.CreateDirectory(Path.Combine(trash, "info")).FullName;
            var name = Path.GetFileName(path.TrimEnd('/'));
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var target = Path.Combine(files, name);
            if (File.Exists(target) || Directory.Exists(target)) target = Path.Combine(files, $"{name}.{stamp}");
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            var trashName = Path.GetFileName(target);
            File.WriteAllText(Path.Combine(info, trashName + ".trashinfo"),
                "[Trash Info]\n" + "Path=" + Uri.EscapeDataString(path) + "\n" + "DeletionDate=" + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") + "\n");
            return true;
        }

        static string QuoteForShell(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ================= 令牌加密 =================
        /// <summary>
        /// 账号令牌加密。Windows 用 DPAPI（当前用户）；
        /// macOS 用 Keychain 级别保护做不到同步 API，退化为「仅本用户可读」的文件 + 明示；
        /// Linux 同理。返回 null 表示该平台不支持加密，调用方按明文存储。
        /// </summary>
        public const string DpapiPrefix = "dpapi:";

        public static byte[] Protect(byte[] data, byte[] entropy)
        {
            if (!IsWindows) return null;
            return ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
        }

        public static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            if (!IsWindows) return null;
            return ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
        }
    }
}
