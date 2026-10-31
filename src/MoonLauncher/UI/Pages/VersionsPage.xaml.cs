using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MoonLauncher.Content;
using MoonLauncher.Core;
using MoonLauncher.Game;

namespace MoonLauncher.UI.Pages
{
    public class InstanceItem
    {
        public Instance Instance { get; set; }
        public string Id => Instance.Id;
        public string Desc { get; set; }
        public Brush Badge { get; set; }
        public string BadgeText { get; set; }

        public static InstanceItem From(Instance i)
        {
            var (resource, text) = i.IsValid ? BadgeFor(i.PrimaryLoader) : ("DangerSoft", "!");
            var brush = Application.Current.TryFindResource(resource) as Brush ?? Brushes.Transparent;
            return new InstanceItem { Instance = i, Desc = i.Description, Badge = brush, BadgeText = text };
        }

        public static (string resource, string text) BadgeFor(LoaderType t) => t switch
        {
            LoaderType.Forge => ("SurfaceElevated", "Fo"),
            LoaderType.NeoForge => ("WarnSoft", "Ne"),
            LoaderType.Fabric => ("CardHover", "Fa"),
            LoaderType.Quilt => ("AccentSoft2", "Q"),
            LoaderType.OptiFine => ("DangerSoft", "Op"),
            LoaderType.LiteLoader => ("AccentSoft2", "Li"),
            _ => ("AccentSoft", "MC"),
        };
    }

    public partial class VersionsPage : UserControl, IPage
    {
        List<InstanceItem> _all = new List<InstanceItem>();
        bool _loading;

        public VersionsPage()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            LoadFolders();
        }

        void LoadFolders()
        {
            _loading = true;
            try
            {
                var folders = Settings.GameFolders;
                Folders.ItemsSource = folders;
                Folders.SelectedItem = folders.FirstOrDefault(f => string.Equals(f.Path, Settings.SelectedGameFolder, StringComparison.OrdinalIgnoreCase)) ?? folders.FirstOrDefault();
            }
            finally { _loading = false; }
            UpdateFolderDetails();
            _ = LoadVersionsAsync();
        }

        void UpdateFolderDetails()
        {
            var f = Folders.SelectedItem as GameFolder;
            bool selected = f != null;
            OpenFolderBtn.IsEnabled = selected;
            RenameFolderBtn.IsEnabled = selected;
            RemoveFolderBtn.IsEnabled = selected;
            FolderPath.Text = selected ? f.Path : "尚未选择游戏目录";
            CurrentFolderName.Text = selected ? f.Name : "未选择游戏目录";
            CurrentFolderPath.Text = selected
                ? f.Path + (Directory.Exists(f.Path) ? "" : "（目录不存在，请检查或重新添加）")
                : "添加或新建一个游戏目录后再选择版本。";
        }

        async Task LoadVersionsAsync()
        {
            if (!(Folders.SelectedItem is GameFolder))
            {
                _all.Clear();
                Versions.ItemsSource = null;
                VersionStats.Text = "没有可用的游戏目录";
                ShowEmpty("没有游戏目录", "先在左侧添加或新建游戏目录。", false);
                return;
            }

            RefreshBtn.IsEnabled = false;
            Versions.IsEnabled = false;
            VersionStats.Text = "正在读取版本…";
            ShowEmpty("正在读取版本", "请稍候。", false);
            try
            {
                var repo = GameRepo.Current;
                var items = await Task.Run(() => repo.ListInstances().Select(InstanceItem.From)
                    .OrderByDescending(i => i.Instance.IsValid)
                    .ThenByDescending(i => SafeTime(i.Instance.Dir)).ToList());
                _all = items;
                ApplyFilter();
                var sel = Settings.SelectedVersion;
                Versions.SelectedItem = _all.FirstOrDefault(i => i.Id == sel);
            }
            catch (Exception ex)
            {
                _all.Clear();
                Versions.ItemsSource = null;
                VersionStats.Text = "版本读取失败";
                ShowEmpty("无法读取这个目录", ex.Message, false);
            }
            finally
            {
                RefreshBtn.IsEnabled = true;
                Versions.IsEnabled = true;
            }
        }

        static DateTime SafeTime(string d) { try { return Directory.GetLastWriteTime(d); } catch { return DateTime.MinValue; } }

        void ShowEmpty(string title, string text, bool download)
        {
            EmptyTitle.Text = title;
            EmptyText.Text = text;
            DownloadBtn.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = Visibility.Visible;
        }

        void ApplyFilter()
        {
            if (Versions == null) return;
            var q = Search.Text.Trim();
            var list = string.IsNullOrEmpty(q) ? _all : _all.Where(i => i.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (i.Desc ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Versions.ItemsSource = list;
            if (_all.Count == 0)
                ShowEmpty("这个目录里还没有版本", "可以导入整合包，或前往下载页安装一个版本。", true);
            else if (list.Count == 0)
                ShowEmpty("没有匹配的版本", "换一个关键词，或清空搜索条件。", false);
            else
                Empty.Visibility = Visibility.Collapsed;
            int invalid = _all.Count(i => !i.Instance.IsValid);
            VersionStats.Text = list.Count == _all.Count
                ? $"共 {_all.Count} 个版本" + (invalid > 0 ? $" · {invalid} 个无效版本（可打开设置查看错误）" : "")
                : $"找到 {list.Count} / {_all.Count} 个版本";
        }

        void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
        void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadVersionsAsync();
        void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Back();
        void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("download");

        void Folders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFolderDetails();
            if (_loading || !(Folders.SelectedItem is GameFolder f)) return;
            Settings.SelectedGameFolder = f.Path;
            _ = LoadVersionsAsync();
        }

        void Select(InstanceItem item)
        {
            if (item == null) return;
            Settings.SelectedVersion = item.Id;
            MainWindow.Current.LaunchPage.RefreshVersion();
        }

        void Versions_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Versions.SelectedItem is InstanceItem item)
            {
                Select(item);
                MainWindow.Current.Back();
            }
        }

        static InstanceItem ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as InstanceItem;

        void SelectItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            Select(item);
            MainWindow.Current.Back();
        }

        void LaunchItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            Select(item);
            MainWindow.Current.Back();
            _ = MainWindow.Current.LaunchPage.LaunchAsync(item.Instance);
        }

        void SettingsItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item == null) return;
            Select(item);
            MainWindow.Current.Push(new InstancePage(item.Instance));
        }

        void FolderItem_Click(object sender, RoutedEventArgs e)
        {
            var item = ItemOf(sender);
            if (item != null) Util.OpenFolder(item.Instance.Dir);
        }

        async void ImportPack_Click(object sender, RoutedEventArgs e)
        {
            var file = Pickers.PickFile("选择整合包", "整合包 (*.mrpack;*.zip)|*.mrpack;*.zip|所有文件|*.*");
            if (file == null) return;
            await ImportPackAsync(file);
        }

        public static async Task ImportPackAsync(string file)
        {
            var name = await Dialogs.Prompt("导入整合包", "给这个整合包起个版本名（留空用整合包自己的名字）", "");
            if (name == null) return;
            var repo = GameRepo.Current;
            TaskManager.Start("导入整合包 " + Path.GetFileName(file), async t =>
            {
                var id = await ModpackImporter.ImportAsync(repo, file, string.IsNullOrWhiteSpace(name) ? null : name, t);
                Settings.SelectedVersion = id;
                TaskManager.OnUi(() => MainWindow.Current.LaunchPage.RefreshVersion());
            });
            MainWindow.Current.ToggleTasks(true);
        }

        // ---------- 游戏目录管理 ----------
        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = Pickers.PickFolder("选择一个 .minecraft 文件夹");
            if (path == null) return;
            if (!Directory.Exists(Path.Combine(path, "versions")) && Directory.Exists(Path.Combine(path, ".minecraft"))) path = Path.Combine(path, ".minecraft");
            AddFolder(path, Path.GetFileName(Path.GetDirectoryName(path)) ?? "游戏目录");
        }

        void NewFolder_Click(object sender, RoutedEventArgs e)
        {
            var path = Pickers.PickFolder("选择新游戏目录的位置（会在里面创建 .minecraft）");
            if (path == null) return;
            path = Path.Combine(path, ".minecraft");
            Directory.CreateDirectory(path);
            AddFolder(path, "新游戏目录");
        }

        void AddFolder(string path, string name)
        {
            var list = Settings.GameFolders;
            if (!list.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                list.Add(new GameFolder { Name = name, Path = path });
            Settings.GameFolders = list;
            Settings.SelectedGameFolder = path;
            LoadFolders();
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e) { if (Folders.SelectedItem is GameFolder f) Util.OpenFolder(f.Path); }

        async void RenameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(Folders.SelectedItem is GameFolder f)) return;
            var n = await Dialogs.Prompt("重命名", "显示名称", f.Name);
            if (string.IsNullOrWhiteSpace(n)) return;
            var list = Settings.GameFolders;
            foreach (var x in list) if (string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase)) x.Name = n;
            Settings.GameFolders = list;
            LoadFolders();
        }

        void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!(Folders.SelectedItem is GameFolder f)) return;
            var list = Settings.GameFolders.Where(x => !string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 0) { Dialogs.Toast("至少要保留一个游戏目录"); return; }
            Settings.GameFolders = list;
            Settings.SelectedGameFolder = list[0].Path;
            LoadFolders();
        }
    }
}
