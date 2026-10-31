using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenLauncher.Core;
using OpenLauncher.UI;
using OpenLauncher.UI.Pages;
using OpenLauncher.Update;

namespace OpenLauncher
{
    public partial class MainWindow : Window
    {
        public static MainWindow Current { get; private set; }

        readonly Dictionary<RadioButton, Stack<FrameworkElement>> _stacks = new Dictionary<RadioButton, Stack<FrameworkElement>>();
        RadioButton _tab;
        DispatcherTimer _toastTimer;

        public LaunchPage LaunchPage { get; private set; }
        public DownloadPage DownloadPage { get; private set; }
        public MultiplayerPage MultiplayerPage { get; private set; }
        public TasksPage TasksPage { get; private set; }
        public SettingsPage SettingsPage { get; private set; }
        public MorePage MorePage { get; private set; }

        // 仅供既有调试截图代码编译；任务入口为右上角任务抽屉与更多域任务页。
        internal Button TasksButton { get; } = new Button();

        public MainWindow()
        {
            Current = this;
            InitializeComponent();
            VersionTag.Text = "v" + AppInfo.VersionString;
            TaskManager.Tasks.CollectionChanged += (s, e) => UpdateTaskBadge();
            TaskManager.Added += t =>
            {
                t.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(LauncherTask.State)) Dispatcher.BeginInvoke((Action)UpdateTaskBadge);
                };
                UpdateTaskBadge();
            };
            TaskManager.Finished += OnTaskFinished;

            LaunchPage = new LaunchPage();
            DownloadPage = new DownloadPage();
            MultiplayerPage = new MultiplayerPage();
            TasksPage = new TasksPage();
            SettingsPage = new SettingsPage();
            MorePage = new MorePage();
            _stacks[NavLaunch] = new Stack<FrameworkElement>(new FrameworkElement[] { LaunchPage });
            _stacks[NavDownload] = new Stack<FrameworkElement>(new FrameworkElement[] { DownloadPage });
            _stacks[NavMultiplayer] = new Stack<FrameworkElement>(new FrameworkElement[] { MultiplayerPage });
            _stacks[NavSettings] = new Stack<FrameworkElement>(new FrameworkElement[] { SettingsPage });
            _stacks[NavMore] = new Stack<FrameworkElement>(new FrameworkElement[] { MorePage });

            ApplyBackground();
            Glass.Apply(this, Settings.Glass);
            UpdateTaskBadge();
            UpdateDialogBounds();
            NavLaunch.IsChecked = true;
            OpenStartupPage();
            // Esc 只收起任务抽屉，不影响页面内其它输入。
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && IsTaskDrawerOpen) { CloseTaskDrawer(); e.Handled = true; }
            };
#if DEBUG
            if (DemoCapture.Active)
            {
                ShowActivated = false;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -6000; Top = 100;
                Loaded += async (s, e) => await DemoCapture.RunAsync();
                return;
            }
#endif
            Loaded += async (s, e) => await StartupChecksAsync();
        }

        // ---------- 导航 ----------
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            CloseTaskDrawer(); // 切换域时收起抽屉（含抽屉内“浏览资源”等跳转）
            _tab = (RadioButton)sender;
            Show(_stacks[_tab].Peek());
        }

        void Show(FrameworkElement page)
        {
            PageHost.Content = page;
            Motion.PageEnter(page);
            (page as IPage)?.OnShown();
        }

        /// <summary>在当前域里打开子页面（有返回）。</summary>
        public void Push(FrameworkElement page)
        {
            if (_tab == null || page == null) return;
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

        public void GoTab(string name, FrameworkElement sub = null)
        {
            name = (name ?? "launch").ToLowerInvariant();
            var rb = name switch
            {
                "resources" or "download" => NavDownload,
                "multiplayer" => NavMultiplayer,
                "tasks" => NavMore,
                "settings" => NavSettings,
                "more" => NavMore,
                _ => NavLaunch,
            };
            var st = _stacks[rb];
            while (st.Count > 1) st.Pop();
            if (rb.IsChecked == true) Show(st.Peek()); else rb.IsChecked = true;
            if (name == "tasks") Push(TasksPage);
            else if (sub != null) Push(sub);
        }

        /// <summary>命令行 --page 保留原语义；resources/download 为资源，tasks 为下载任务。</summary>
        void OpenStartupPage()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            if (i < 0 || i + 1 >= args.Length) return;
            switch (args[i + 1].ToLowerInvariant())
            {
                case "resources":
                case "download": GoTab("resources"); break;
                case "multiplayer": GoTab("multiplayer"); break;
                case "tasks": GoTab("tasks"); break;
                case "settings": GoTab("settings"); break;
                case "more": GoTab("more"); break;
                case "versions":
                    GoTab("launch");
                    Push(new VersionsPage());
                    break;
                case "instance":
                    GoTab("launch");
                    var inst = LaunchPage.SelectedInstance();
                    if (inst != null) Push(new InstancePage(inst, i + 2 < args.Length ? args[i + 2] : null));
                    break;
                case "modsearch":
                    GoTab("resources");
                    DownloadPage.OpenContent(global::OpenLauncher.Content.ContentType.Mod, LaunchPage.SelectedInstance());
                    break;
            }
        }

        // ---------- 标题栏 ----------
        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Window_StateChanged(object sender, EventArgs e)
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDialogBounds();

        // ---------- 任务入口（有活动任务时显示右上角入口；失败任务保留在任务中心可恢复，不计为活动） ----------
        TasksPage _drawerTasks;

        /// <summary>兼容既有页面调用：true 在当前页右侧弹出任务抽屉（不再跳转更多页）；false 关闭抽屉。</summary>
        public void ToggleTasks(bool show)
        {
            if (show) OpenTaskDrawer();
            else CloseTaskDrawer();
        }

        public bool IsTaskDrawerOpen => TaskDrawerLayer.Visibility == Visibility.Visible;

        void OpenTaskDrawer()
        {
            if (IsTaskDrawerOpen) return;
            if (_drawerTasks == null) _drawerTasks = new TasksPage();
            TaskDrawerHost.Content = _drawerTasks;
            TaskDrawerLayer.Visibility = Visibility.Visible;
            _drawerTasks.OnShown();
            if (Motion.ReducedMotion)
            {
                TaskDrawerLayer.Opacity = 1;
                TaskDrawerPanel.RenderTransform = null;
                return;
            }
            TaskDrawerLayer.Opacity = 0;
            var transform = new TranslateTransform(48, 0);
            TaskDrawerPanel.RenderTransform = transform;
            var storyboard = new Storyboard();
            var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)));
            Storyboard.SetTarget(fade, TaskDrawerLayer);
            Storyboard.SetTargetProperty(fade, new PropertyPath(FrameworkElement.OpacityProperty));
            var slide = new DoubleAnimation(48, 0, new Duration(TimeSpan.FromMilliseconds(220)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, new PropertyPath(TranslateTransform.XProperty));
            storyboard.Children.Add(fade);
            storyboard.Children.Add(slide);
            storyboard.Begin();
        }

        void CloseTaskDrawer()
        {
            if (!IsTaskDrawerOpen) return;
            TaskDrawerLayer.Visibility = Visibility.Collapsed;
            TaskDrawerLayer.Opacity = 1;
            TaskDrawerPanel.RenderTransform = null;
        }

        void TasksTitle_Click(object sender, RoutedEventArgs e) => ToggleTasks(!IsTaskDrawerOpen);
        void TaskDrawerScrim_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.Source == sender) CloseTaskDrawer();
        }
        void TaskDrawerClose_Click(object sender, RoutedEventArgs e) => CloseTaskDrawer();

        /// <summary>只有用户明确点“查看全部任务”才进入更多域的完整任务视图。</summary>
        void TaskDrawerViewAll_Click(object sender, RoutedEventArgs e)
        {
            CloseTaskDrawer();
            GoTab("tasks");
        }

        void UpdateTaskBadge()
        {
            bool active = TaskManager.Tasks.Any(t => t.IsActive);
            DownloadTasksButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnTaskFinished(LauncherTask t)
        {
            UpdateTaskBadge();
            if (t.Title.StartsWith("启动 ")) return;
            if (t.State == TaskState.Done) Toast($"{t.Title}：完成", ToastKind.Ok);
            else if (t.State == TaskState.Failed) Toast($"{t.Title}：失败（点右上角任务图标查看原因）", ToastKind.Error);
        }

        // ---------- 提示 ----------
        public enum ToastKind { Info, Ok, Error }

        public void Toast(string text, ToastKind kind = ToastKind.Info)
        {
            ToastText.Text = text;
            ToastIcon.Text = kind == ToastKind.Ok ? "" : kind == ToastKind.Error ? "" : "";
            ToastBox.Visibility = Visibility.Visible;
            Motion.ToastEnter(ToastBox);
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5 : 3) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                Motion.ToastExit(ToastBox, () => ToastBox.Visibility = Visibility.Collapsed);
            };
            _toastTimer.Start();
        }

        // ---------- 对话框 ----------
        readonly Stack<object> _dialogStack = new Stack<object>();

        internal void ShowDialogContent(FrameworkElement content)
        {
            if (DialogHost.Content != null) _dialogStack.Push(DialogHost.Content);
            DialogHost.Content = content;
            DialogLayer.Visibility = Visibility.Visible;
            if (content is Border border)
            {
                border.SetResourceReference(Border.BackgroundProperty, "DialogSurface");
                border.SetResourceReference(Border.BorderBrushProperty, "BorderGradient");
                border.SetResourceReference(Border.EffectProperty, "Shadow.Dialog");
                border.CornerRadius = (CornerRadius)FindResource("Radius.Dialog");
            }
            UpdateDialogBounds();
            Motion.DialogEnter(content);
        }

        internal void CloseDialogContent(FrameworkElement content)
        {
            if (DialogHost.Content != content) return;
            if (_dialogStack.Count > 0) DialogHost.Content = _dialogStack.Pop();
            else { DialogHost.Content = null; DialogLayer.Visibility = Visibility.Collapsed; }
            UpdateDialogBounds();
        }

        void UpdateDialogBounds()
        {
            if (DialogHost == null) return;
            double widthMargin = (double)FindResource("Size.Dialog.ViewportMargin");
            double heightMargin = (double)FindResource("Size.Dialog.ViewportHeightMargin");
            double maxWidth = Math.Max(0, ActualWidth - widthMargin);
            double maxHeight = Math.Max(0, ActualHeight - heightMargin);
            DialogHost.MaxWidth = maxWidth;
            DialogHost.MaxHeight = maxHeight;
            if (DialogHost.Content is FrameworkElement content)
            {
                content.MaxWidth = maxWidth;
                content.MaxHeight = maxHeight;
            }
        }

        // ---------- 背景 ----------
        public void ApplyBackground()
        {
            var img = Theme.LoadImage(Settings.BackgroundImage, 1920);
            BgImage.Source = img;
            BgImage.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- 启动时 ----------
        async Task StartupChecksAsync()
        {
            await LaunchPage.RunOnboardingAsync();
            _ = Task.Run(() => Mirrors.ProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--updated"))
                Toast($"已更新到 {AppInfo.VersionString}", ToastKind.Ok);

            if (Settings.AutoCheckUpdate)
            {
                try
                {
                    await Task.Delay(1500);
                    var info = await Task.Run(() => Updater.CheckAsync());
                    if (info != null && info.IsNewer) await Dialogs.ShowUpdateAsync(info);
                }
                catch (Exception ex) { Log.Warn("自动检查更新失败：" + ex.Message); }
            }
        }
    }

    /// <summary>页面显示时回调（刷新数据用）</summary>
    public interface IPage
    {
        void OnShown();
    }
}
