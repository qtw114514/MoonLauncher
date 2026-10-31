using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using MoonLauncher.Content;
using MoonLauncher.Core;
using MoonLauncher.Game;

namespace MoonLauncher.UI.Pages
{
    public class FileRow
    {
        public ContentFile File { get; set; }
        public string DisplayName => string.IsNullOrEmpty(File.DisplayName) ? File.FileName : File.DisplayName;
        public string ReleaseTypeText => File.ReleaseTypeText;
        public DateTime Published => File.Published;
        public string GameVersionsText
        {
            get
            {
                var v = File.GameVersions ?? new List<string>();
                return v.Count == 0 ? "未标注版本" : v.Count <= 4 ? string.Join(", ", v) : $"{v.First()} ~ {v.Last()}（{v.Count} 个版本）";
            }
        }
        public string LoadersText => File.Loaders == null || File.Loaders.Count == 0 ? "通用" : string.Join(" / ", File.Loaders);
        public string SizeText => File.Size > 0 ? Util.FormatSize(File.Size) : "";
    }

    public partial class ProjectPage : UserControl, IPage
    {
        readonly ContentProject _p;
        readonly ContentType _type;
        readonly Instance _target;
        readonly string _mc, _loader;
        List<ContentFile> _files;
        bool _loaded;

        public ProjectPage(ContentProject p, ContentType type, Instance target, string mc, string loader)
        {
            InitializeComponent();
            _p = p; _type = type; _target = target;
            _mc = string.IsNullOrEmpty(mc) ? null : mc;
            _loader = string.IsNullOrEmpty(loader) ? null : loader;
            PTitle.Text = p.Title;
            PMeta.Text = $"{p.Author} · {p.SourceText} · 下载 {p.DownloadsText} · 更新于 {p.Updated:yyyy-MM-dd}" + (p.Categories?.Count > 0 ? " · " + string.Join(" ", p.Categories.Take(6)) : "");
            PDesc.Text = p.Description;
            TargetInfo.Text = type == ContentType.Modpack ? "整合包会安装成一个新版本" : target != null ? $"安装到 {target.Id}" : "未选择目标版本";
            OnlyMatch.IsVisible = _mc == null && _loader == null ? false : true;
        }

        public async void OnShown()
        {
            if (_loaded) return;
            _loaded = true;
            Icon.Source = await RemoteImage.LoadAsync(_p.IconUrl ?? "", 128);
            _ = LoadDescAsync();
            await LoadFilesAsync();
        }

        // ---------- 介绍 ----------
        async Task LoadDescAsync()
        {
            // 完整介绍原文（best-effort，失败不影响文件列表）
            try
            {
                var prov = ContentService.Get(_p.Source);
                var full = await Task.Run(() => prov.GetProjectAsync(_p.Id, CancellationToken.None));
                if (!string.IsNullOrWhiteSpace(full?.Body))
                {
                    _p.Body = full.Body;
                    BodyText.Text = PlainText(full.Body);
                    DescCard.IsVisible = true;
                    DescToggle.IsVisible = true;
                }
            }
            catch (Exception ex) { Log.Warn($"获取项目介绍失败（{_p.Title}）：{ex.Message}"); }

            // 中文简介（MC 百科，best-effort）
            try
            {
                var intro = await Task.Run(() => McmodIntro.FetchAsync(_p.Title, CancellationToken.None));
                if (intro != null)
                {
                    ZhText.Text = intro.Text;
                    ZhPanel.IsVisible = true;
                    McmodLink.Tag = intro.Url;
                    McmodLink.IsVisible = true;
                    DescCard.IsVisible = true;
                }
            }
            catch (Exception ex) { Log.Warn($"获取中文介绍失败（{_p.Title}）：{ex.Message}"); }
            ZhStatus.IsVisible = false;
        }

        bool _descExpanded = true;
        void DescToggle_Click(object sender, RoutedEventArgs e)
        {
            _descExpanded = !_descExpanded;
            DescContent.IsVisible = _descExpanded ? true : false;
            DescToggle.Content = _descExpanded ? "收起" : "展开";
        }

        void Mcmod_Click(object sender, RoutedEventArgs e)
        {
            if (McmodLink.Tag is string url && !string.IsNullOrEmpty(url)) Util.OpenUrl(url);
        }

        /// <summary>把 Markdown / HTML 介绍压成可读纯文本（保留段落，去掉格式语法）</summary>
        static string PlainText(string md)
        {
            var s = md ?? "";
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, "!\\[[^\\]]*\\]\\([^)]*\\)", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\[([^\\]]*)\\]\\([^)]*\\)", "$1");
            s = System.Text.RegularExpressions.Regex.Replace(s, "^#{1,6}\\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\*\\*|__", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, "&nbsp;", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, "&amp;", "&");
            s = System.Text.RegularExpressions.Regex.Replace(s, "[ \\t]+", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, "\\n{3,}", "\n\n");
            return s.Trim();
        }

        async Task LoadFilesAsync()
        {
            StatusPanel.IsVisible = true;
            StatusText.Text = "加载文件列表…";
            StatusBar.IsVisible = true;
            RetryFiles.IsVisible = false;
            try
            {
                var prov = ContentService.Get(_p.Source);
                _files = await Task.Run(() => prov.GetFilesAsync(_p.Id, null, null, CancellationToken.None));
                Apply();
            }
            catch (Exception ex)
            {
                _loaded = false;
                StatusText.Text = "文件列表加载失败：" + ex.Message;
                StatusBar.IsVisible = false;
                RetryFiles.IsVisible = true;
            }
        }

        async void RetryFiles_Click(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            await LoadFilesAsync();
        }

        void Apply()
        {
            if (_files == null) return;
            IEnumerable<ContentFile> q = _files;
            if (OnlyMatch.IsChecked == true && OnlyMatch.IsVisible == true)
            {
                if (_mc != null) q = q.Where(f => f.GameVersions == null || f.GameVersions.Count == 0 || f.GameVersions.Contains(_mc));
                if (_loader != null && (_type == ContentType.Mod || _type == ContentType.Modpack))
                    q = q.Where(f => f.Loaders == null || f.Loaders.Count == 0 || f.Loaders.Any(l => string.Equals(l, _loader, StringComparison.OrdinalIgnoreCase)));
            }
            if (_type == ContentType.Modpack)
            {
                // 整合包按支持的 MC 版本从高到低排（未标注版本的排最后，同版本按发布日期新在前）
                var cmp = Comparer<string>.Create(ContentService.CompareGameVersion);
                q = q.OrderByDescending(f => HighestMcVersion(f) != null)
                     .ThenByDescending(f => HighestMcVersion(f), cmp)
                     .ThenByDescending(f => f.Published);
            }
            var rows = q.Select(f => new FileRow { File = f }).ToList();
            Files.ItemsSource = rows;
            FilesTitle.Text = $"选择要下载的文件（{rows.Count}）";
            if (rows.Count == 0)
            {
                StatusPanel.IsVisible = true;
                StatusBar.IsVisible = false;
                StatusText.Text = _files.Count == 0 ? "这个项目没有可下载的文件" : $"没有适配 {_mc} {_loader} 的文件，关掉右上角的筛选看看全部文件";
            }
            else StatusPanel.IsVisible = false;
        }

        /// <summary>取文件支持的最高 MC 版本；未标注版本时返回 null</summary>
        static string HighestMcVersion(ContentFile f)
        {
            var vs = f.GameVersions;
            if (vs == null || vs.Count == 0) return null;
            var cmp = Comparer<string>.Create(ContentService.CompareGameVersion);
            var best = vs[0];
            for (int i = 1; i < vs.Count; i++)
                if (cmp.Compare(vs[i], best) > 0) best = vs[i];
            return best;
        }

        void OnlyMatch_Click(object sender, RoutedEventArgs e) => Apply();
        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();
        void Web_Click(object sender, RoutedEventArgs e) { if (!string.IsNullOrEmpty(_p.WebUrl)) Util.OpenUrl(_p.WebUrl); }

        async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Control)?.DataContext is FileRow row)) return;
            var file = row.File;

            if (_type == ContentType.Modpack)
            {
                var name = await Dialogs.Prompt("安装整合包", "版本名", Util.SafeFileName(_p.Title));
                if (string.IsNullOrWhiteSpace(name)) return;
                var repo = GameRepo.Current;
                TaskManager.Start($"安装整合包 {_p.Title}", async t =>
                {
                    var id = await ModpackImporter.InstallFromFileAsync(repo, file, name, t);
                    Settings.SelectedVersion = id;
                    TaskManager.OnUi(() => MainWindow.Current.LaunchPage.RefreshVersion());
                });
                MainWindow.Current.ToggleTasks(true);
                return;
            }

            if (_target == null) { await Dialogs.Alert("没有目标版本", "先在上一页的“安装到”里选一个版本（没有的话先去装一个）。"); return; }

            string dir;
            if (_type == ContentType.DataPack)
            {
                var saves = Directory.Exists(_target.SavesDir) ? Directory.GetDirectories(_target.SavesDir).Select(Path.GetFileName).ToList() : new List<string>();
                if (saves.Count == 0) { await Dialogs.Alert("没有存档", "数据包要装进某个存档里。先进游戏创建一个世界吧。"); return; }
                var idx = await Dialogs.Choose("装到哪个存档", "数据包只对选中的世界生效：", saves);
                if (idx < 0) return;
                dir = Path.Combine(_target.SavesDir, saves[idx], "datapacks");
            }
            else dir = ContentService.TargetDir(_target, _type);

            if (_type == ContentType.Mod && file.Loaders != null && file.Loaders.Count > 0 && _target.PrimaryLoader != LoaderType.None)
            {
                var slug = LoaderInfo.Slug(_target.PrimaryLoader);
                if (!file.Loaders.Any(l => string.Equals(l, slug, StringComparison.OrdinalIgnoreCase)) &&
                    !await Dialogs.Confirm("加载器不匹配", $"这个文件是给 {string.Join("/", file.Loaders)} 的，而 {_target.Id} 用的是 {LoaderInfo.Name(_target.PrimaryLoader)}，装了大概率不能用。仍然下载？"))
                    return;
            }

            var mc = _target.McVersion;
            var loader = LoaderInfo.Slug(_target.PrimaryLoader);
            bool deps = _type == ContentType.Mod;
            var title = _p.Title;
            var button = sender as Button;
            if (button != null) { button.IsEnabled = false; button.Content = "检查中…"; }
            try
            {
                var plan = await ContentService.PrepareInstallAsync(file, dir, deps, mc, loader, CancellationToken.None);
                if (!await ConfirmInstallAsync(plan)) return;
                TaskManager.Start($"下载 {title}", async t =>
                {
                    var report = await ContentService.InstallPreparedAsync(plan, t);
                    t.Detail = report.Installed.Count > 1 ? $"已安装 {report.Installed.Count} 个文件（含前置）" : "安装完成";
                    TaskManager.OnUi(() => _ = ShowInstallCompleteAsync(report));
                });
                MainWindow.Current.ToggleTasks(true);
                Dialogs.Toast($"开始下载 {title}" + (plan.Files.Count > 1 ? $"（含 {plan.Files.Count - 1} 个前置）" : ""));
            }
            catch (Exception ex)
            {
                await ErrorDialog.ShowAsync("无法准备安装", TaskManager.Flatten(ex), "检查网络、目标文件归属和版本筛选后重试。", ex);
            }
            finally
            {
                if (button != null) { button.IsEnabled = true; button.Content = "下载"; }
            }
        }

        async Task<bool> ConfirmInstallAsync(InstallPlan plan)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"安装到：{_target.Id}\n目标目录：{plan.TargetDir}", Classes = { "sub" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            void Section(string title, IEnumerable<string> values, bool warning = false)
            {
                var list = values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
                if (list == null || list.Count == 0) return;
                panel.Children.Add(new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = warning ? (Avalonia.Media.Brush)Application.Current.FindResource("Warn") : (Avalonia.Media.Brush)Application.Current.FindResource("Text"), Margin = new Thickness(0, 7, 0, 3) });
                foreach (var value in list.Take(30)) panel.Children.Add(new TextBlock { Text = "• " + value, Classes = { "body" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5, 0, 0, 2) });
                if (list.Count > 30) panel.Children.Add(new TextBlock { Text = $"…另有 {list.Count - 30} 项", Classes = { "sub" } });
            }
            Section("将下载", plan.Files.Select((f, i) => (i == 0 ? "主文件：" : "前置：") + f.FileName + (f.Size > 0 ? $"（{Util.FormatSize(f.Size)}）" : "")));
            Section("安装成功后将替换（旧文件移入回收站）", plan.Replacements.Select(Path.GetFileName));
            Section("已安装，跳过", plan.Skipped);
            Section("找不到适配的前置（需要之后手动处理）", plan.Missing, true);
            var choice = await Dialogs.ShowAsync("确认安装", new ScrollViewer { Content = panel, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, new[] { "取消", "开始安装" }, primary: 1, width: 560);
            return choice == 1;
        }

        async Task ShowInstallCompleteAsync(InstallReport report)
        {
            var text = $"已安装 {report.Installed.Count} 个文件到 {_target.Id}。";
            if (report.Replaced.Count > 0) text += $"\n已替换 {report.Replaced.Count} 个旧文件（旧文件在回收站）。";
            if (report.Missing.Count > 0) text += "\n仍未找到前置：" + string.Join("、", report.Missing);
            var choice = await Dialogs.ShowAsync("安装完成", Dialogs.Text(text), new[] { "完成", "打开管理页" }, primary: 1, width: 480);
            if (choice != 1) return;
            var tab = _type == ContentType.ResourcePack ? "resource" : _type == ContentType.Shader ? "shader" : _type == ContentType.DataPack ? "saves" : "mods";
            MainWindow.Current.GoTab("launch", new InstancePage(_target, tab));
        }
    }
}
