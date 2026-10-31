using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using MoonLauncher.Core;
using MoonLauncher.Game;

namespace MoonLauncher.UI.Pages
{
    public partial class GameListView : UserControl, IPage
    {
        List<McVersionEntry> _all;
        bool _loading;

        public GameListView()
        {
            InitializeComponent();
        }

        public void OnShown()
        {
            if (_all == null && !_loading) _ = LoadAsync(false);
        }

        async Task LoadAsync(bool force)
        {
            _loading = true;
            Loading.IsVisible = true;
            LoadingText.Text = "正在从官方获取版本列表…";
            List.ItemsSource = null;
            try
            {
                _all = await Task.Run(() => VersionList.GetMinecraftAsync(force));
                Loading.IsVisible = false;
                Apply();
            }
            catch (Exception ex)
            {
                LoadingText.Text = "获取失败：" + ex.Message + "\n检查网络，或在 设置 → 下载 里换下载源后点刷新";
            }
            finally { _loading = false; }
        }

        string _filter = "release";

        void Apply()
        {
            if (_all == null || List == null) return;
            IEnumerable<McVersionEntry> q = _all;
            if (_filter == "release") q = q.Where(v => v.Type == "release");
            else if (_filter == "snapshot") q = q.Where(v => v.Type == "snapshot" && !v.IsAprilFools);
            else if (_filter == "old") q = q.Where(v => v.Type == "old_beta" || v.Type == "old_alpha");
            else if (_filter == "fools") q = q.Where(v => v.IsAprilFools);
            var s = (Search.Text ?? "").Trim();
            if (s.Length > 0) q = q.Where(v => v.Id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            List.ItemsSource = q.ToList();
        }

        void Filter_Changed(object sender, RoutedEventArgs e)
        {
            // Avalonia 的 RadioButton 在 GroupName 内互斥时，Checked 事件可能先于同组其他按钮的
            // IsChecked 复位触发；此时读 IsChecked 拿到的是上一轮的状态，表现为列表"晚一拍"。
            // 用 sender 判定当前选中的是哪个，才和用户操作同步。
            if (sender == FRelease) _filter = "release";
            else if (sender == FSnapshot) _filter = "snapshot";
            else if (sender == FOld) _filter = "old";
            else if (sender == FFools) _filter = "fools";
            else if (sender == FAll) _filter = "all";
            Apply();
        }
        void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync(true);

        void Install_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is McVersionEntry v) MainWindow.Current.Push(new GameInstallPage(v));
        }

        void List_DoubleClick(object sender, TappedEventArgs e)
        {
            if (List.SelectedItem is McVersionEntry v) MainWindow.Current.Push(new GameInstallPage(v));
        }
    }
}
