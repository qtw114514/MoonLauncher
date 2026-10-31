using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Platform;
using Avalonia.Threading;
using MoonLauncher.Core;
using MoonLauncher.UI;
using MoonLauncher.UI.Pages;
using MoonLauncher.Update;

namespace MoonLauncher
{
    /// <summary>
    /// 主窗口（Avalonia 版）。
    /// WPF 的自绘标题栏（WindowChrome）换成了系统标题栏 + 顶部导航条：
    /// 这样三个平台的窗口按钮、缩放、全屏都由系统处理，不用各自适配。
    /// </summary>
    public partial class MainWindow : Window
    {
        public static MainWindow Current { get; private set; }

        /// <summary>命令行 --page 指定启动页（快捷方式 / 测试用）</summary>
        public static string[] StartupPages { get; set; }

        readonly Dictionary<RadioButton, Stack<Control>> _stacks = new Dictionary<RadioButton, Stack<Control>>();
        RadioButton _tab;
        DispatcherTimer _toastTimer;

        public LaunchPage LaunchPage { get; private set; }
        public DownloadPage DownloadPage { get; private set; }
        public SettingsPage SettingsPage { get; private set; }
        public MorePage MorePage { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Current = this;

            VersionTag.Text = "v" + AppInfo.VersionString;
            TryLoadIcon();

            TaskList.ItemsSource = TaskManager.Tasks;
            TaskManager.Tasks.CollectionChanged += (s, e) => UpdateTaskBadge();
            TaskManager.Added += t =>
            {
                t.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LauncherTask.State)) Dispatcher.UIThread.Post(UpdateTaskBadge); };
                UpdateTaskBadge();
            };
            TaskManager.Finished += OnTaskFinished;

            LaunchPage = new LaunchPage();
            DownloadPage = new DownloadPage();
            SettingsPage = new SettingsPage();
            MorePage = new MorePage();

            _stacks[NavLaunch] = new Stack<Control>(new Control[] { LaunchPage });
            _stacks[NavDownload] = new Stack<Control>(new Control[] { DownloadPage });
            _stacks[NavSettings] = new Stack<Control>(new Control[] { SettingsPage });
            _stacks[NavMore] = new Stack<Control>(new Control[] { MorePage });

            ApplyBackground();
            _tab = NavLaunch;
            Show(LaunchPage);

            Opened += (s, e) =>
            {
                OpenStartupPage();
                _ = StartupChecksAsync();
            };
        }

        void TryLoadIcon()
        {
            try
            {
                using (var s = AssetLoader.Open(new Uri("avares://MoonLauncher/Assets/icon.png")))
                {
                    var bmp = new Bitmap(s);
                    Icon = new WindowIcon(bmp);
                    AppIcon.Source = bmp;
                }
            }
            catch (Exception ex) { Log.Warn("加载图标失败：" + ex.Message); }
        }

        public void ApplyBackground()
        {
            var path = Settings.BackgroundImage;
            if (string.IsNullOrEmpty(path)) return;
            var bmp = UI.Theme.LoadImage(path);
            if (bmp == null) return;
            Background = new Avalonia.Media.ImageBrush(bmp) { Opacity = 0.28, Stretch = Avalonia.Media.Stretch.UniformToFill };
        }

        // ---------- 导航 ----------
        void Nav_Checked(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (sender is not RadioButton rb) return;
            _tab = rb;
            if (_stacks.TryGetValue(rb, out var st)) Show(st.Peek());
        }

        void Show(Control page)
        {
            PageHost.Content = page;
            (page as IPage)?.OnShown();
        }

        public void Push(Control page)
        {
            if (_tab == null) return;
            _stacks[_tab].Push(page);
            Show(page);
        }

        public void Back()
        {
            if (_tab == null) return;
            var st = _stacks[_tab];
            if (st.Count > 1) st.Pop();
            Show(st.Peek());
        }

        public void GoTab(string name, Control sub = null)
        {
            var rb = name switch
            {
                "download" => NavDownload,
                "settings" => NavSettings,
                "more" => NavMore,
                _ => NavLaunch,
            };
            var st = _stacks[rb];
            while (st.Count > 1) st.Pop();
            if (rb.IsChecked == true) Show(st.Peek()); else rb.IsChecked = true;
            if (sub != null) Push(sub);
        }

        void OpenStartupPage()
        {
            var args = StartupPages ?? Array.Empty<string>();
            int i = Array.IndexOf(args, "--page");
            if (i < 0 || i + 1 >= args.Length) return;
            switch (args[i + 1])
            {
                case "download": GoTab("download"); break;
                case "settings": GoTab("settings"); break;
                case "more": GoTab("more"); break;
            }
        }

        async Task StartupChecksAsync()
        {
            if (!Settings.AutoCheckUpdate) return;
            try
            {
                await Task.Delay(1500);
                var info = await Task.Run(() => Updater.CheckAsync());
                if (info != null && info.IsNewer) await Dialogs.ShowUpdateAsync(info);
            }
            catch (Exception ex) { Log.Warn("自动检查更新失败：" + ex.Message); }
        }

        // ---------- 任务抽屉 ----------
        void Tasks_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleTasks(!TaskLayer.IsVisible);

        void ClearTasks_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e) => TaskManager.ClearFinished();

        static LauncherTask TaskOf(object sender) => (sender as Control)?.DataContext as LauncherTask;

        void CancelTask_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e) => TaskOf(sender)?.Cancel();
        void PauseTask_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e) => TaskOf(sender)?.Pause();
        void ResumeTask_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e) => TaskOf(sender)?.Resume();

        void RetryTask_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (task != null && task.CanRetry) TaskManager.Retry(task);
        }

        public void ToggleTasks(bool show)
        {
            TaskLayer.IsVisible = show;
            NoTasks.IsVisible = TaskManager.Tasks.Count == 0;
        }

        void UpdateTaskBadge()
        {
            int running = TaskManager.Tasks.Count(t => t.IsActive);
            TaskCount.Text = running > 0 ? running.ToString() : "";
            NoTasks.IsVisible = TaskManager.Tasks.Count == 0;
        }

        void OnTaskFinished(LauncherTask t)
        {
            UpdateTaskBadge();
            if (t.Title.StartsWith("启动 ")) return;
            if (t.State == TaskState.Done) Toast($"{t.Title}：完成", ToastKind.Ok);
            else if (t.State == TaskState.Failed) Toast($"{t.Title}：失败（点右上角任务查看原因）", ToastKind.Error);
        }

        // ---------- 提示 ----------
        public enum ToastKind { Info, Ok, Error }

        public void Toast(string text, ToastKind kind = ToastKind.Info)
        {
            ToastText.Text = text;
            ToastBox.IsVisible = true;
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5 : 3) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastBox.IsVisible = false;
            };
            _toastTimer.Start();
        }

        public void ToastOk(string text) => Toast(text, ToastKind.Ok);
        public void ToastError(string text) => Toast(text, ToastKind.Error);

        // ---------- 窗内对话框 ----------
        readonly Stack<object> _dialogStack = new Stack<object>();

        internal void ShowDialogContent(Control content)
        {
            if (DialogHost.Content != null) _dialogStack.Push(DialogHost.Content);
            DialogHost.Content = content;
            DialogLayer.IsVisible = true;
        }

        internal void CloseDialogContent(Control content)
        {
            if (ReferenceEquals(DialogHost.Content, content))
            {
                DialogHost.Content = _dialogStack.Count > 0 ? _dialogStack.Pop() : null;
                DialogLayer.IsVisible = DialogHost.Content != null;
            }
            else
            {
                // 关的不是栈顶：从栈里删掉
                if (_dialogStack.Contains(content))
                {
                    var keep = _dialogStack.Where(x => !ReferenceEquals(x, content)).Reverse().ToList();
                    _dialogStack.Clear();
                    foreach (var x in keep) _dialogStack.Push(x);
                }
            }
        }
    }

    /// <summary>页面显示时回调（刷新数据用）</summary>
    public interface IPage
    {
        void OnShown();
    }
}
