using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>
    /// 不运行 Forge 安装器的图形界面，而是自己解析 install_profile.json：
    /// 下载依赖 → 解出内置文件 → 逐个执行 processors。这样下载能走镜像、能并行、能复用。
    /// 同时支持老格式（1.12.2 及以前：versionInfo + install.filePath）和新格式（1.13+ / NeoForge）。
    /// </summary>
    public static partial class Installer
    {
        class ForgeContext : IDisposable
        {
            public string InstallerPath;
            public string TempDir;
            public JNode Profile;
            public JNode VersionJson;
            public bool Legacy;
            public LoaderType Loader;

            public List<DownloadItem> ProfileLibraries(GameRepo repo)
            {
                var list = new List<DownloadItem>();
                if (Legacy) return list;
                foreach (var j in Profile["libraries"])
                {
                    var lib = new Library(j);
                    var a = lib.Artifact(repo.Libraries);
                    if (a != null && !string.IsNullOrEmpty(a.Url)) list.Add(a);
                }
                return list;
            }

            public void Dispose()
            {
                Util.TryDeleteDir(TempDir);
                Util.TryDelete(InstallerPath);
            }
        }

        static async Task<string> ForgeInstallerUrlAsync(InstallRequest req, System.Threading.CancellationToken ct)
        {
            if (req.Loader == LoaderType.NeoForge)
            {
                if (req.McVersion == "1.20.1")
                {
                    var full = req.LoaderVersion.StartsWith("1.20.1-") ? req.LoaderVersion : "1.20.1-" + req.LoaderVersion;
                    return $"https://maven.neoforged.net/releases/net/neoforged/forge/{full}/forge-{full}-installer.jar";
                }
                return $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{req.LoaderVersion}/neoforge-{req.LoaderVersion}-installer.jar";
            }
            // Forge：老版本的 maven 版本号带后缀（1.7.10-10.13.4.1614-1.7.10），从列表里查完整写法
            var fullVer = $"{req.McVersion}-{req.LoaderVersion}";
            try
            {
                var list = await VersionList.GetLoaderVersionsAsync(LoaderType.Forge, req.McVersion, ct).ConfigureAwait(false);
                var hit = list.FirstOrDefault(e => e.Version == req.LoaderVersion || e.FullVersion == req.LoaderVersion);
                if (hit?.FullVersion != null) fullVer = hit.FullVersion;
            }
            catch (Exception ex) { Log.Warn("查询 Forge 完整版本号失败，按默认格式拼接：" + ex.Message); }
            return $"https://maven.minecraftforge.net/net/minecraftforge/forge/{fullVer}/forge-{fullVer}-installer.jar";
        }

        /// <summary>
        /// 拉取安装器 jar 的官方 SHA-1（url + ".sha1"）。先按镜像设置竞速，失败再强制官方源；
        /// 拿不到可信的校验值就拒绝安装——宁可装不上，也不跑来源不明的安装器。
        /// </summary>
        static async Task<string> FetchInstallerSha1Async(string url, System.Threading.CancellationToken ct)
        {
            Exception last = null;
            foreach (var mirror in new[] { true, false })
            {
                try
                {
                    var text = await Http.GetStringAsync(url + ".sha1", ct, null, mirror, 15, LooksLikeSha1File).ConfigureAwait(false);
                    var hash = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (hash != null && hash.Length == 40 && hash.All(Uri.IsHexDigit))
                        return hash.ToLowerInvariant();
                    last = new FormatException("sha1 文件内容无法识别");
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    last = ex;
                    Log.Warn($"获取安装器 SHA-1 失败（{(mirror ? "镜像优先" : "官方源")}）：{ex.Message}");
                }
            }
            var file = Path.GetFileName(url);
            throw new Exception($"拿不到 {file} 的 SHA-1 校验值，已停止安装（请检查网络后重试）" + (last != null ? "：" + last.Message : ""));
        }

        /// <summary>maven .sha1 副车是纯文本 "<40位十六进制> 文件名"，不是网页</summary>
        static bool LooksLikeSha1File(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var i = 0;
            while (i < text.Length && text[i] != ' ' && text[i] != '\t' && text[i] != '\r' && text[i] != '\n') i++;
            if (i != 40) return false;
            for (int k = 0; k < i; k++) if (!Uri.IsHexDigit(text[k])) return false;
            return true;
        }

        static async Task<ForgeContext> PrepareForgeAsync(GameRepo repo, InstallRequest req, LauncherTask task, double from, double to)
        {
            var ct = task?.Token ?? default;
            var name = LoaderInfo.Name(req.Loader);
            task?.Report(from, $"下载 {name} 安装器…");
            var url = await ForgeInstallerUrlAsync(req, ct).ConfigureAwait(false);
            // 安装器里装着要以用户权限执行的 processors，下载必须校验官方 sha1（Maven 构件旁挂 .sha1 副车，镜像同样提供）
            var sha1 = await FetchInstallerSha1Async(url, ct).ConfigureAwait(false);
            var ctx = new ForgeContext
            {
                Loader = req.Loader,
                InstallerPath = Path.Combine(Paths.Temp, $"{name}-{req.McVersion}-{req.LoaderVersion}-{Guid.NewGuid():N}.jar"),
                TempDir = Path.Combine(Paths.Temp, "forge-" + Guid.NewGuid().ToString("N")),
            };
            Directory.CreateDirectory(ctx.TempDir);
            await Downloader.DownloadFileAsync(new DownloadItem(url, ctx.InstallerPath, sha1), task, from, to).ConfigureAwait(false);

            using (var zip = ZipFile.OpenRead(ctx.InstallerPath))
            {
                var profileText = Util.ReadZipText(zip, "install_profile.json") ?? throw new Exception($"{name} 安装器里没有 install_profile.json，可能下载损坏");
                ctx.Profile = JNode.Parse(profileText);

                if (!ctx.Profile["versionInfo"].IsNull)
                {
                    // 老格式：versionInfo 就是版本 JSON，universal jar 直接解到 libraries
                    ctx.Legacy = true;
                    ctx.VersionJson = ctx.Profile["versionInfo"];
                    var install = ctx.Profile["install"];
                    var filePath = install["filePath"].Str;
                    var target = repo.LibraryPath(install["path"].Str);
                    if (filePath != null && target != null)
                    {
                        var entry = zip.GetEntry(filePath) ?? throw new Exception("安装器缺少 " + filePath);
                        Util.EnsureParent(target);
                        entry.ExtractToFile(target, true);
                    }
                }
                else
                {
                    var jsonPath = (ctx.Profile["json"].Str ?? "/version.json").TrimStart('/');
                    var vtext = Util.ReadZipText(zip, jsonPath) ?? throw new Exception($"{name} 安装器里没有 {jsonPath}");
                    ctx.VersionJson = JNode.Parse(vtext);
                    // 安装器自带的 maven/ 目录（里面有一些服务器上没有的库）
                    Util.ExtractPrefix(zip, "maven/", repo.Libraries);
                }
            }
            return ctx;
        }

        static async Task RunProcessorsAsync(GameRepo repo, ForgeContext ctx, VersionInfo info, string mc, LauncherTask task, double from, double to)
        {
            if (ctx.Legacy) { task?.Report(to); return; }
            var ct = task?.Token ?? default;
            var processors = ctx.Profile["processors"].Where(p =>
            {
                var sides = p["sides"];
                return sides.IsNull || sides.StrList().Contains("client");
            }).ToList();
            if (processors.Count == 0) { task?.Report(to); return; }

            // 处理器需要 Java；用这个版本本身要求的 Java
            task?.Report(from, "准备 Java…");
            bool forgeOld = ctx.Loader == LoaderType.Forge && Util.CompareVersion(mc, "1.17") < 0;
            var java = await JavaManager.EnsureAsync(info.JavaMajor, info.JavaComponent, forgeOld, task, from, from + (to - from) * 0.15).ConfigureAwait(false);
            if (java == null) throw new Exception("没有可用的 Java，无法完成安装");
            var javaExe = Path.Combine(Path.GetDirectoryName(java.JavawPath), "java.exe");
            if (!File.Exists(javaExe)) javaExe = java.JavawPath;

            // 组装 data 变量表
            var data = new Dictionary<string, string>
            {
                ["SIDE"] = "client",
                ["MINECRAFT_JAR"] = repo.VersionJar(info.Id),
                ["MINECRAFT_VERSION"] = mc,
                ["ROOT"] = repo.Root,
                ["INSTALLER"] = ctx.InstallerPath,
                ["LIBRARY_DIR"] = repo.Libraries,
            };
            using (var zip = ZipFile.OpenRead(ctx.InstallerPath))
            {
                foreach (var kv in ctx.Profile["data"].Props)
                {
                    var v = kv.Value["client"].Str;
                    if (v == null) continue;
                    if (v.StartsWith("[") && v.EndsWith("]")) data[kv.Key] = repo.LibraryPath(v.Substring(1, v.Length - 2));
                    else if (v.StartsWith("'") && v.EndsWith("'")) data[kv.Key] = v.Substring(1, v.Length - 2);
                    else if (v.StartsWith("/"))
                    {
                        var dst = Path.Combine(ctx.TempDir, v.TrimStart('/').Replace('/', '\\'));
                        var entry = zip.GetEntry(v.TrimStart('/'));
                        if (entry != null) { Util.EnsureParent(dst); entry.ExtractToFile(dst, true); }
                        data[kv.Key] = dst;
                    }
                    else data[kv.Key] = v;
                }
            }

            string Resolve(string arg)
            {
                if (arg.StartsWith("[") && arg.EndsWith("]")) return repo.LibraryPath(arg.Substring(1, arg.Length - 2));
                return Regex.Replace(arg, @"\{(\w+)\}", m => data.TryGetValue(m.Groups[1].Value, out var val) ? val : m.Value);
            }

            for (int i = 0; i < processors.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = processors[i];
                double pf = from + (to - from) * (0.15 + 0.85 * i / processors.Count);
                var jarName = p["jar"].Str;
                var args = p["args"].StrList().Select(Resolve).ToList();
                task?.Report(pf, $"执行安装步骤 {i + 1}/{processors.Count}：{ShortProcessorName(jarName, args)}");

                // 已有输出且校验通过就跳过（可重复安装）
                var outputs = p["outputs"];
                if (outputs.Count > 0 && outputs.Props.All(o =>
                {
                    var path = Resolve(o.Key);
                    var sha = Resolve(o.Value.Str ?? "").Trim('\'');
                    return File.Exists(path) && string.Equals(Util.Sha1File(path), sha, StringComparison.OrdinalIgnoreCase);
                })) continue;

                // DOWNLOAD_MOJMAPS 由我们自己下（走镜像，比处理器直连快得多）
                var taskIdx = args.IndexOf("--task");
                if (taskIdx >= 0 && taskIdx + 1 < args.Count && args[taskIdx + 1] == "DOWNLOAD_MOJMAPS")
                {
                    var outIdx = args.IndexOf("--output");
                    var mappings = info.Json["downloads"]["client_mappings"];
                    if (outIdx >= 0 && !mappings.IsNull)
                    {
                        await Downloader.DownloadFileAsync(new DownloadItem(mappings["url"].Str, args[outIdx + 1], mappings["sha1"].Str, mappings["size"].Long), task, pf, pf).ConfigureAwait(false);
                        continue;
                    }
                }

                var jarPath = repo.LibraryPath(jarName);
                if (!File.Exists(jarPath)) throw new Exception($"安装器依赖缺失：{jarName}");
                var cp = new List<string> { jarPath };
                foreach (var c in p["classpath"].StrList())
                {
                    var cpPath = repo.LibraryPath(c);
                    if (!File.Exists(cpPath)) throw new Exception($"安装器依赖缺失：{c}");
                    cp.Add(cpPath);
                }
                string mainClass;
                using (var zip = ZipFile.OpenRead(jarPath))
                {
                    var mf = Util.ReadZipText(zip, "META-INF/MANIFEST.MF") ?? "";
                    mainClass = Regex.Match(mf, @"^Main-Class:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value;
                }
                if (string.IsNullOrEmpty(mainClass)) throw new Exception($"{jarName} 没有 Main-Class");

                var cmd = new List<string> { "-cp", string.Join(";", cp), mainClass };
                cmd.AddRange(args);
                var (code, output) = await RunJavaAsync(javaExe, cmd, repo.Root, ct).ConfigureAwait(false);
                if (code != 0)
                {
                    var tail = string.Join("\n", output.Split('\n').Reverse().Take(15).Reverse());
                    throw new Exception($"安装步骤失败（{ShortProcessorName(jarName, args)}，退出码 {code}）：\n{tail}");
                }
            }
            task?.Report(to);
        }

        static string ShortProcessorName(string jar, List<string> args)
        {
            var m = Maven.Parse(jar);
            var t = args.IndexOf("--task");
            return (m?.Artifact ?? jar) + (t >= 0 && t + 1 < args.Count ? " " + args[t + 1] : "");
        }

        internal static Task<(int code, string output)> RunJavaPublicAsync(string java, List<string> args, string workDir, CancellationToken ct)
            => RunJavaAsync(java, args, workDir, ct);

        static Task<(int code, string output)> RunJavaAsync(string java, List<string> args, string workDir, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(java, string.Join(" ", args.Select(Util.QuoteArg)))
                {
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                var sb = new StringBuilder();
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    using (ct.Register(() => { try { proc.Kill(); } catch { } }))
                        proc.WaitForExit();
                    proc.WaitForExit();
                    ct.ThrowIfCancellationRequested();
                    var text = sb.ToString();
                    Log.Info($"处理器输出（退出码 {proc.ExitCode}）：\n" + (text.Length > 4000 ? text.Substring(text.Length - 4000) : text));
                    return (proc.ExitCode, text);
                }
            }, ct);
        }
    }
}
