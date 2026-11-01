using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 只监听 127.0.0.1 的随机端口，可同时服务多个离线账号；
    /// 每个使用它的游戏在启动时 Acquire 一次引用、退出时 Release 配对归还，
    /// 引用归零才真正停止——避免一个游戏退出把其他还在运行的游戏的皮肤服务停掉。
    /// </summary>
    public class LocalSkinServer
    {
        public static LocalSkinServer Current { get; private set; }

        static readonly object Gate = new object();
        static int _refs;

        readonly Dictionary<string, Account> _accounts = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);
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

        LocalSkinServer() { }

        /// <summary>
        /// 为一个游戏会话获取（必要时启动）本地皮肤服务器，返回提供给 authlib-injector 的 API 根地址。
        /// 与 <see cref="Release"/> 严格一一配对；已运行时直接复用，可同时服务多个离线账号。
        /// </summary>
        public static string Acquire(Account acc)
        {
            lock (Gate)
            {
                var s = Current;
                if (s == null)
                {
                    s = new LocalSkinServer();
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
                    Log.Info($"本地皮肤服务器已启动：127.0.0.1:{port}");
                    Task.Run((Action)s.Loop);
                }
                s._accounts[AccountUtil.NormalizeUuid(acc.Uuid)] = acc;
                _refs++;
                Log.Info($"本地皮肤服务器为 {acc.Name} 提供皮肤（{s._accounts.Count} 个账号 / {_refs} 个游戏）");
                return "http://127.0.0.1:" + s._port;
            }
        }

        /// <summary>与 Acquire 配对归还引用；最后一个游戏退出后才真正停止服务器</summary>
        public static void Release()
        {
            lock (Gate)
            {
                if (_refs > 0) _refs--;
                if (_refs > 0 || Current == null) return;
                Log.Info("所有使用皮肤的游戏已退出，本地皮肤服务器已停止");
                try { Current._listener.Stop(); } catch { }
                try { Current._listener.Close(); } catch { }
                Current = null;
            }
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
            int status;

            const string profilePrefix = "sessionserver/session/minecraft/profile/";
            lock (Gate)
            {
                Account acc = null;
                if (path.StartsWith(profilePrefix, StringComparison.Ordinal))
                {
                    _accounts.TryGetValue(AccountUtil.NormalizeUuid(path.Substring(profilePrefix.Length)), out acc);
                }
                else if (path.StartsWith("sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
                {
                    // 部分版本先查 hasJoined：按用户名找
                    var name = ctx.Request.QueryString["username"];
                    foreach (var a in _accounts.Values)
                        if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) { acc = a; break; }
                    if (acc == null) acc = _accounts.Values.FirstOrDefault();
                }
                else if (path.StartsWith("skin/", StringComparison.Ordinal))
                {
                    var uuid = Path.GetFileNameWithoutExtension(path.Substring("skin/".Length));
                    if (_accounts.TryGetValue(AccountUtil.NormalizeUuid(uuid), out var a) && HasSkin(a)) acc = a;
                }

                if (acc == null) status = 404;
                else if (path.StartsWith("skin/", StringComparison.Ordinal))
                {
                    body = File.ReadAllBytes(SkinPath(acc));
                    type = "image/png";
                    status = 200;
                }
                else
                {
                    body = Encoding.UTF8.GetBytes(ProfileJson(acc));
                    status = 200;
                }
            }

            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = type;
                if (body != null) ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.OutputStream.Close();
            }
            catch { }
        }

        string ProfileJson(Account acc)
        {
            var uuid = AccountUtil.NormalizeUuid(acc.Uuid);
            var textures = "{\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                + ",\"profileId\":\"" + AccountUtil.Dashed(uuid)
                + "\",\"profileName\":\"" + acc.Name.Replace("\"", "") + "\""
                + ",\"textures\":{\"SKIN\":{\"url\":\"http://127.0.0.1:" + _port + "/skin/" + uuid + ".png\"}}}";
            return "{\"id\":\"" + uuid + "\",\"name\":\"" + acc.Name.Replace("\"", "")
                + "\",\"properties\":[{\"name\":\"textures\",\"value\":\""
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(textures)) + "\"}]}";
        }
    }
}
