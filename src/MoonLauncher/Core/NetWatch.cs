using System;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MoonLauncher.Core
{
    /// <summary>网络断了时读网络流抛的包装，区别于磁盘 IO 错误（磁盘错误不该触发"等网恢复"）</summary>
    public class NetStreamException : IOException
    {
        public NetStreamException(Exception inner) : base("网络流中断", inner) { }
    }

    /// <summary>下载暂停信号：DownloadOneAsync 遇到它时不记错误，外层等网恢复后断点续传</summary>
    public class NetworkDownException : Exception
    {
        public NetworkDownException(Exception inner) : base("网络中断", inner) { }
    }

    /// <summary>网络连通性监视：断网时挂起下载，联网后自动继续。</summary>
    public static class NetWatch
    {
        // 国内 + 国外各一个，能通任意一个就算在线
        static readonly string[] Probes = { "https://www.baidu.com/", "https://www.microsoft.com/" };

        /// <summary>这个异常是不是"网断了"（而不是文件不存在 / 服务器报错 / 本地文件问题）</summary>
        public static bool ProbablyOffline(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is NetStreamException) return true;
                if (e is TimeoutException) return true;
                if (e is SocketException) return true;
                if (e is HttpRequestException) return true;
            }
            return false;
        }

        /// <summary>已联网立即返回；否则每秒探测一次，挂起直到恢复。用户取消会抛 OperationCanceledException。</summary>
        public static async Task WaitOnlineAsync(CancellationToken ct)
        {
            if (await OnlineNowAsync(ct).ConfigureAwait(false)) return;
            Log.Warn("网络中断，下载已暂停，等待网络恢复…");
            while (!await OnlineNowAsync(ct).ConfigureAwait(false))
                await Task.Delay(1500, ct).ConfigureAwait(false);
            Log.Info("网络已恢复，继续下载");
        }

        static async Task<bool> OnlineNowAsync(CancellationToken ct)
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable()) return false;
            }
            catch { return false; }
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(4000);
                foreach (var url in Probes)
                {
                    try
                    {
                        using (var resp = await Http.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cts.Token).ConfigureAwait(false))
                        {
                            // 能连上服务器就算在线：403/404 也说明网是通的
                            if ((int)resp.StatusCode < 500) return true;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                }
            }
            return false;
        }
    }
}
