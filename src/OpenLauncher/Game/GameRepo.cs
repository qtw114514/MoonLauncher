using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenLauncher.Core;

namespace OpenLauncher.Game
{
    /// <summary>一个 .minecraft 目录</summary>
    public class GameRepo
    {
        public string Root { get; }
        public string Libraries => Path.Combine(Root, "libraries");
        public string Assets => Path.Combine(Root, "assets");
        public string Versions => Path.Combine(Root, "versions");

        public GameRepo(string root) { Root = Path.GetFullPath(root); }

        public static GameRepo Current => new GameRepo(Settings.SelectedGameFolder);

        public string VersionDir(string id) => Path.Combine(Versions, id);
        public string VersionJson(string id) => Path.Combine(Versions, id, id + ".json");
        public string VersionJar(string id) => Path.Combine(Versions, id, id + ".jar");
        public string LibraryPath(string mavenName) => Maven.Parse(mavenName)?.LocalPath(Libraries);

        public bool HasVersion(string id) => File.Exists(VersionJson(id));

        public List<string> ListVersionIds()
        {
            var list = new List<string>();
            string[] dirs;
            try { dirs = Directory.Exists(Versions) ? Directory.GetDirectories(Versions) : new string[0]; }
            catch (Exception ex) { Log.Warn("枚举版本目录失败：" + ex.Message); return list; } // 一个坏目录不该让列表全空
            foreach (var d in dirs)
            {
                try
                {
                    var id = Path.GetFileName(d);
                    if (File.Exists(VersionJson(id))) list.Add(id);
                }
                catch (Exception ex) { Log.Warn($"版本目录 {Path.GetFileName(d)} 读取失败：{ex.Message}"); }
            }
            return list;
        }

        public List<Instance> ListInstances()
        {
            var list = new List<Instance>();
            foreach (var id in ListVersionIds())
            {
                try { list.Add(new Instance(this, id)); }
                catch (Exception ex) { Log.Warn($"版本 {id} 读取失败：{ex.Message}"); }
            }
            return list;
        }

        /// <summary>
        /// 一些安装器（OptiFine、老 Forge）会检查 launcher_profiles.json 是否存在。
        /// </summary>
        public void EnsureLauncherProfiles()
        {
            Directory.CreateDirectory(Root);
            var p = Path.Combine(Root, "launcher_profiles.json");
            if (File.Exists(p)) return;
            var json = JNode.Obj()
                .Set("profiles", JNode.Obj())
                .Set("selectedProfile", (string)null)
                .Set("clientToken", Guid.NewGuid().ToString("N"))
                .Set("launcherVersion", JNode.Obj().Set("name", Branding.AppName).Set("format", 21));
            json.SaveFile(p);
        }

        /// <summary>同名版本已存在时，生成一个不冲突的新名字</summary>
        public string UniqueName(string baseName)
        {
            baseName = Util.SafeFileName(baseName);
            if (!Directory.Exists(VersionDir(baseName))) return baseName;
            for (int i = 2; ; i++)
            {
                var n = $"{baseName} ({i})";
                if (!Directory.Exists(VersionDir(n))) return n;
            }
        }
    }
}
