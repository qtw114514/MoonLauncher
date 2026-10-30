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
        public SettingsPage SettingsPage { get; private set; }
        public MorePage MorePage { get; private set; }

        public MainWindow()
        {
            Current = this;
            InitializeComponent();
            VersionTag.Text = "v" + AppInfo.VersionString;
            TaskList.ItemsSource = TaskManager.Tasks;
            TaskManager.Tasks.CollectionChanged += (s, e) => UpdateTaskBadge();
            TaskManager.Added += t => { t.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LauncherTask.State)) Dispatcher.BeginInvoke((Action)UpdateTaskBadge); }; UpdateTaskBadge(); };
            TaskManager.Finished += OnTaskFinished;

            LaunchPage = new LaunchPage();
            DownloadPage = new DownloadPage();
            SettingsPage = new SettingsPage();
            MorePage = new MorePage();
            _stacks[NavLaunch] = new Stack<FrameworkElement>(new FrameworkElement[] { LaunchPage });
            _stacks[NavDownload] = new Stack<FrameworkElement>(new FrameworkElement[] { DownloadPage });
            _stacks[NavSettings] = new Stack<FrameworkElement>(new FrameworkElement[] { SettingsPage });
            _stacks[NavMore] = new Stack<FrameworkElement>(new FrameworkElement[] { MorePage });

            ApplyBackground();
            Glass.Apply(this, Settings.Glass); // 液态玻璃（若开启）：亚克力模糊 + 半透明资源，开关即时生效
            NavLaunch.IsChecked = true;
            OpenStartupPage();
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
            _tab = (RadioButton)sender;
            Show(_stacks[_tab].Peek());
        }

        void Show(FrameworkElement page)
        {
            PageHost.Content = page;
            page.Opacity = 0;
            var tt = new TranslateTransform(0, 14);
            page.RenderTransform = tt;
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
            (page as IPage)?.OnShown();
        }

        /// <summary>在当前标签页里打开子页面（有返回）</summary>
        public void Push(FrameworkElement page)
        {
            _stacks[_tab].Push(page);
            Show(page);
        }

        public void Back()
        {
            var st = _stacks[_tab];
            if (st.Count > 1) st.Pop();
            Show(st.Peek());
        }

        public void GoTab(string name, FrameworkElement sub = null)
        {
            var rb = name switch { "download" => NavDownload, "settings" => NavSettings, "more" => NavMore, _ => NavLaunch };
            var st = _stacks[rb];
            while (st.Count > 1) st.Pop();
            if (rb.IsChecked == true) Show(st.Peek()); else rb.IsChecked = true;
            if (sub != null) Push(sub);
        }

        /// <summary>命令行 --page download|settings|more|versions|mods|modsearch 直接打开某页（快捷方式 / 测试用）</summary>
        void OpenStartupPage()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            if (i < 0 || i + 1 >= args.Length) return;
            switch (args[i + 1])
            {
                case "download": GoTab("download"); break;
                case "settings": GoTab("settings"); break;
                case "more": GoTab("more"); break;
                case "versions": Push(new VersionsPage()); break;
                case "instance":
                    var inst = LaunchPage.SelectedInstance();
                    if (inst != null) Push(new InstancePage(inst, i + 2 < args.Length ? args[i + 2] : null));
                    break;
                case "modsearch":
                    GoTab("download");
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
            // 无边框窗口最大化时会溢出屏幕边缘几个像素
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        // ---------- 任务 ----------
        void Tasks_Click(object sender, RoutedEventArgs e) => ToggleTasks(TaskLayer.Visibility != Visibility.Visible);
        void TaskLayer_MouseDown(object sender, MouseButtonEventArgs e) => ToggleTasks(false);
        void ClearTasks_Click(object sender, RoutedEventArgs e) => TaskManager.ClearFinished();
        static LauncherTask TaskOf(object sender) => (sender as FrameworkElement)?.DataContext as LauncherTask;
        void CancelTask_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Cancel();
        void PauseTask_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Pause();
        void ResumeTask_Click(object sender, RoutedEventArgs e) => TaskOf(sender)?.Resume();
        void RetryTask_Click(object sender, RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (task != null && task.CanRetry) TaskManager.Retry(task);
        }
        void CopyTask_Click(object sender, RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (task == null) return;
            try
            {
                Clipboard.SetText($"{task.Title}\r\n状态：{task.StateText}\r\n{task.Detail}\r\n{task.Error}");
                Toast("诊断信息已复制", ToastKind.Ok);
            }
            catch (Exception ex) { Toast("复制失败：" + ex.Message, ToastKind.Error); }
        }

        void TaskDetails_Click(object sender, RoutedEventArgs e)
        {
            var task = TaskOf(sender);
            if (task == null) return;
            var summary = string.IsNullOrWhiteSpace(task.Error) ? task.Detail : task.Error;
            var details = $"{task.Title}\r\n状态：{task.StateText}\r\n开始时间：{task.Started:yyyy-MM-dd HH:mm:ss}\r\n{task.Detail}\r\n{task.Error}";
            var actions = new List<ErrorDialogAction>();
            if (task.CanRetry)
                actions.Add(new ErrorDialogAction("重试任务", () => { TaskManager.Retry(task); ToggleTasks(true); }, primary: true));
            _ = ErrorDialog.ShowAsync(task.Title + "失败", summary,
                task.CanRetry ? "可以直接重试任务；若再次失败，请复制技术详情排查。" : "请根据原因处理后，从原页面重新发起任务。",
                details, actions);
        }

        public void ToggleTasks(bool show)
        {
            TaskLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            NoTasks.Visibility = TaskManager.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateTaskBadge()
        {
            int running = TaskManager.Tasks.Count(t => t.IsActive);
            TaskCount.Text = running > 0 ? running.ToString() : "";
            NoTasks.Visibility = TaskManager.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnTaskFinished(LauncherTask t)
        {
            UpdateTaskBadge();
            if (t.Title.StartsWith("启动 ")) return; // 启动页自己会提示（窗口出现 / 失败对话框）
            if (t.State == TaskState.Done) Toast($"{t.Title}：完成", ToastKind.Ok);
            else if (t.State == TaskState.Failed) Toast($"{t.Title}：失败（点右上角任务查看原因）", ToastKind.Error);
        }

        // ---------- 提示 ----------
        public enum ToastKind { Info, Ok, Error }

        public void Toast(string text, ToastKind kind = ToastKind.Info)
        {
            ToastText.Text = text;
            ToastIcon.Text = kind == ToastKind.Ok ? "" : kind == ToastKind.Error ? "" : "";
            ToastBox.Visibility = Visibility.Visible;
            // 苹果式：从下方弹性浮入
            var tt = ToastBox.RenderTransform as TranslateTransform ?? new TranslateTransform();
            ToastBox.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5 : 3) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                anim.Completed += (s2, e2) => ToastBox.Visibility = Visibility.Collapsed;
                ToastBox.BeginAnimation(OpacityProperty, anim);
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
            content.Opacity = 0;
            // 苹果式弹窗：小幅缩放过冲回弹
            var st = new ScaleTransform(0.92, 0.92);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = st;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = spring });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = spring });
        }

        internal void CloseDialogContent(FrameworkElement content)
        {
            if (DialogHost.Content != content) return;
            if (_dialogStack.Count > 0) DialogHost.Content = _dialogStack.Pop();
            else { DialogHost.Content = null; DialogLayer.Visibility = Visibility.Collapsed; }
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
