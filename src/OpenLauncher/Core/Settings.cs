using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace OpenLauncher.Core
{
    public enum SourceMode
    {
        /// <summary>镜像优先，失败再用官方</summary>
        MirrorFirst = 0,
        /// <summary>官方优先，失败再用镜像</summary>
        OfficialFirst = 1,
        /// <summary>只用官方源</summary>
        OfficialOnly = 2,
    }

    /// <summary>
    /// 全局设置。存成一个 JSON，读写都走这里。
    /// </summary>
    public static class Settings
    {
        static JNode _root = JNode.Obj();
        static Timer _saveTimer;
        static readonly object Lock = new object();

        public static event Action Changed;

        public static void Load()
        {
            try
            {
                if (File.Exists(Paths.Config)) _root = JNode.ParseFile(Paths.Config);
                if (!_root.IsObj) _root = JNode.Obj();
            }
            catch (Exception ex)
            {
                Log.Error("配置文件损坏，已备份并重置", ex);
                try { File.Copy(Paths.Config, Paths.Config + ".broken", true); } catch { }
                _root = JNode.Obj();
            }
        }

        public static void SaveNow()
        {
            lock (Lock)
            {
                try { _root.SaveFile(Paths.Config); }
                catch (Exception ex) { Log.Error("保存配置失败", ex); }
            }
        }

        static void ScheduleSave()
        {
            lock (Lock)
            {
                _saveTimer?.Dispose();
                _saveTimer = new Timer(_ => SaveNow(), null, 400, Timeout.Infinite);
            }
            Changed?.Invoke();
        }

        // 读路径同样加锁：后台线程 Set 时会原地改 JNode 树，无锁读可能踩到正在修改的 List（评审 #8）。
        // GetNode 返回快照副本，避免调用方枚举期间被并发改坏。
        public static string Get(string key, string def = "") { lock (Lock) return _root[key].Str ?? def; }
        public static int GetInt(string key, int def) { lock (Lock) return _root[key].IsNull ? def : _root[key].Int; }
        public static bool GetBool(string key, bool def) { lock (Lock) return _root[key].BoolOr(def); }
        public static JNode GetNode(string key) { lock (Lock) return _root[key].Clone(); }

        public static void Set(string key, JNode value) { lock (Lock) _root.Set(key, value); ScheduleSave(); }

        // ---------- 常用项 ----------
        public static SourceMode GameSource { get => (SourceMode)GetInt("download.gameSource", 0); set => Set("download.gameSource", (int)value); }
        public static SourceMode ModSource { get => (SourceMode)GetInt("download.modSource", 0); set => Set("download.modSource", (int)value); }
        public static int DownloadThreads { get => Math.Max(1, Math.Min(128, GetInt("download.threads", 32))); set => Set("download.threads", value); }
        public static int SpeedLimitKb { get => GetInt("download.speedLimitKb", 0); set => Set("download.speedLimitKb", value); }
        /// <summary>modrinth / curseforge / all（all 仅用于 Mod 双来源搜索）</summary>
        public static string PreferredContentSource { get => Get("content.source", "modrinth"); set => Set("content.source", value); }
        public static string CurseForgeApiKey
        {
            get
            {
                var raw = Get("content.cfApiKey", "");
                // 旧版明文存储：读到就顺手升级成密文
                if (!string.IsNullOrEmpty(raw) && !raw.StartsWith("dpapi:", StringComparison.Ordinal))
                {
                    Set("content.cfApiKey", Protect(raw));
                    return raw;
                }
                var key = Unprotect(raw);
                return string.IsNullOrEmpty(key) ? Branding.DefaultCurseForgeApiKey : key;
            }
            set => Set("content.cfApiKey", string.IsNullOrEmpty(value) ? "" : Protect(value));
        }

        // DPAPI（CurrentUser）加密：config.json 里不再明文存 Key。换 Windows 用户解不开，当作没填。
        const string DpapiPrefix = "dpapi:";
        static string Protect(string s)
        {
            try { return DpapiPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) { Log.Warn("加密存储失败，退回明文：" + ex.Message); return s; }
        }

        static string Unprotect(string s)
        {
            if (string.IsNullOrEmpty(s) || !s.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return s;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.Substring(DpapiPrefix.Length)), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        public static string MicrosoftClientId { get => Get("account.msClientId", Branding.DefaultMicrosoftClientId); set => Set("account.msClientId", value); }
        public static string SelectedAccount { get => Get("account.selected"); set => Set("account.selected", value); }

        public static string JavaPath { get => Get("launch.java"); set => Set("launch.java", value); }
        public static bool MemoryAuto { get => GetBool("launch.memoryAuto", true); set => Set("launch.memoryAuto", value); }
        public static int MemoryMb { get => GetInt("launch.memoryMb", 4096); set => Set("launch.memoryMb", value); }
        public static string JvmArgs { get => Get("launch.jvmArgs"); set => Set("launch.jvmArgs", value); }
        public static string GameArgs { get => Get("launch.gameArgs"); set => Set("launch.gameArgs", value); }
        public static int WindowWidth { get => GetInt("launch.width", 854); set => Set("launch.width", value); }
        public static int WindowHeight { get => GetInt("launch.height", 480); set => Set("launch.height", value); }
        public static bool Fullscreen { get => GetBool("launch.fullscreen", false); set => Set("launch.fullscreen", value); }
        /// <summary>keep / minimize / close / hide</summary>
        public static string AfterLaunch { get => Get("launch.after", "minimize"); set => Set("launch.after", value); }
        /// <summary>none / modded / all —— 版本隔离策略</summary>
        public static string Isolation { get => Get("launch.isolation", "modded"); set => Set("launch.isolation", value); }
        public static string WindowTitle { get => Get("launch.title"); set => Set("launch.title", value); }

        public static string ThemeColor { get => Get("ui.theme", "#2F7BF5"); set => Set("ui.theme", value); }
        public static string BackgroundImage { get => Get("ui.background"); set => Set("ui.background", value); }
        /// <summary>液态玻璃（仿 macOS / iOS 的毛玻璃效果）</summary>
        public static bool Glass { get => GetBool("ui.glass", false); set => Set("ui.glass", value); }

        public static bool AutoCheckUpdate { get => GetBool("update.auto", true); set => Set("update.auto", value); }
        public static bool OnboardingCompleted { get => GetBool("ui.onboarded", false); set => Set("ui.onboarded", value); }
        /// <summary>首次引导已经开始安装；本次安装完成后提供返回首页并启动的衔接。</summary>
        public static bool OnboardingInstalling { get => GetBool("ui.onboardingInstalling", false); set => Set("ui.onboardingInstalling", value); }

        // ---------- 游戏目录（可以有多个 .minecraft） ----------
        public static List<GameFolder> GameFolders
        {
            get
            {
                lock (Lock)
                {
                    var list = new List<GameFolder>();
                    foreach (var n in _root["game.folders"])
                        if (n["path"].Str != null) list.Add(new GameFolder { Name = n["name"].StrOr("游戏目录"), Path = n["path"].Str });
                    if (list.Count == 0)
                    {
                        list.Add(new GameFolder { Name = "当前目录", Path = Paths.DefaultGameDir });
                        if (Directory.Exists(Paths.OfficialGameDir) && !string.Equals(Paths.OfficialGameDir, Paths.DefaultGameDir, StringComparison.OrdinalIgnoreCase))
                            list.Add(new GameFolder { Name = "官方启动器目录", Path = Paths.OfficialGameDir });
                    }
                    return list;
                }
            }
            set
            {
                var arr = JNode.Arr();
                foreach (var f in value) arr.Add(JNode.Obj().Set("name", f.Name).Set("path", f.Path));
                Set("game.folders", arr);
            }
        }

        public static string SelectedGameFolder
        {
            get
            {
                var p = Get("game.folder");
                return string.IsNullOrEmpty(p) ? GameFolders[0].Path : p;
            }
            set => Set("game.folder", value);
        }

        /// <summary>每个游戏目录各自记住上次选中的版本</summary>
        public static string SelectedVersion
        {
            get { lock (Lock) return _root["game.selectedVersion"][SelectedGameFolder.ToLowerInvariant()].Str; }
            set
            {
                lock (Lock)
                {
                    var m = _root["game.selectedVersion"];
                    if (!m.IsObj) { m = JNode.Obj(); _root.Set("game.selectedVersion", m); }
                    m.Set(SelectedGameFolder.ToLowerInvariant(), value);
                }
                ScheduleSave();
            }
        }

        public static List<string> ExtraList(string key)
        {
            lock (Lock) return _root[key].StrList();
        }

        public static void SetList(string key, IEnumerable<string> items)
        {
            var a = JNode.Arr();
            foreach (var i in items) a.Add(i);
            Set(key, a);
        }
    }

    public class GameFolder
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public override string ToString() => $"{Name}  ({Path})";
    }
}
