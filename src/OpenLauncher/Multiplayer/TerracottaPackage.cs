using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Multiplayer
{
    /// <summary>
    /// Terracotta | 陶瓦联机官方 v0.4.2 Windows x86_64 未修改二进制的下载与受限解包。
    /// 项目：https://github.com/burningtnt/Terracotta
    /// 许可：AGPLv3 or later 及其打包/进程间通信例外；本模块仅通过 HTTP IPC 使用官方程序。
    /// </summary>
    internal static class TerracottaPackage
    {
        internal const string Version = "0.4.2";
        internal const string ExecutableName = "terracotta-0.4.2-windows-x86_64.exe";
        internal const string RuntimeName = "VCRUNTIME140.DLL";
        internal const string ArchiveName = "terracotta-0.4.2-windows-x86_64-pkg.tar.gz";
        internal const string ExpectedSha256 = "07ebe139e3ca5f74576e58b1a96efe59abdfbe148d3f1a49bfdca8b6f70745f0";
        internal const string PrimaryUrl = "https://gitee.com/burningtnt/Terracotta/releases/download/v0.4.2/" + ArchiveName;
        internal const string FallbackUrl = "https://github.com/burningtnt/Terracotta/releases/download/v0.4.2/" + ArchiveName;

        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static readonly HashSet<string> ExpectedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            ExecutableName,
            RuntimeName,
        };

        internal static string InstallDirectory => Path.Combine(Paths.Tools, "Terracotta-" + Version);
        internal static string ExecutablePath => Path.Combine(InstallDirectory, ExecutableName);
        internal static string ArchivePath => Path.Combine(Paths.Cache, ArchiveName);

        internal static bool IsInstalled()
        {
            return HasContent(ExecutablePath) && HasContent(Path.Combine(InstallDirectory, RuntimeName));
        }

        internal static async Task EnsureInstalledAsync(LauncherTask task, CancellationToken ct)
        {
            if (IsInstalled()) return;
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsInstalled()) return;
                task?.Report(0, "准备 Terracotta | 陶瓦联机…");

                // Downloader 原生支持 SHA-1/SHA-512；官方给的是 SHA-256，因此下载前后都在这里严格校验。
                if (File.Exists(ArchivePath) && !IsArchiveValid())
                {
                    Util.TryDelete(ArchivePath);
                    Util.TryDelete(ArchivePath + ".oltmp");
                }
                if (!IsArchiveValid())
                {
                    var item = new DownloadItem(PrimaryUrl, ArchivePath)
                    {
                        AltUrls = new List<string> { FallbackUrl },
                        UseMirror = false,
                    };
                    // Downloader 从 LauncherTask 取取消令牌；无 UI 任务时使用分离任务传递 ct。
                    var downloadTask = task ?? new LauncherTask { Title = "下载 Terracotta | 陶瓦联机" };
                    CancellationTokenRegistration registration = default;
                    if (task == null && ct.CanBeCanceled) registration = ct.Register(downloadTask.Cancel);
                    try
                    {
                        await Downloader.RunAsync(new[] { item }, downloadTask, 0, 0.9, "下载 Terracotta | 陶瓦联机").ConfigureAwait(false);
                    }
                    finally
                    {
                        registration.Dispose();
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (!IsArchiveValid())
                {
                    Util.TryDelete(ArchivePath);
                    Util.TryDelete(ArchivePath + ".oltmp");
                    throw new InvalidDataException("Terracotta 官方包 SHA-256 校验失败");
                }

                task?.Report(0.92, "校验并解包 Terracotta | 陶瓦联机…");
                string staging = InstallDirectory + ".installing-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(staging);
                    await Task.Run(() => TarGz.ExtractFiles(ArchivePath, staging, ExpectedFiles, ct), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(InstallDirectory);
                    foreach (string name in ExpectedFiles)
                    {
                        string target = Path.Combine(InstallDirectory, name);
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(Path.Combine(staging, name), target);
                    }
                }
                finally
                {
                    Util.TryDeleteDir(staging);
                }
                if (!IsInstalled()) throw new InvalidDataException("Terracotta 解包结果不完整");
                task?.Report(1, "Terracotta | 陶瓦联机已就绪");
                Log.Info("Terracotta | 陶瓦联机 v" + Version + " 已安装");
            }
            finally
            {
                Gate.Release();
            }
        }

        static bool IsArchiveValid()
        {
            try
            {
                return File.Exists(ArchivePath) && new FileInfo(ArchivePath).Length > 0 &&
                       string.Equals(Util.Sha256File(ArchivePath), ExpectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        static bool HasContent(string path)
        {
            try { return File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }
    }
}
