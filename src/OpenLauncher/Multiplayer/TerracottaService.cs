using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenLauncher.Core;

namespace OpenLauncher.Multiplayer
{
    /// <summary>
    /// Terracotta | 陶瓦联机业务服务。仅连接 http://127.0.0.1:{port}，通过官方 HTTP API
    /// （/meta、/state、/state/scanning、/state/guesting、/state/ide、/panic、/log）交互。
    /// 官方项目：https://github.com/burningtnt/Terracotta
    /// 官方程序采用 AGPLv3 or later 及打包/IPC 例外；UI 必须明显标识
    /// “Terracotta | 陶瓦联机”及上述项目链接。本服务不实现或模拟 P2P。
    /// </summary>
    public static class TerracottaService
    {
        public const string ProjectUrl = "https://github.com/burningtnt/Terracotta";
        public const string Attribution = "Terracotta | 陶瓦联机 — " + ProjectUrl;

        const int PollIntervalMs = 500;
        const int MaxPollFailures = 5;
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        static readonly object Sync = new object();

        static Process _process;                 // null 表示复用了非本服务启动的进程
        static CancellationTokenSource _pollCts;
        static Task _pollTask;
        static int _port;
        static string _portFile;
        static long _lastIndex = -1;
        static long _generation;
        static bool _closing;
        static TerracottaState _current = TerracottaState.ProcessUnavailable(-1, "陶瓦联机尚未启动");

        /// <summary>在后台线程触发；需要更新 UI 时由调用方自行切换到 UI 线程。</summary>
        public static event Action<TerracottaState> StateChanged;

        public static TerracottaState Current { get { lock (Sync) return _current; } }
        public static int Port { get { lock (Sync) return _port; } }
        public static bool IsAvailable { get { lock (Sync) return _current.IsAvailable && _port > 0; } }

        /// <summary>下载官方包、校验固定 SHA-256，并只解包官方 exe 与 VCRUNTIME140.DLL。</summary>
        public static Task EnsureInstalledAsync(LauncherTask task = null, CancellationToken ct = default)
            => TerracottaPackage.EnsureInstalledAsync(task, ct);

        public static Task StartAsync(LauncherTask task = null, CancellationToken ct = default)
            => EnsureStartedAsync(task, ct);

        /// <summary>启动 Terracotta，或复用 lock 文件中发现的已有官方进程。</summary>
        public static async Task EnsureStartedAsync(LauncherTask task = null, CancellationToken ct = default)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int current = Port;
                if (current > 0 && await ProbeAsync(current, ct).ConfigureAwait(false))
                {
                    StartPolling(current);
                    return;
                }

                await StopPollingAsync().ConfigureAwait(false);
                DetachProcess();

                int existing = ReadLockPort();
                if (existing > 0 && await ProbeAsync(existing, ct).ConfigureAwait(false))
                {
                    Attach(existing, null, null);
                    Log.Info("复用正在运行的 Terracotta | 陶瓦联机进程");
                    return;
                }

                await TerracottaPackage.EnsureInstalledAsync(task, ct).ConfigureAwait(false);
                task?.Report(0.95, "启动 Terracotta | 陶瓦联机…");
                string portFile = Path.Combine(Paths.Temp, "terracotta-" + Guid.NewGuid().ToString("N") + ".json");
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = TerracottaPackage.ExecutablePath,
                        Arguments = "--hmcl " + Util.QuoteArg(portFile),
                        WorkingDirectory = TerracottaPackage.InstallDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                    EnableRaisingEvents = true,
                };
                process.Exited += OnProcessExited;
                Attach(0, process, portFile);
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("无法启动 Terracotta 进程");
                    int discovered = await DiscoverPortAsync(process, portFile, ct).ConfigureAwait(false);
                    Attach(discovered, process, portFile);
                    task?.Report(1, "Terracotta | 陶瓦联机已启动");
                    Log.Info($"Terracotta | 陶瓦联机已启动，仅连接 127.0.0.1:{discovered}");
                }
                catch
                {
                    try { process.Exited -= OnProcessExited; } catch { }
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    process.Dispose();
                    Util.TryDelete(portFile);
                    DetachProcess();
                    throw;
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>开始房主扫描；room 为空时由 Terracotta 官方进程生成房间码。</summary>
        public static async Task HostAsync(string player, string room = null, CancellationToken ct = default)
        {
            player = ValidatePlayer(player);
            room = string.IsNullOrWhiteSpace(room) ? null : NormalizeRoom(room);
            await EnsureStartedAsync(null, ct).ConfigureAwait(false);
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int port = RequirePort();
                await RequireWaitingAsync(port, ct).ConfigureAwait(false);
                string query = Http.Query(("player", player), ("room", room));
                await GetTextAsync(port, "/state/scanning?" + query, ct, 10).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>加入房间。房间码按官方 Base34、分组和 7 的倍数规则校验。</summary>
        public static async Task JoinAsync(string room, string player, CancellationToken ct = default)
        {
            room = NormalizeRoom(room);
            player = ValidatePlayer(player);
            await EnsureStartedAsync(null, ct).ConfigureAwait(false);
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int port = RequirePort();
                await RequireWaitingAsync(port, ct).ConfigureAwait(false);
                string query = Http.Query(("room", room), ("player", player));
                await GetTextAsync(port, "/state/guesting?" + query, ct, 10).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>请求官方 /state/ide，将状态重置为 waiting。</summary>
        public static async Task ResetAsync(CancellationToken ct = default)
        {
            await EnsureStartedAsync(null, ct).ConfigureAwait(false);
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await GetTextAsync(RequirePort(), "/state/ide", ct, 10).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>读取官方 /meta 原始 JSON。</summary>
        public static async Task<JNode> GetMetadataAsync(CancellationToken ct = default)
        {
            await EnsureStartedAsync(null, ct).ConfigureAwait(false);
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GetJsonAsync(RequirePort(), "/meta", ct, 10).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>读取官方 /log?fetch=true 返回的日志文本。</summary>
        public static async Task<string> GetLogAsync(CancellationToken ct = default)
        {
            await EnsureStartedAsync(null, ct).ConfigureAwait(false);
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await GetTextAsync(RequirePort(), "/log?fetch=true", ct, 20).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>关闭轮询，并请求官方 /panic?peaceful=true 退出进程。</summary>
        public static async Task ShutdownAsync(CancellationToken ct = default)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                int port;
                lock (Sync) { _closing = true; port = _port; }
                await StopPollingAsync().ConfigureAwait(false);
                try
                {
                    if (port > 0)
                    {
                        try
                        {
                            await GetTextAsync(port, "/panic?peaceful=true", ct, 5).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // 和平退出可能让连接在响应前断开；记录真实结果，不伪造官方状态。
                            Log.Warn("Terracotta 和平退出请求未正常返回：" + TaskManager.Flatten(ex));
                        }
                    }
                }
                finally
                {
                    DetachProcess();
                    lock (Sync) _closing = false;
                    PublishUnavailable("陶瓦联机已停止", null);
                }
            }
            finally
            {
                lock (Sync) _closing = false;
                Gate.Release();
            }
        }

        /// <summary>校验并规范化官方 U/XXXX-XXXX-XXXX-XXXX 房间码；兼容小写及 I→1、O→0。</summary>
        public static bool TryNormalizeRoomCode(string room, out string normalized)
        {
            normalized = null;
            if (room == null) return false;
            char[] code = room.Trim().ToUpperInvariant().ToCharArray();
            if (code.Length != 21 || code[0] != 'U' || code[1] != '/') return false;
            int remainder = 0;
            for (int i = 2; i < code.Length; i++)
            {
                int position = i - 2;
                if (position == 4 || position == 9 || position == 14)
                {
                    if (code[i] != '-') return false;
                    continue;
                }
                if (code[i] == 'I') code[i] = '1';
                else if (code[i] == 'O') code[i] = '0';
                int digit = "0123456789ABCDEFGHJKLMNPQRSTUVWXYZ".IndexOf(code[i]);
                if (digit < 0) return false;
                remainder = (remainder * 34 + digit) % 7;
            }
            if (remainder != 0) return false;
            normalized = new string(code);
            return true;
        }

        static string NormalizeRoom(string room)
        {
            if (!TryNormalizeRoomCode(room, out string normalized))
                throw new ArgumentException("房间码无效，应为 U/XXXX-XXXX-XXXX-XXXX", nameof(room));
            return normalized;
        }

        static string ValidatePlayer(string player)
        {
            if (string.IsNullOrWhiteSpace(player)) throw new ArgumentException("玩家名不能为空", nameof(player));
            player = player.Trim();
            foreach (char c in player)
                if (char.IsControl(c)) throw new ArgumentException("玩家名含有控制字符", nameof(player));
            return player;
        }

        static async Task RequireWaitingAsync(int port, CancellationToken ct)
        {
            var state = TerracottaState.Parse(await GetJsonAsync(port, "/state", ct, 10).ConfigureAwait(false));
            long generation;
            lock (Sync) generation = _generation;
            AcceptState(state, generation);
            if (state.State != TerracottaState.Waiting)
                throw new InvalidOperationException("Terracotta 当前不是等待状态，请先重置再建房或加入");
        }

        static async Task<JNode> GetJsonAsync(int port, string path, CancellationToken ct, int timeoutSec)
        {
            string text = await GetTextAsync(port, path, ct, timeoutSec).ConfigureAwait(false);
            return JNode.Parse(text);
        }

        static Task<string> GetTextAsync(int port, string path, CancellationToken ct, int timeoutSec)
        {
            if (port <= 0 || port > 65535) throw new InvalidOperationException("Terracotta 本地端口无效");
            return Http.GetStringAsync($"http://127.0.0.1:{port}{path}", ct, null, mirror: false, timeoutSec: timeoutSec);
        }

        static async Task<bool> ProbeAsync(int port, CancellationToken ct)
        {
            if (port <= 0 || port > 65535) return false;
            try
            {
                var meta = await GetJsonAsync(port, "/meta", ct, 2).ConfigureAwait(false);
                if (!meta.IsObj || string.IsNullOrWhiteSpace(meta["version"].Str)) return false;
                string targetOs = meta["target_os"].Str;
                return string.IsNullOrEmpty(targetOs) || string.Equals(targetOs, "windows", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        static async Task<int> DiscoverPortAsync(Process process, string portFile, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                ct.ThrowIfCancellationRequested();
                try { if (process.HasExited) throw new InvalidOperationException("Terracotta 进程在 HTTP 服务就绪前退出"); }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException("无法读取 Terracotta 进程状态", ex); }

                int port = ReadJsonPort(portFile);
                if (port <= 0) port = ReadLockPort();
                if (port > 0 && await ProbeAsync(port, ct).ConfigureAwait(false)) return port;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("等待 Terracotta 本地 HTTP 服务就绪超时");
        }

        static int ReadJsonPort(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
                int port = JNode.Parse(text)["port"].Int;
                return port > 0 && port <= 65535 ? port : 0;
            }
            catch
            {
                return 0;
            }
        }

        static int ReadLockPort()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "terracotta", "terracotta.lock");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int high = stream.ReadByte(), low = stream.ReadByte();
                    if (high < 0 || low < 0) return 0;
                    int port = high << 8 | low;
                    return port > 0 ? port : 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        static int RequirePort()
        {
            lock (Sync)
            {
                if (_port <= 0) throw new InvalidOperationException("Terracotta 尚未启动");
                return _port;
            }
        }

        static void Attach(int port, Process process, string portFile)
        {
            lock (Sync)
            {
                _port = port;
                _process = process;
                _portFile = portFile;
            }
            if (port > 0) StartPolling(port);
        }

        static void StartPolling(int port)
        {
            lock (Sync)
            {
                if (_pollTask != null && !_pollTask.IsCompleted && _port == port) return;
                _pollCts?.Dispose();
                _generation++;
                long generation = _generation;
                _lastIndex = -1;
                _current = TerracottaState.ProcessUnavailable(-1, "正在读取陶瓦联机状态");
                _pollCts = new CancellationTokenSource();
                _pollTask = PollAsync(port, generation, _pollCts.Token);
            }
        }

        static async Task StopPollingAsync()
        {
            CancellationTokenSource cts;
            Task task;
            lock (Sync)
            {
                _generation++;
                cts = _pollCts;
                task = _pollTask;
                _pollCts = null;
                _pollTask = null;
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
            if (task != null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Warn("停止 Terracotta 状态轮询时出现异常：" + TaskManager.Flatten(ex)); }
            }
            cts?.Dispose();
        }

        static async Task PollAsync(int port, long generation, CancellationToken ct)
        {
            int failures = 0;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var state = TerracottaState.Parse(await GetJsonAsync(port, "/state", ct, 3).ConfigureAwait(false));
                        failures = 0;
                        AcceptState(state, generation);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Log.Warn($"读取 Terracotta 状态失败（{failures}/{MaxPollFailures}）：{TaskManager.Flatten(ex)}");
                        if (failures >= MaxPollFailures)
                        {
                            PublishUnavailable("Terracotta 进程不可用（连续 5 次状态请求失败）：" + TaskManager.Flatten(ex), generation);
                            return;
                        }
                    }
                    await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 正常停止；由 StopPollingAsync 等待并清理。
            }
        }

        static void AcceptState(TerracottaState state, long generation)
        {
            lock (Sync)
            {
                if (generation != _generation || state.Index <= _lastIndex) return;
                _lastIndex = state.Index;
                _current = state;
            }
            RaiseStateChanged(state);
        }

        static void PublishUnavailable(string reason, long? generation)
        {
            CancellationTokenSource cts;
            TerracottaState state;
            lock (Sync)
            {
                if (_closing || (generation.HasValue && generation.Value != _generation)) return;
                state = TerracottaState.ProcessUnavailable(_lastIndex, reason);
                _current = state;
                cts = _pollCts;
            }
            if (cts != null) { try { cts.Cancel(); } catch { } }
            RaiseStateChanged(state);
        }

        static void RaiseStateChanged(TerracottaState state)
        {
            var handlers = StateChanged;
            if (handlers == null) return;
            foreach (Action<TerracottaState> handler in handlers.GetInvocationList())
            {
                try { handler(state); }
                catch (Exception ex) { Log.Warn("Terracotta StateChanged 订阅者异常：" + TaskManager.Flatten(ex)); }
            }
        }

        static void OnProcessExited(object sender, EventArgs e)
        {
            lock (Sync)
            {
                if (!ReferenceEquals(_process, sender) || _closing) return;
            }
            Log.Warn("Terracotta 进程已退出");
            PublishUnavailable("Terracotta 进程已退出", null);
        }

        static void DetachProcess()
        {
            Process process;
            string portFile;
            lock (Sync)
            {
                process = _process;
                portFile = _portFile;
                _process = null;
                _portFile = null;
                _port = 0;
            }
            if (process != null)
            {
                try { process.Exited -= OnProcessExited; } catch { }
                process.Dispose();
            }
            if (!string.IsNullOrEmpty(portFile)) Util.TryDelete(portFile);
        }
    }
}
