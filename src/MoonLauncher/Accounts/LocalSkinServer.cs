using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MoonLauncher.Core;

namespace MoonLauncher.Accounts
{
    /// <summary>
    /// 离线皮肤的本地 Yggdrasil 协议服务器：authlib-injector 挂到游戏 JVM 后，
    /// 由它向游戏提供离线账号的皮肤（textures 指向本服务器下发的 PNG）。
    /// 只监听 127.0.0.1 的随机端口，游戏退出后停止。
    /// </summary>
    public class LocalSkinServer
    {
        public static LocalSkinServer Current { get; private set; }

        readonly Account _acc;
        HttpListener _listener;
        int _port;

        public static string SkinDir => Paths.Ensure(Path.Combine(Paths.DataDir, "skins"));

        public static string SkinPath(Account acc) => Path.Combine(SkinDir, AccountUtil.NormalizeUuid(acc.Uuid) + ".png");

        /// <summary>离线账号是否已设置本地皮肤（字段有值且文件存在）</summary>
        public static bool HasSkin(Account acc)
        {
            return acc != null && acc.Type == AccountType.Offline
                && !string.IsNullOrEmpty(acc.LocalSkin) && File.Exists(SkinPath(acc));
        }

        LocalSkinServer(Account acc) { _acc = acc; }

        /// <summary>启动服务器（停掉上一个），返回提供给 authlib-injector 的 API 根地址</summary>
        public static string Start(Account acc)
        {
            StopAll();
            var s = new LocalSkinServer(acc);
            // HttpListener 不支持端口 0 回读实际端口，先自己找个空闲端口
            int port;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            try
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally { probe.Stop(); }
            s._listener = new HttpListener();
            s._listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            s._listener.Start();
            s._port = port;
            Current = s;
            Log.Info($"本地皮肤服务器已启动：127.0.0.1:{port}（{acc.Name}）");
            Task.Run((Action)s.Loop);
            return "http://127.0.0.1:" + port;
        }

        public static void StopAll()
        {
            try
            {
                var s = Current;
                Current = null;
                if (s != null)
                {
                    Log.Info("本地皮肤服务器已停止");
                    try { s._listener.Stop(); } catch { }
                    try { s._listener.Close(); } catch { }
                }
            }
            catch { }
        }

        void Loop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; } // Stop() 之后 GetContext 会抛异常，正常退出
                try { Handle(ctx); }
                catch (Exception ex) { Log.Warn("本地皮肤服务器处理请求失败：" + ex.Message); }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.Trim('/');
            byte[] body = null;
            string type = "application/json; charset=utf-8";
            int status = 200;

            if (path == $"sessionserver/session/minecraft/profile/{AccountUtil.NormalizeUuid(_acc.Uuid)}")
                body = Encoding.UTF8.GetBytes(ProfileJson());
            else if (path.StartsWith("sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
                body = Encoding.UTF8.GetBytes(ProfileJson()); // 部分版本先查 hasJoined
            else if (path == $"skin/{AccountUtil.NormalizeUuid(_acc.Uuid)}.png" && File.Exists(SkinPath(_acc)))
            {
                body = File.ReadAllBytes(SkinPath(_acc));
                type = "image/png";
            }
            else status = 404;

            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = type;
                if (body != null) ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        string ProfileJson()
        {
            var uuid = AccountUtil.NormalizeUuid(_acc.Uuid);
            var textures = "{\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                + ",\"profileId\":\"" + AccountUtil.Dashed(uuid)
                + "\",\"profileName\":\"" + _acc.Name.Replace("\"", "") + "\""
                + ",\"textures\":{\"SKIN\":{\"url\":\"http://127.0.0.1:" + _port + "/skin/" + uuid + ".png\"}}}";
            return "{\"id\":\"" + uuid + "\",\"name\":\"" + _acc.Name.Replace("\"", "")
                + "\",\"properties\":[{\"name\":\"textures\",\"value\":\""
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(textures)) + "\"}]}";
        }
    }
}
