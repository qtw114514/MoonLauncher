using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenLauncher.Core
{
    public class DownloadItem
    {
        /// <summary>官方地址（会按镜像表自动展开）</summary>
        public string Url { get; set; }
        /// <summary>额外的备用地址（不同来源的同一个文件）</summary>
        public List<string> AltUrls { get; set; }
        public string Path { get; set; }
        public string Sha1 { get; set; }
        public string Sha512 { get; set; }
        public long Size { get; set; }
        public bool UseMirror { get; set; } = true;
        /// <summary>覆盖已有目标前先把旧文件移入回收站；默认仍直接删除。</summary>
        public bool RecycleExisting { get; set; } = false;
        public string Name => System.IO.Path.GetFileName(Path);

        public DownloadItem() { }
        public DownloadItem(string url, string path, string sha1 = null, long size = 0)
        { Url = url; Path = path; Sha1 = sha1; Size = size; }

        public List<string> Candidates()
        {
            var list = new List<string>();
            void add(string u)
            {
                if (string.IsNullOrEmpty(u)) return;
                foreach (var x in UseMirror ? Mirrors.Expand(u) : new List<string> { u })
                    if (!list.Contains(x)) list.Add(x);
            }
            add(Url);
            if (AltUrls != null) foreach (var u in AltUrls) add(u);
            return list;
        }

        /// <summary>本地文件已存在且校验通过。quick=true 时只核对大小（启动前检查用，几千个资源文件不用逐个算哈希）</summary>
        public bool IsValid(bool quick = false)
        {
            var p = Util.LP(Path);
            if (!File.Exists(p)) return false;
            var len = new FileInfo(p).Length;
            if (Size > 0 && len != Size) return false;
            if (len == 0 && Size != 0) return false;
            if (quick) return true;
            if (!string.IsNullOrEmpty(Sha1)) return string.Equals(Util.Sha1File(Path), Sha1, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(Sha512)) return string.Equals(Util.Sha512File(Path), Sha512, StringComparison.OrdinalIgnoreCase);
            return true;
        }
    }

    /// <summary>
    /// 多线程、多来源、带校验的下载器。所有游戏文件 / Mod / Java 都走这里。
    /// </summary>
    public static class Downloader
    {
        static long _limiterBytes;
        static long _limiterTick = Stopwatch.GetTimestamp();
        static readonly object LimiterLock = new object();

        /// <summary>批量下载。progressFrom/progressTo 用于把这一批映射到任务总进度的一段。</summary>
        public static async Task RunAsync(IList<DownloadItem> items, LauncherTask task, double progressFrom = 0, double progressTo = 1, string label = "下载文件", bool quickCheck = false)
        {
            var ct = task?.Token ?? CancellationToken.None;
            items = items.Where(i => i != null && !string.IsNullOrEmpty(i.Path)).GroupBy(i => i.Path.ToLowerInvariant()).Select(g => g.First()).ToList();

            // 先并行校验已有文件
            task?.Report(progressFrom, $"{label}：检查已有文件…");
            var todo = new ConcurrentBag<DownloadItem>();
            await Task.Run(() => Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, i =>
            {
                if (!i.IsValid(quickCheck)) todo.Add(i);
            }), ct).ConfigureAwait(false);

            var list = todo.ToList();
            if (list.Count == 0) { task?.Report(progressTo); return; }

            long avg = Math.Max(64 * 1024, (long)(list.Where(i => i.Size > 0).Select(i => (double)i.Size).DefaultIfEmpty(256 * 1024).Average()));
            long total = list.Sum(i => i.Size > 0 ? i.Size : avg);
            long doneBytes = 0;
            int doneFiles = 0;
            var errors = new ConcurrentQueue<string>();

            var sem = new SemaphoreSlim(Settings.DownloadThreads);
            long lastBytes = 0;
            var sw = Stopwatch.StartNew();
            bool waitingNet = false; // 有任务在等网恢复时，任务详情显示"等待网络恢复…"
            using (var timer = new Timer(_ =>
            {
                var b = Interlocked.Read(ref doneBytes);
                var secs = sw.Elapsed.TotalSeconds;
                sw.Restart();
                if (task != null && secs > 0)
                {
                    task.Speed = task.State == TaskState.Paused ? 0 : Math.Max(0, (long)((b - lastBytes) / secs));
                    var fraction = total > 0 ? Math.Max(0, Math.Min(1, (double)b / total)) : 1;
                    task.Report(progressFrom + (progressTo - progressFrom) * fraction,
                        waitingNet ? $"{label}：等待网络恢复…（{doneFiles}/{list.Count}）" : $"{label}：{doneFiles}/{list.Count}");
                }
                lastBytes = b;
            }, null, 500, 700))
            {
                var jobs = list.Select(async item =>
                {
                    if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    // 固定临时名让任务失败/取消后的下一次运行也能从同一文件断点续传
                    var tmp = item.Path + ".oltmp";
                    long counted = TempLength(tmp);
                    if (counted > 0) Interlocked.Add(ref doneBytes, counted);
                    try
                    {
                        if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                        while (true)
                        {
                            try
                            {
                                await DownloadOneAsync(item, tmp, task, ct, n => { counted += n; Interlocked.Add(ref doneBytes, n); }).ConfigureAwait(false);
                                break;
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested)
                            {
                                throw; // 保留部分文件，重试时继续 Range 下载
                            }
                            catch (NetworkDownException)
                            {
                                // 网断了：进度保留在临时文件里，挂起等网络恢复后重试（下载器会发 Range 续传）
                                waitingNet = true;
                                if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                                await NetWatch.WaitOnlineAsync(ct).ConfigureAwait(false);
                                if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                                waitingNet = false;
                            }
                        }
                        // 把未知大小的文件按平均值记账，保证进度条能走满
                        long expect = item.Size > 0 ? item.Size : avg;
                        Interlocked.Add(ref doneBytes, expect - counted);
                        Interlocked.Increment(ref doneFiles);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        // 普通失败也保留部分文件；只有校验失败会在 DownloadOneAsync 中删除
                        errors.Enqueue($"{item.Name}：{ex.Message}");
                        Log.Error($"下载失败 {item.Url} -> {item.Path}", ex);
                    }
                    finally { sem.Release(); }
                }).ToList();
                await Task.WhenAll(jobs).ConfigureAwait(false);
            }
            task?.Report(progressTo, $"{label}：{doneFiles}/{list.Count}");
            if (task != null) task.Speed = 0;

            if (!errors.IsEmpty)
            {
                var errs = errors.ToArray();
                throw new Exception($"{errs.Length} 个文件下载失败（已尝试所有镜像）：\n" + string.Join("\n", errs.Take(5)) + (errs.Length > 5 ? "\n…" : ""));
            }
        }

        public static Task DownloadFileAsync(DownloadItem item, LauncherTask task, double from = 0, double to = 1)
            => RunAsync(new[] { item }, task, from, to, "下载 " + item.Name);

        static long TempLength(string path)
        {
            try
            {
                var p = Util.LP(path);
                return File.Exists(p) ? new FileInfo(p).Length : 0;
            }
            catch { return 0; }
        }

        static async Task DownloadOneAsync(DownloadItem item, string tmp, LauncherTask task, CancellationToken ct, Action<long> onBytes)
        {
            var candidates = item.Candidates();
            if (candidates.Count == 0) throw new Exception("没有下载地址");
            Exception last = null;
            Util.EnsureParent(item.Path);

            foreach (var url in candidates)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await FetchToFile(url, tmp, task, ct, onBytes).ConfigureAwait(false);
                        var check = new DownloadItem { Path = tmp, Sha1 = item.Sha1, Sha512 = item.Sha512, Size = item.Size };
                        if (!check.IsValid())
                        {
                            // 校验不通过的缓存绝不能留到下次 Range，否则会反复拼出坏文件
                            var invalidBytes = TempLength(tmp);
                            Util.TryDelete(tmp);
                            onBytes(-invalidBytes);
                            throw new InvalidDataException("文件校验失败");
                        }
                        if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                        var dest = Util.LP(item.Path);
                        try
                        {
                            if (File.Exists(dest))
                            {
                                if (item.RecycleExisting) RecycleBin.Delete(dest);
                                else File.Delete(dest);
                            }
                            File.Move(Util.LP(tmp), dest);
                        }
                        catch (IOException) when (new DownloadItem { Path = item.Path, Sha1 = item.Sha1, Sha512 = item.Sha512, Size = item.Size }.IsValid())
                        {
                            Util.TryDelete(tmp); // 别的任务已经放好了同一个文件
                        }
                        Mirrors.ReportSuccess(url);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (HttpStatusException ex) when (ex.Status == 404 || ex.Status == 403 || ex.Status == 410)
                    {
                        last = ex;
                        Mirrors.ReportFailure(url);
                        break; // 这个地址没有这个文件，保留已下载部分并换下一个来源
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        Mirrors.ReportFailure(url);
                        if (NetWatch.ProbablyOffline(ex))
                        {
                            // 网络问题：不删临时文件（进度在里面），交给外层挂起等网恢复后断点续传
                            throw new NetworkDownException(ex);
                        }
                        // 其他失败同样保留部分文件；若是校验失败，上面已经先删掉了
                    }
                }
            }
            throw last ?? new Exception("下载失败");
        }

        static async Task FetchToFile(string url, string path, LauncherTask task, CancellationToken ct, Action<long> onBytes)
        {
            string lp = Util.LP(path);
            // 断点续传：临时文件里已有上次下到的部分就从那里接着下
            long pos = TempLength(path);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 秒连不上就换下一个来源
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (pos > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(pos, null);
                using (var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                // .NET Framework 的响应流读取不理会取消令牌：超时 / 用户取消时直接释放响应，让卡住的读取立刻抛出
                using (cts.Token.Register(() => { try { resp.Dispose(); } catch { } }))
                {
                    // 416 = 区间不满足：临时文件很可能已经完整，让后续校验去判定
                    if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                        throw new HttpStatusException(url, (int)resp.StatusCode, null);
                    if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && pos > 0) return;
                    // 有的镜像出错时返回 200 + 网页；要的不是网页就当这个来源失败
                    var mt = resp.Content.Headers.ContentType?.MediaType ?? "";
                    // 临时文件名是“原文件名.oltmp”，取原文件的扩展名
                    var real = path.EndsWith(".oltmp", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - ".oltmp".Length) : path;
                    var ext = System.IO.Path.GetExtension(real).ToLowerInvariant();
                    if (mt.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) && ext != ".html" && ext != ".htm")
                        throw new InvalidDataException("下载源返回了网页而不是文件");
                    var append = resp.StatusCode == System.Net.HttpStatusCode.PartialContent && pos > 0;
                    var discard = !append && pos > 0;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(lp, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                    {
                        // Create 已成功截断旧文件后才回滚旧字节，避免服务器忽略 Range 时重复累计
                        if (discard) onBytes(-pos);
                        var buf = new byte[1 << 16];
                        while (true)
                        {
                            if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                            // 25 秒没收到数据就算卡死，换下一个来源
                            cts.CancelAfter(TimeSpan.FromSeconds(25));
                            int n;
                            try { n = await src.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false); }
                            catch (Exception) when (cts.IsCancellationRequested)
                            {
                                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                                throw new TimeoutException("下载卡住超过 25 秒");
                            }
                            catch (IOException ex) { throw new NetStreamException(ex); } // 网络流断了（磁盘错误不会走到这）
                            if (n <= 0) break;
                            if (task != null) await task.WaitIfPausedAsync(ct).ConfigureAwait(false);
                            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                            onBytes(n);
                            await ThrottleAsync(n, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        /// <summary>全局限速（设置里的 KB/s，0 表示不限）</summary>
        static async Task ThrottleAsync(int bytes, CancellationToken ct)
        {
            int limitKb = Settings.SpeedLimitKb;
            if (limitKb <= 0) return;
            long limit = limitKb * 1024L;
            int waitMs;
            lock (LimiterLock)
            {
                var now = Stopwatch.GetTimestamp();
                double elapsed = (now - _limiterTick) / (double)Stopwatch.Frequency;
                if (elapsed >= 1) { _limiterTick = now; _limiterBytes = 0; elapsed = 0; }
                _limiterBytes += bytes;
                waitMs = _limiterBytes > limit ? (int)((1 - elapsed) * 1000) : 0;
            }
            if (waitMs > 0) await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }
}
