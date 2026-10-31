using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MoonLauncher.Core;

namespace MoonLauncher.Game
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

        /// <summary>直接继承指定版本的子版本</summary>
        public List<string> DirectChildren(string id)
        {
            var list = new List<string>();
            foreach (var child in ListVersionIds())
            {
                if (string.Equals(child, id, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (JNode.ParseFile(VersionJson(child))["inheritsFrom"].Str == id) list.Add(child);
                }
                catch (Exception ex) { Log.Warn($"读取版本 {child} 的继承关系失败：{ex.Message}"); }
            }
            return list;
        }

        /// <summary>重命名版本，并同步直接子版本；任一步失败都按相反顺序回滚</summary>
        public void RenameVersion(string oldId, string newId)
        {
            if (string.IsNullOrWhiteSpace(newId)) throw new ArgumentException("版本名不能为空", nameof(newId));
            if (oldId == newId) return;
            var oldDir = VersionDir(oldId);
            var newDir = VersionDir(newId);
            if (!File.Exists(VersionJson(oldId))) throw new FileNotFoundException($"找不到版本 {oldId} 的 JSON");
            if (Directory.Exists(newDir)) throw new IOException("已经有同名版本了");

            var ownPath = VersionJson(oldId);
            var own = JNode.ParseFile(ownPath);
            var ownOriginal = own.Clone();
            var children = DirectChildren(oldId).Select(id =>
            {
                var json = JNode.ParseFile(VersionJson(id));
                return (id, path: VersionJson(id), json, original: json.Clone());
            }).ToList();
            var undo = new List<Action>();
            try
            {
                undo.Add(() => { if (Directory.Exists(newDir) && !Directory.Exists(oldDir)) Directory.Move(newDir, oldDir); });
                Directory.Move(oldDir, newDir);

                var newJson = Path.Combine(newDir, newId + ".json");
                var movedJson = Path.Combine(newDir, oldId + ".json");
                undo.Add(() => { if (File.Exists(newJson) && !File.Exists(movedJson)) File.Move(newJson, movedJson); });
                File.Move(movedJson, newJson);

                var oldJar = Path.Combine(newDir, oldId + ".jar");
                var newJar = Path.Combine(newDir, newId + ".jar");
                if (File.Exists(oldJar))
                {
                    undo.Add(() => { if (File.Exists(newJar) && !File.Exists(oldJar)) File.Move(newJar, oldJar); });
                    File.Move(oldJar, newJar);
                }

                undo.Add(() => ownOriginal.SaveFile(newJson));
                own.Set("id", newId).SaveFile(newJson);
                foreach (var child in children)
                {
                    undo.Add(() => child.original.SaveFile(child.path));
                    child.json.Set("inheritsFrom", newId).SaveFile(child.path);
                }
            }
            catch (Exception ex)
            {
                var errors = new List<string>();
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    try { undo[i](); }
                    catch (Exception rollback) { errors.Add(rollback.Message); }
                }
                throw new IOException("重命名失败，已回滚操作：" + ex.Message +
                    (errors.Count == 0 ? "" : "\n部分回滚失败：" + string.Join("；", errors)), ex);
            }

            Util.TryDeleteDir(Path.Combine(newDir, oldId + "-natives"));
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
