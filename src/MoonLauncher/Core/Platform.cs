using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MoonLauncher.Core
{
    /// <summary>
    /// 平台差异的唯一出口。
    ///
    /// 业务层（Core / Game / Accounts / Content / Update / Multiplayer）只许调用这里，
    /// 不许自己写 RuntimeInformation.IsOSPlatform、#if WINDOWS、DllImport("kernel32")。
    /// 需要新的平台能力时，在本文件里加方法并同时给出 Windows 与 Unix 两套实现，
    /// 不要退回业务代码里散落条件编译。
    ///
    /// 本文件内部允许出现 Windows 独占 API（Registry、DPAPI、Shell、kernel32），
    /// 因为它们全部被 IsWindows 分支包住，非 Windows 上不会被执行。
    /// </summary>
    public static class Platform
    {
        public static readonly bool IsWindows =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static readonly bool IsMacOS =
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        public static readonly bool IsLinux =
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        /// 路径比较规则：Windows 不区分大小写，Unix 区分。
        /// 路径相等判断一律用它，不要写死 StringComparison.OrdinalIgnoreCase。
        /// </summary>
        public static readonly StringComparison PathComparison =
            IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>路径集合去重 / 字典键用的比较器，规则同 <see cref="PathComparison"/></summary>
        public static readonly StringComparer PathComparer =
            IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>本机路径分隔符</summary>
        public static readonly char PathSeparator = Path.DirectorySeparatorChar;

        // ---------- Java 可执行文件名 ----------

        /// <summary>
        /// 各平台常见的 JDK 安装根目录（每项下面一般是 厂商/版本/bin 或 版本/bin）。
        /// Windows 上返回空 —— 那边靠注册表与 Program Files，由 JavaManager 自己扫。
        /// </summary>
        public static IEnumerable<string> CommonJavaRoots
        {
            get
            {
                if (IsWindows) yield break;
                if (IsMacOS)
                {
                    yield return "/Library/Java/JavaVirtualMachines";
                    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "Library", "Java", "JavaVirtualMachines");
                    yield return "/opt/homebrew/opt";
                    yield return "/usr/local/opt";
                }
                else
                {
                    yield return "/usr/lib/jvm";
                    yield return "/usr/lib64/jvm";
                    yield return "/usr/java";
                }
            }
        }

        /// <summary>无控制台窗口运行 Java 的可执行文件名（Windows 上是 javaw.exe）</summary>
        public static readonly string JavaWindowedExe = IsWindows ? "javaw.exe" : "java";

        /// <summary>控制台版 Java 可执行文件名（探测版本、导出启动脚本用）</summary>
        public static readonly string JavaConsoleExe = IsWindows ? "java.exe" : "java";

        // ---------- 路径 ----------

        /// <summary>
        /// 长路径：超过 240 字符时加 \\?\ 前缀，绕过 Windows 260 字符限制。
        /// 非 Windows 原样返回 —— \\?\ 前缀在 Unix 上会让路径失效。
        /// </summary>
        public static string LongPath(string path)
        {
            if (!IsWindows || string.IsNullOrEmpty(path) || path.Length < 240 || path.StartsWith(@"\\?\")) return path;
            var full = Path.GetFullPath(path);
            return full.StartsWith(@"\\") ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        /// <summary>把清单里以 / 分隔的相对路径落成当前平台的本地路径</summary>
        public static string FromManifestPath(string relative) =>
            string.IsNullOrEmpty(relative) ? relative : relative.Replace('/', PathSeparator);

        /// <summary>把本地路径转成清单里惯用的 / 分隔形式</summary>
        public static string ToManifestPath(string path) =>
            string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');

        /// <summary>
        /// 解开符号链接 / 目录联接后的真实路径，用于判断两个路径是不是同一个目录。
        /// 拿不到时返回 null，调用方自行回退。
        /// </summary>
        public static string RealPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (IsWindows) return WindowsRealPath(path);
            try
            {
                var full = Path.GetFullPath(path).TrimEnd(PathSeparator);
                // Unix 上用真实文件系统信息绕开软链
                var dir = Path.GetDirectoryName(full);
                var name = Path.GetFileName(full);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return full;
                var info = new DirectoryInfo(dir);
                foreach (var e in info.EnumerateFileSystemInfos(name))
                    return Path.GetFullPath(e.FullName).TrimEnd(PathSeparator);
                return full;
            }
            catch { return null; }
        }

        /// <summary>路径是否在某个目录之下（两边都先规范化）</summary>
        public static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            var d = dir.TrimEnd(PathSeparator) + PathSeparator;
            return path.StartsWith(d, PathComparison);
        }

        /// <summary>
        /// 官方启动器的游戏目录。
        /// Windows 在 %APPDATA%\.minecraft，macOS 在 ~/Library/Application Support/minecraft，
        /// Linux 在 ~/.minecraft。
        /// </summary>
        public static string OfficialMinecraftDir
        {
            get
            {
                if (IsWindows)
                    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return IsMacOS
                    ? Path.Combine(home, "Library", "Application Support", "minecraft")
                    : Path.Combine(home, ".minecraft");
            }
        }

        // ---------- 外壳 ----------

        /// <summary>用系统默认程序打开网址</summary>
        public static void OpenUrl(string url)
        {
            try
            {
                if (IsWindows) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                else if (IsMacOS) Process.Start("open", Quote(url));
                else Process.Start("xdg-open", Quote(url));
            }
            catch (Exception ex) { Log.Warn("打开网址失败: " + ex.Message); }
        }

        /// <summary>打开文件夹</summary>
        public static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
                else if (IsMacOS) Process.Start("open", Quote(path));
                else Process.Start("xdg-open", Quote(path));
            }
            catch (Exception ex) { Log.Warn("打开文件夹失败: " + ex.Message); }
        }

        /// <summary>打开文件夹并选中文件（资源管理器 / 访达）</summary>
        public static void RevealInFolder(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (IsWindows) Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(full)) { UseShellExecute = true });
                else if (IsMacOS) Process.Start("open", "-R " + Quote(full));
                else OpenFolder(Path.GetDirectoryName(full) ?? full);
            }
            catch (Exception ex) { Log.Warn("在文件夹中显示失败: " + ex.Message); }
        }

        /// <summary>删到回收站；没有回收站的平台直接删</summary>
        public static void DeleteToRecycleBin(string path)
        {
            if (IsWindows)
            {
                WindowsDeleteToRecycleBin(path);
                return;
            }
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        /// <summary>启动一个新进程（自更新重启用），返回是否启动成功</summary>
        public static bool TryStartProcess(string exe, string args, bool shellExecute)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = shellExecute });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("启动进程失败: " + ex.Message, ex);
                return false;
            }
        }

        /// <summary>改游戏窗口标题；没有对应系统能力的平台什么也不做</summary>
        public static void SetWindowTitle(IntPtr handle, string title)
        {
            if (!IsWindows || handle == IntPtr.Zero) return;
            try { WindowsSetWindowText(handle, title); } catch (Exception ex) { Log.Warn("改窗口标题失败: " + ex.Message); }
        }

        // ---------- 系统信息 ----------

        /// <summary>系统真实架构：x64 / x86 / arm64</summary>
        public static string OsArch
        {
            get
            {
                try
                {
                    switch (RuntimeInformation.OSArchitecture)
                    {
                        case Architecture.X64: return "x64";
                        case Architecture.Arm64: return "arm64";
                        case Architecture.X86: return "x86";
                        case Architecture.Arm: return "arm";
                    }
                }
                catch { }
                return "x64";
            }
        }

        public static Version OsVersion
        {
            get
            {
                if (!IsWindows) return Environment.OSVersion.Version;
                // Environment.OSVersion 在没声明 manifest 时会撒谎，读注册表更准
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
                return Environment.OSVersion.Version;
            }
        }

        /// <summary>物理内存总量与可用量（MB）。拿不到时给保守默认值，不抛异常。</summary>
        public static (long totalMb, long availMb) Memory()
        {
            if (IsWindows)
            {
                try
                {
                    var m = new MemoryStatusEx();
                    if (WindowsGlobalMemoryStatusEx(m))
                        return ((long)(m.ullTotalPhys / 1048576), (long)(m.ullAvailPhys / 1048576));
                }
                catch { }
                return (8192, 4096);
            }

            if (IsLinux) return ReadLinuxMemory();
            if (IsMacOS) return ReadMacMemory();
            return (8192, 4096);
        }

        // ---------- 加密存储 ----------

        /// <summary>
        /// 用当前用户的凭据加密一段数据（Windows 上是 DPAPI）。
        /// 返回 false 表示本平台没有等价能力，调用方自行降级。
        /// </summary>
        public static bool TryProtect(byte[] plain, byte[] entropy, out byte[] cipher)
        {
            cipher = null;
            if (!IsWindows || plain == null) return false;
            try
            {
                cipher = System.Security.Cryptography.ProtectedData.Protect(
                    plain, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("加密存储失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>解密 TryProtect 的结果；返回 false 表示解不开（换了用户、换了机器或本平台没有等价能力）</summary>
        public static bool TryUnprotect(byte[] cipher, byte[] entropy, out byte[] plain)
        {
            plain = null;
            if (!IsWindows || cipher == null) return false;
            try
            {
                plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    cipher, entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return true;
            }
            catch { return false; }
        }

        // ---------- 注册表 ----------

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
            var hives = new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser };
            var views = Environment.Is64BitOperatingSystem
                ? new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 }
                : new[] { Microsoft.Win32.RegistryView.Default };

            foreach (var hive in hives)
                foreach (var view in views)
                {
                    try
                    {
                        using (var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view))
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

        // ---------- 实现细节 ----------

        static string Quote(string s) => "\"" + s + "\"";

        [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool WindowsGlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MemoryStatusEx
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
        static extern bool WindowsSetWindowText(IntPtr hWnd, string text);

        static string WindowsRealPath(string path)
        {
            try
            {
                // 0 = 只查询属性；7 = 允许读/写/删共享；3 = OPEN_EXISTING；0x02000000 = FILE_FLAG_BACKUP_SEMANTICS
                var h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
                try
                {
                    var sb = new StringBuilder(1024);
                    var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                    if (n == 0 || n >= sb.Capacity) return null;
                    var s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
                    if (s.StartsWith(@"\\?\")) return s.Substring(4);
                    return s;
                }
                finally { try { CloseHandle(h); } catch { } }
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        static void WindowsDeleteToRecycleBin(string path)
        {
            if (File.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
            else if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        }

        static void WalkRegistry(
            Microsoft.Win32.RegistryKey key, int depth,
            List<string> valueNames, HashSet<string> skip, int maxDepth,
            ref int budget, Action<string> onValue)
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

        static (long totalMb, long availMb) ReadLinuxMemory()
        {
            try
            {
                long total = 0, avail = 0;
                foreach (var line in File.ReadAllLines("/proc/meminfo"))
                {
                    var parts = line.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || !long.TryParse(parts[1], out var kb)) continue;
                    if (parts[0] == "MemTotal") total = kb;
                    else if (parts[0] == "MemAvailable") avail = kb;
                }
                if (total > 0) return (total / 1024, avail > 0 ? avail / 1024 : total / 2048);
            }
            catch { }
            return (8192, 4096);
        }

        static long _macTotalMb;

        static (long totalMb, long availMb) ReadMacMemory()
        {
            // 起子进程问一次就够了，总量不会变；可用量按总量一半估
            if (_macTotalMb <= 0)
            {
                try
                {
                    var psi = new ProcessStartInfo("sysctl", "-n hw.memsize")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        if (p != null)
                        {
                            var text = p.StandardOutput.ReadToEnd();
                            p.WaitForExit(1000);
                            if (long.TryParse(text.Trim(), out var bytes) && bytes > 0) _macTotalMb = bytes / 1048576;
                        }
                    }
                }
                catch { }
            }
            return _macTotalMb > 0 ? (_macTotalMb, _macTotalMb / 2) : (8192, 4096);
        }
    }
}
