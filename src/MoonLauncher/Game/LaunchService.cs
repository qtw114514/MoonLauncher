using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MoonLauncher.Accounts;
using MoonLauncher.Core;

namespace MoonLauncher.Game
{
    /// <summary>一次正在运行的游戏</summary>
    public class GameSession
    {
        public Instance Instance { get; set; }
        public Process Process { get; set; }
        public JavaInfo Java { get; set; }
        public DateTime StartTime { get; set; }
        public bool KilledByUser { get; set; }
        public bool WindowShown { get; internal set; }
        public string CommandLine { get; set; }

        readonly LinkedList<string> _lines = new LinkedList<string>();
        readonly object _evLock = new object();
        Action _windowAppeared;
        Action<int> _exited;
        public event Action<string> Line;
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        internal string TempArgFile { get; set; }

        /// <summary>订阅时窗口已经出现过，会立刻补发一次</summary>
        public event Action WindowAppeared
        {
            add { bool fire; lock (_evLock) { _windowAppeared += value; fire = WindowShown; } if (fire) value(); }
            remove { lock (_evLock) _windowAppeared -= value; }
        }

        /// <summary>订阅时游戏已经退出（比如 JVM 参数写错秒退），会立刻补发一次</summary>
        public event Action<int> Exited
        {
            add { bool fire; lock (_evLock) { _exited += value; fire = HasExited; } if (fire) value(ExitCode); }
            remove { lock (_evLock) _exited -= value; }
        }

        internal void AddLine(string s)
        {
            lock (_lines)
            {
                _lines.AddLast(s);
                while (_lines.Count > 8000) _lines.RemoveFirst();
            }
            Line?.Invoke(s);
        }

        public string Output { get { lock (_lines) return string.Join("\n", _lines); } }
        internal void RaiseWindow()
        {
            Action h;
            lock (_evLock) { if (WindowShown) return; WindowShown = true; h = _windowAppeared; }
            DeleteTempArgFile();
            h?.Invoke();
        }

        internal void RaiseExit(int code)
        {
            Action<int> h;
            lock (_evLock) { HasExited = true; ExitCode = code; h = _exited; }
            DeleteTempArgFile();
            h?.Invoke(code);
        }

        /// <summary>参数文件里有访问令牌，Java 读完就删</summary>
        void DeleteTempArgFile()
        {
            if (TempArgFile != null) { Util.TryDelete(TempArgFile); TempArgFile = null; }
        }

        public void Kill()
        {
            KilledByUser = true;
            try { if (!Process.HasExited) Process.Kill(); } catch { }
        }
    }

    public static class LaunchService
    {
        public static readonly List<GameSession> Running = new List<GameSession>();
        static readonly HashSet<string> Preparing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string InstanceKey(Instance inst) => inst.Repo.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "|" + inst.Id;

        /// <summary>运行中或正在准备启动。版本管理用它在移动、删除前阻止危险操作。</summary>
        public static bool IsInstanceBusy(Instance inst)
        {
            if (inst == null) return false;
            var key = InstanceKey(inst);
            lock (Running) return Preparing.Contains(key) || Running.Any(s => !s.HasExited && InstanceKey(s.Instance) == key);
        }

        /// <summary>在首个 await 前占位，避免重复点击或账号对话框期间进入第二个启动流程。</summary>
        public static bool TryBeginLaunch(Instance inst)
        {
            if (inst == null) return false;
            var key = InstanceKey(inst);
            lock (Running)
            {
                if (Preparing.Contains(key) || Running.Any(s => !s.HasExited && InstanceKey(s.Instance) == key)) return false;
                Preparing.Add(key);
                return true;
            }
        }

        public static void EndLaunch(Instance inst)
        {
            if (inst == null) return;
            lock (Running) Preparing.Remove(InstanceKey(inst));
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hWnd, string text);

        /// <summary>自动内存：按可用内存与 Mod 数量估算，单位 MB</summary>
        public static int AutoMemory(Instance inst, JavaInfo java)
        {
            var (total, avail) = Util.Memory();
            int mods = 0;
            try { if (inst.IsModded && Directory.Exists(inst.ModsDir)) mods = Directory.GetFiles(inst.ModsDir, "*.jar").Length; } catch { }
            double want = inst.IsModded ? 2560 + mods * 24 : 2048;
            if (inst.IsValid && inst.Info.JavaMajor >= 21) want += 512; // 新版本本体就更吃内存
            want = Math.Min(want, inst.IsModded ? 12288 : 6144);
            // 不要把系统内存吃光：最多用到 可用内存 + 总内存的 10%
            double cap = Math.Max(1024, avail + total * 0.1 - 512);
            int mb = (int)Math.Min(want, cap);
            if (java != null && !java.Is64Bit) mb = Math.Min(mb, 1024);
            return Math.Max(1024, mb / 128 * 128);
        }

        public static int MemoryFor(Instance inst, JavaInfo java)
        {
            var s = inst.Settings;
            if (s["memoryMode"].Str == "custom" && s["memoryMb"].Int > 0) return s["memoryMb"].Int;
            if (s["memoryMode"].Str == "auto") return AutoMemory(inst, java);
            return Settings.MemoryAuto ? AutoMemory(inst, java) : Settings.MemoryMb;
        }

        public static async Task<GameSession> LaunchAsync(Instance inst, Account account, LauncherTask task)
        {
            var ct = task.Token;
            if (account == null) throw new Exception("还没有添加账号，先去添加一个吧（离线账号也行）");

            task.Report(0.02, "检查版本…");
            inst.Reload();
            var info = inst.Info; // 读失败会直接抛出带原因的异常
            var repo = inst.Repo;

            var missingGen = GameFiles.MissingGeneratedLibraries(repo, info);
            if (missingGen.Count > 0)
                throw new Exception($"这个版本缺少由安装器生成的文件（{missingGen[0]} 等 {missingGen.Count} 个），请删除后重新安装这个版本的加载器");

            task.Report(0.05, "选择 Java…");
            bool forgeOld = inst.Loaders.Any(l => l.Type == LoaderType.Forge) && Util.CompareVersion(info.McVersion, "1.17") < 0;
            JavaInfo java = null;
            var forced = inst.GetSetting("java");
            if (!string.IsNullOrEmpty(forced)) java = JavaManager.Inspect(forced);
            if (java == null) java = await JavaManager.EnsureAsync(info.JavaMajor, info.JavaComponent, forgeOld, task, 0.05, 0.3).ConfigureAwait(false);
            if (java == null) throw new Exception($"找不到合适的 Java {info.JavaMajor}");
            Log.Info($"使用 Java：{java.Display}（{java.JavawPath}）");

            task.Report(0.3, "补全游戏文件…");
            if (!inst.Settings["skipFileCheck"].Bool)
                await GameFiles.CompleteAsync(repo, info, task, 0.3, 0.75, quickCheck: true, arch: java.Arch).ConfigureAwait(false);

            task.Report(0.75, "登录账号…");
            var auth = await AccountService.PrepareAsync(account, task, ct).ConfigureAwait(false);

            task.Report(0.85, "解压原生库…");
            var natives = inst.NativesDir;
            GameFiles.ExtractNatives(repo, info, natives, java.Arch);
            Directory.CreateDirectory(inst.GameDir);
            var assetsRoot = GameFiles.PrepareLegacyAssets(repo, info, inst.GameDir);
            EnsureDefaultLanguage(inst, info);

            task.Report(0.9, "生成启动参数…");
            var (jvm, mainClass, game) = BuildArguments(inst, info, java, auth, natives, assetsRoot);

            ct.ThrowIfCancellationRequested();
            task.Report(0.95, "启动游戏…");
            var session = Start(inst, java, jvm, mainClass, game);
            return session;
        }

        /// <summary>第一次启动（还没有 options.txt）时默认简体中文</summary>
        static void EnsureDefaultLanguage(Instance inst, VersionInfo info)
        {
            if (!Settings.GetBool("launch.defaultChinese", true)) return;
            var options = Path.Combine(inst.GameDir, "options.txt");
            if (File.Exists(options)) return;
            try
            {
                // 1.11 以前语言代码是 zh_CN
                bool old = info.ReleaseTime != DateTime.MinValue && info.ReleaseTime < new DateTime(2016, 11, 14);
                File.WriteAllText(options, "lang:" + (old ? "zh_CN" : "zh_cn") + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("写入默认语言失败：" + ex.Message); }
        }

        static (List<string> jvm, string mainClass, List<string> game) BuildArguments(Instance inst, VersionInfo info, JavaInfo java, LaunchAuth auth, string natives, string assetsRoot)
        {
            var repo = inst.Repo;
            var s = inst.Settings;
            int width = s["width"].IntOr(Settings.WindowWidth), height = s["height"].IntOr(Settings.WindowHeight);
            bool fullscreen = s["fullscreen"].BoolOr(Settings.Fullscreen);
            var server = s["server"].Str;

            var features = new Dictionary<string, bool>
            {
                ["has_custom_resolution"] = !fullscreen,
                ["is_demo_user"] = false,
                ["has_quick_plays_support"] = false,
                ["is_quick_play_multiplayer"] = !string.IsNullOrEmpty(server),
                ["is_quick_play_singleplayer"] = false,
                ["is_quick_play_realms"] = false,
            };

            // classpath
            var cp = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var j in info.Json["libraries"])
            {
                var lib = new Library(j);
                if (lib.Maven == null || !lib.Allowed || GameFiles.IsServerOnly(j)) continue;
                if (lib.IsLegacyNative && j["downloads"]["artifact"].IsNull) continue;
                var a = lib.Artifact(repo.Libraries);
                if (a == null) continue;
                if (seen.Add(a.Path)) cp.Add(a.Path);
            }
            var jar = repo.VersionJar(inst.Id);
            if (File.Exists(jar) && seen.Add(jar)) cp.Add(jar);

            var title = s["title"].Str;
            var vars = new Dictionary<string, string>
            {
                ["auth_player_name"] = auth.PlayerName,
                ["version_name"] = inst.Id,
                ["game_directory"] = inst.GameDir,
                ["assets_root"] = assetsRoot,
                ["game_assets"] = assetsRoot,
                ["assets_index_name"] = info.AssetsId,
                ["auth_uuid"] = auth.Uuid,
                ["auth_access_token"] = auth.AccessToken,
                ["auth_session"] = auth.AccessToken,
                ["clientid"] = auth.ClientId ?? "",
                ["auth_xuid"] = auth.Xuid ?? "",
                ["user_type"] = auth.UserType,
                ["user_properties"] = auth.UserProperties ?? "{}",
                ["version_type"] = s["versionType"].Str ?? Branding.DisplayName,
                ["natives_directory"] = natives,
                ["launcher_name"] = Branding.AppName,
                ["launcher_version"] = AppInfo.VersionString,
                ["classpath"] = string.Join(Path.PathSeparator, cp),
                ["classpath_separator"] = Path.PathSeparator.ToString(),
                ["library_directory"] = repo.Libraries,
                ["resolution_width"] = width.ToString(),
                ["resolution_height"] = height.ToString(),
                ["quickPlayMultiplayer"] = server ?? "",
                ["quickPlayPath"] = Path.Combine(inst.GameDir, "quickPlay", "log.json"),
                ["quickPlaySingleplayer"] = "",
                ["quickPlayRealms"] = "",
            };
            string Sub(string a) => Regex.Replace(a, @"\$\{([^}]+)\}", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

            IEnumerable<string> Expand(JNode list)
            {
                foreach (var item in list)
                {
                    if (item.IsStr) { yield return item.Str; continue; }
                    if (!Rules.Allowed(item["rules"], features)) continue;
                    var val = item["value"];
                    if (val.IsStr) yield return val.Str;
                    else foreach (var v in val.StrList()) yield return v;
                }
            }

            // ---------- JVM ----------
            var jvm = new List<string>();
            int mem = MemoryFor(inst, java);
            jvm.Add($"-Xmx{mem}m");
            jvm.Add($"-Xms{Math.Min(mem, 1024)}m");

            var userJvm = s["jvmArgs"].Str;
            if (string.IsNullOrWhiteSpace(userJvm)) userJvm = Settings.JvmArgs;
            bool userSetGc = !string.IsNullOrWhiteSpace(userJvm) && Regex.IsMatch(userJvm, @"-XX:\+Use\w+GC");

            var defaultUser = info.Json["arguments"]["default-user-jvm"];
            if (!userSetGc)
            {
                if (!defaultUser.IsNull)
                    jvm.AddRange(Expand(defaultUser).Where(a => !a.StartsWith("-Xmx") && !a.StartsWith("-Xms") && a != "-XX:+AlwaysPreTouch"));
                else
                {
                    jvm.Add("-XX:+UseG1GC");
                    jvm.Add("-XX:-UseAdaptiveSizePolicy");
                    jvm.Add("-XX:-OmitStackTraceInFastThrow");
                }
            }
            jvm.Add("-Dlog4j2.formatMsgNoLookups=true");
            jvm.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
            jvm.Add("-Dfml.ignorePatchDiscrepancies=true");
            jvm.Add("-Djdk.lang.Process.allowAmbiguousCommands=true");
            // 让游戏输出用 UTF-8，方便我们读日志、做崩溃分析
            jvm.Add("-Dsun.stdout.encoding=UTF-8");
            jvm.Add("-Dsun.stderr.encoding=UTF-8");
            if (java.Major >= 19) { jvm.Add("-Dstdout.encoding=UTF-8"); jvm.Add("-Dstderr.encoding=UTF-8"); }

            // 不使用官方的 log4j XML 配置（那是给官方启动器解析用的 XML 事件流），
            // 让游戏按默认格式输出纯文本，日志窗口和崩溃分析都更好读

            jvm.AddRange(auth.ExtraJvmArgs ?? new List<string>());
            if (!string.IsNullOrWhiteSpace(userJvm)) jvm.AddRange(Util.SplitArgs(userJvm));

            if (info.Json["arguments"]["jvm"].IsArr)
                jvm.AddRange(Expand(info.Json["arguments"]["jvm"]).Select(Sub));
            else
            {
                jvm.Add("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump");
                jvm.Add(Sub("-Djava.library.path=${natives_directory}"));
                jvm.Add(Sub("-Dminecraft.launcher.brand=${launcher_name}"));
                jvm.Add(Sub("-Dminecraft.launcher.version=${launcher_version}"));
                jvm.Add("-cp");
                jvm.Add(vars["classpath"]);
            }

            // ---------- 游戏参数 ----------
            var game = new List<string>();
            if (info.Json["arguments"]["game"].IsArr) game.AddRange(Expand(info.Json["arguments"]["game"]).Select(Sub));
            else game.AddRange(Util.SplitArgs(info.Json["minecraftArguments"].Str ?? "").Select(Sub));

            if (info.IsLegacyArguments)
            {
                if (fullscreen) game.Add("--fullscreen");
                else if (!game.Contains("--width")) { game.Add("--width"); game.Add(width.ToString()); game.Add("--height"); game.Add(height.ToString()); }
                if (!string.IsNullOrEmpty(server))
                {
                    var parts = server.Split(':');
                    game.Add("--server"); game.Add(parts[0]);
                    game.Add("--port"); game.Add(parts.Length > 1 ? parts[1] : "25565");
                }
            }
            else
            {
                if (fullscreen) game.Add("--fullscreen");
                bool hasQuickPlay = info.Json["arguments"]["game"].ToJson(false).Contains("quickPlayMultiplayer");
                if (!string.IsNullOrEmpty(server) && !hasQuickPlay)
                {
                    var parts = server.Split(':');
                    game.Add("--server"); game.Add(parts[0]);
                    game.Add("--port"); game.Add(parts.Length > 1 ? parts[1] : "25565");
                }
            }

            var userGame = s["gameArgs"].Str;
            if (string.IsNullOrWhiteSpace(userGame)) userGame = Settings.GameArgs;
            if (!string.IsNullOrWhiteSpace(userGame)) game.AddRange(Util.SplitArgs(userGame));

            return (jvm, info.MainClass, game);
        }

        static GameSession Start(Instance inst, JavaInfo java, List<string> jvm, string mainClass, List<string> game)
        {
            var all = new List<string>(jvm) { mainClass };
            all.AddRange(game);
            var argLine = string.Join(" ", all.Select(Util.QuoteArg));

            var psi = new ProcessStartInfo
            {
                FileName = java.JavawPath,
                WorkingDirectory = inst.GameDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Java 8 用系统代码页输出（简体中文 Windows 是 GBK），Java 9+ 默认 UTF-8
                StandardOutputEncoding = Platform.ConsoleEncoding(java.Major),
                StandardErrorEncoding = Platform.ConsoleEncoding(java.Major),
            };

            // 命令太长时 Java 9+ 用 @参数文件；Java 8 把 classpath 挪进环境变量。
            // Windows 是 32767 字符上限；Unix 的 ARG_MAX 也有上限，同一套兜底够用。
            string argFile = null;
            var argv = all;
            if (Platform.IsWindows && string.Join(" ", all.Select(Util.QuoteArg)).Length > 30000)
            {
                if (java.Major >= 9)
                {
                    argFile = Path.Combine(Paths.Temp, "launch-" + Guid.NewGuid().ToString("N") + ".txt");
                    File.WriteAllText(argFile, string.Join("\n", all.Select(ArgFileQuote)), Platform.ArgFileEncoding);
                    argv = new List<string> { "@" + argFile };
                }
                else
                {
                    int i = jvm.IndexOf("-cp");
                    if (i >= 0 && i + 1 < jvm.Count)
                    {
                        psi.EnvironmentVariables["CLASSPATH"] = jvm[i + 1];
                        var j2 = new List<string>(jvm);
                        j2.RemoveRange(i, 2);
                        var a2 = new List<string>(j2) { mainClass };
                        a2.AddRange(game);
                        argv = a2;
                    }
                }
            }
            // 逐个传参：Unix 上 .NET 按自己的规则拆分 Arguments 字符串，拼成一整行会踩转义坑
            foreach (var a in argv) psi.ArgumentList.Add(a);
            argLine = string.Join(" ", argv.Select(Util.QuoteArg));
            // 清掉可能干扰游戏的环境变量
            psi.EnvironmentVariables.Remove("JAVA_TOOL_OPTIONS");
            psi.EnvironmentVariables.Remove("_JAVA_OPTIONS");
            psi.EnvironmentVariables.Remove("JDK_JAVA_OPTIONS");

            var session = new GameSession { Instance = inst, Java = java, StartTime = DateTime.Now, CommandLine = MaskToken(java.JavawPath + " " + argLine), TempArgFile = argFile };
            Log.Info("启动命令：" + session.CommandLine);
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (o, e) => { if (e.Data != null) session.AddLine(e.Data); };
            proc.ErrorDataReceived += (o, e) => { if (e.Data != null) session.AddLine(e.Data); };
            proc.Exited += (o, e) =>
            {
                int code = 0;
                try { proc.WaitForExit(); code = proc.ExitCode; } catch { }
                lock (Running) Running.Remove(session);
                Accounts.LocalSkinServer.StopAll();
                Log.Info($"游戏退出，退出码 {code}");
                session.RaiseExit(code);
                // 释放进程句柄（句柄攒到 GC 前可能耗尽）；GameSession.Process 无人再读
                try { proc.Dispose(); } catch { }
            };
            session.Process = proc;
            lock (Running) Running.Add(session);
            try
            {
                if (!proc.Start()) throw new Exception("Java 进程启动失败");
            }
            catch
            {
                lock (Running) Running.Remove(session);
                Util.TryDelete(argFile);
                throw;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 等窗口出现；有自定义标题时持续改标题。
            // 改标题依赖 Win32 的 SetWindowText + MainWindowHandle，目前只在 Windows 上做。
            var title = inst.Settings["title"].Str;
            if (string.IsNullOrEmpty(title)) title = Settings.WindowTitle;
            if (Platform.IsWindows)
            Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                while (true)
                {
                    // 进程可能已在 Exited 里被 Dispose，HasExited 访问要包在 try 里
                    bool exited = true;
                    try { exited = proc.HasExited; } catch { break; }
                    if (exited) break;
                    try
                    {
                        proc.Refresh();
                        var h = proc.MainWindowHandle;
                        if (h != IntPtr.Zero)
                        {
                            if (!session.WindowShown) session.RaiseWindow();
                            if (string.IsNullOrEmpty(title)) break;
                            SetWindowText(h, title.Replace("{version}", inst.Id).Replace("{name}", inst.Id));
                        }
                    }
                    catch { }
                    await Task.Delay(session.WindowShown ? 1000 : 300).ConfigureAwait(false);
                    if (!session.WindowShown && sw.Elapsed.TotalMinutes > 10) break;
                }
            });
            return session;
        }

        static string ArgFileQuote(string a)
        {
            // Java @argfile：用双引号包住，内部的 \ 和 " 需要转义
            return "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static string MaskToken(string cmd)
        {
            // --accessToken 是现在的写法，--session 是 1.5.2 及更早老版本的写法，都要遮
            return Regex.Replace(cmd, @"(--(?:accessToken|session)\s+)(\S+)", m => m.Groups[1].Value + "********");
        }

        /// <summary>导出启动脚本（.bat），方便排查或脱离启动器直接运行</summary>
        public static async Task<string> ExportScriptAsync(Instance inst, Account account, string path, LauncherTask task)
        {
            var info = inst.Info;
            bool forgeOld = inst.Loaders.Any(l => l.Type == LoaderType.Forge) && Util.CompareVersion(info.McVersion, "1.17") < 0;
            var java = await JavaManager.EnsureAsync(info.JavaMajor, info.JavaComponent, forgeOld, task, 0, 0.5).ConfigureAwait(false);
            var auth = await AccountService.PrepareAsync(account, task, task.Token).ConfigureAwait(false);
            GameFiles.ExtractNatives(inst.Repo, info, inst.NativesDir, java.Arch);
            var assetsRoot = GameFiles.PrepareLegacyAssets(inst.Repo, info, inst.GameDir);
            var (jvm, main, game) = BuildArguments(inst, info, java, auth, inst.NativesDir, assetsRoot);
            var all = new List<string>(jvm) { main };
            all.AddRange(game);
            var javaExe = Platform.ConsoleJavaPath(java.JavawPath);
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($"rem 由 {Branding.AppName} 导出：{inst.Id}（包含账号令牌，不要发给别人）");
            sb.AppendLine($"cd /d \"{inst.GameDir}\"");
            if (java.Major >= 9)
            {
                // cmd 一行最多 8191 字符，模组版本的 classpath 远超这个长度：参数放进同名 .args 文件
                var argPath = Path.ChangeExtension(path, ".args");
                File.WriteAllText(argPath, string.Join("\n", all.Select(ArgFileQuote)), Encoding.Default);
                sb.AppendLine($"\"{javaExe}\" @\"{Path.GetFileName(argPath)}\"".Replace("@\"", "@\"%~dp0"));
            }
            else
                sb.AppendLine("\"" + javaExe + "\" " + string.Join(" ", all.Select(a => Util.QuoteArg(a).Replace("%", "%%"))));
            sb.AppendLine("pause");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); // 配合 chcp 65001；.args 仍用本地代码页，java 启动器按它读
            return path;
        }
    }
}
