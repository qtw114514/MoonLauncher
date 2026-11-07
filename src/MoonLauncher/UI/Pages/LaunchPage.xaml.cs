using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MoonLauncher.Accounts;
using MoonLauncher.Content;
using MoonLauncher.Core;
using MoonLauncher.Game;

namespace MoonLauncher.UI.Pages
{
    public partial class LaunchPage : UserControl, IPage
    {
        LauncherTask _launchTask;
        GameSession _session;
        readonly StringBuilder _pendingLog = new StringBuilder();
        readonly DispatcherTimer _logTimer;
        bool _launchBusy;
        bool _newsLoaded, _newsLoading;
        string _lastLaunchError;
        string _heroLink;
        string _heroImageUrl;
        Action<string> _lineHandler;
        Action _windowHandler;
        Action<int> _exitHandler;

        public LaunchPage()
        {
            InitializeComponent();
            AccountStore.Changed += () => Dispatcher.BeginInvoke((Action)RefreshAccount);
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (s, e) => FlushLog();
            // 圆角同心裁剪，并按 HTML 的 16:6.6 比例与断点调整头图和摘要。
            NewsCard.SizeChanged += (s, e) =>
            {
                double r = ((CornerRadius)FindResource("Radius.Card")).TopLeft;
                NewsCard.Clip = new RectangleGeometry(new Rect(NewsCard.RenderSize), r, r);
                UpdateNewsLayout();
            };
        }

        void UpdateNewsLayout()
        {
            double width = NewsCard.ActualWidth;
            if (width <= 0) return;
            bool narrow = width < 560;
            NewsHero.Height = Math.Max(narrow ? 300 : 280, Math.Min(440, width * 6.6 / 16));
            NewsFoot.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(320);
            NewsLead.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 32, 0);
            Grid.SetColumn(NewsHeroDesc, narrow ? 0 : 1);
            Grid.SetRow(NewsHeroDesc, narrow ? 1 : 0);
            NewsHeroDesc.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
            NewsHeroDesc.MaxHeight = narrow ? 82 : 102;
        }

        public void OnShown()
        {
            RefreshAccount();
            RefreshVersion();
            RefreshLaunchState();
            if (!_newsLoading && !_newsLoaded) _ = LoadNewsAsync();
        }

        /// <summary>首次使用只串联现有账号和下载页面，不让 Java、内存等高级选项挡住新手。</summary>
        public async Task RunOnboardingAsync()
        {
            if (Settings.OnboardingCompleted) return;
            // 老用户升级后已有账号和版本，不再打断使用。
            if (AccountStore.Selected != null && SelectedInstance() != null) { Settings.OnboardingCompleted = true; return; }
            var start = await Dialogs.Confirm("欢迎使用 MoonLauncher",
                $"接下来只需添加一个账号并安装游戏版本，Java、内存和下载源都会自动处理。\n\n游戏目录：{Settings.SelectedGameFolder}\n之后可以在“版本选择”中更换。",
                "开始设置", "跳过");
            if (!start) { Settings.OnboardingCompleted = true; return; }
            if (AccountStore.Selected == null) await AccountDialogs.ManageAsync();
            if (SelectedInstance() == null)
            {
                Settings.OnboardingInstalling = true;
                MainWindow.Current.GoTab("download");
                Dialogs.Toast("选择一个 Minecraft 版本并点击安装，完成后就能启动游戏");
                return;
            }
            Settings.OnboardingCompleted = true;
            RefreshVersion();
            Dialogs.Toast("准备完成，点击“启动游戏”就能开始", MainWindow.ToastKind.Ok);
        }

        void RefreshLaunchState()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshLaunchState); return; }
            bool running = _session != null && !_session.HasExited;
            bool starting = _launchBusy || (_launchTask != null && _launchTask.IsActive);
            LaunchButton.IsEnabled = !running && !starting;
            LaunchText.Text = running ? "游戏运行中" : starting ? "正在启动…" : _lastLaunchError != null ? "重试启动" : "启动游戏";
            if (running) RunningCard.Visibility = Visibility.Visible;
        }

        // ---------- 账号 ----------
        public async void RefreshAccount()
        {
            var acc = AccountStore.Selected;
            if (acc == null)
            {
                PlayerName.Text = "未登录";
                AccountType.Text = "点击下方添加账户";
                Avatar.Source = null;
                AvatarFallback.Visibility = Visibility.Visible;
                RefreshLaunchState();
                return;
            }
            PlayerName.Text = acc.Name;
            AccountType.Text = acc.Type == Accounts.AccountType.Offline ? "离线登录" : acc.TypeText + "账号";
            Avatar.Source = null;
            AvatarFallback.Visibility = Visibility.Visible;
            RefreshLaunchState();
            try
            {
                var head = await Skins.GetHeadAsync(acc, 64);
                if (head != null && AccountStore.Selected == acc) { Avatar.Source = WpfHost.ToBitmapSource(head); AvatarFallback.Visibility = Visibility.Collapsed; }
            }
            catch (Exception ex) { Log.Warn("获取头像失败：" + ex.Message); }
        }

        void Accounts_Click(object sender, RoutedEventArgs e)
        {
            AccountMenu.IsOpen = !AccountMenu.IsOpen;
            AccountCaretRotation.Angle = AccountMenu.IsOpen ? 180 : 0;
        }

        void AccountMenu_Closed(object sender, EventArgs e) => AccountCaretRotation.Angle = 0;

        void SwitchAccounts_Click(object sender, RoutedEventArgs e)
        {
            AccountMenu.IsOpen = false;
            _ = AccountDialogs.ManageAsync();
        }

        async void NewAccount_Click(object sender, RoutedEventArgs e)
        {
            AccountMenu.IsOpen = false;
            await AccountDialogs.AddAsync();
        }

        // ---------- 版本 ----------
        public static Instance SelectedInstance()
        {
            var repo = GameRepo.Current;
            var id = Settings.SelectedVersion;
            if (!string.IsNullOrEmpty(id) && repo.HasVersion(id)) return new Instance(repo, id);
            var first = repo.ListInstances().Where(i => i.IsValid).OrderByDescending(i => Directory_LastWrite(i.Dir)).FirstOrDefault();
            if (first != null) Settings.SelectedVersion = first.Id;
            return first;
        }

        static DateTime Directory_LastWrite(string dir)
        {
            try { return System.IO.Directory.GetLastWriteTime(dir); } catch { return DateTime.MinValue; }
        }

        public void RefreshVersion()
        {
            RefreshInstances();
            RefreshCurrentInstance();
        }

        void RefreshCurrentInstance() => RefreshLaunchState();

        // ---------- 实例列表（左栏，虚拟化+回收，只滚动自身） ----------
        bool _syncingList;
        List<InstanceItem> _allInstances = new List<InstanceItem>();

        public void RefreshInstances()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshInstances); return; }
            _allInstances = GameRepo.Current.ListInstances().Select(InstanceItem.From).ToList();
            ApplyInstanceFilter();
        }

        void ApplyInstanceFilter()
        {
            string query = (VersionSearch.Text ?? "").Trim();
            var items = _allInstances.Where(x => query.Length == 0 ||
                x.Id.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (x.Desc ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var current = SelectedInstance();
            _syncingList = true;
            try
            {
                InstanceList.ItemsSource = items;
                InstanceEmpty.Text = _allInstances.Count == 0 ? "当前游戏目录还没有实例，请从下载页安装。" : "没有找到匹配的版本";
                InstanceEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                InstanceList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                InstanceList.SelectedItem = current == null ? null : items.FirstOrDefault(x => x.Id == current.Id);
            }
            finally { _syncingList = false; }
        }

        void VersionSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyInstanceFilter();

        void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingList) return;
            if (!(InstanceList.SelectedItem is InstanceItem item) || !item.Instance.IsValid) return;
            Settings.SelectedVersion = item.Id;
            RefreshCurrentInstance();
        }

        void VersionSettings_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            if (inst == null) { Dialogs.Toast("先选择或安装一个版本"); return; }
            MainWindow.Current.Push(new InstancePage(inst));
        }

        void InstanceGear_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is Instance inst && inst.IsValid)
                MainWindow.Current.Push(new InstancePage(inst));
        }

        void OpenGameDir_Click(object sender, RoutedEventArgs e)
        {
            var inst = _session?.Instance ?? SelectedInstance();
            if (inst == null) { Dialogs.Toast("还没有可打开的实例目录，请先安装版本"); return; }
            Util.OpenFolder(inst.GameDir);
        }

        // ---------- 官方新闻（minecraft.net RSS，后台加载，失败给明确错误与重试） ----------
        async Task LoadNewsAsync()
        {
            _newsLoading = true;
            try
            {
                try
                {
                    NewsLoading.Visibility = Visibility.Visible;
                    NewsError.Visibility = Visibility.Collapsed;
                    NewsHero.Visibility = Visibility.Collapsed;
                    NewsHeroImage.Source = null;
                    _heroLink = null;
                    _heroImageUrl = null;
                }
                catch (Exception) { return; } // 窗口已关闭等场景，直接放弃这次加载
                MinecraftNews.Result result;
                try { result = await Task.Run(() => MinecraftNews.LoadAsync()); }
                catch (Exception ex)
                {
                    result = new MinecraftNews.Result { Error = "加载官方新闻失败：" + ex.Message };
                }
                NewsLoading.Visibility = Visibility.Collapsed;
                if (result.Error != null)
                {
                    NewsErrorText.Text = result.Error;
                    NewsError.Visibility = Visibility.Visible;
                    return;
                }

                // 与设计稿一致，只展示首条真实新闻的大图、标题、摘要与官网入口。
                var hero = result.Items[0];
                NewsHeroTitle.Text = hero.Title;
                NewsHeroDesc.Text = hero.Description;
                _heroLink = hero.Link;
                NewsHero.ToolTip = (result.FromCache ? "当前为本地缓存内容。" : "") +
                    (string.IsNullOrEmpty(hero.Link) ? "" : hero.Link + "（点击打开原文）");
                NewsHero.Visibility = Visibility.Visible;
                UpdateNewsLayout();
                if (!string.IsNullOrEmpty(hero.ImageUrl))
                {
                    _heroImageUrl = hero.ImageUrl;
                    _ = LoadHeroImageAsync(hero.ImageUrl);
                }
                _newsLoaded = true;
            }
            catch (Exception ex) { Log.Warn("更新官方新闻界面失败：" + ex.Message); }
            finally { _newsLoading = false; }
        }

        /// <summary>后台下载横幅图（磁盘+内存缓存）；失败时保留玻璃/图标兜底底，不崩。</summary>
        async Task LoadHeroImageAsync(string url)
        {
            try
            {
                var src = await RemoteImage.LoadAsync(url, 960);
                if (src != null && _heroImageUrl == url) NewsHeroImage.Source = src;
            }
            catch (Exception ex) { Log.Warn("加载新闻头图失败：" + ex.Message); }
        }

        void NewsHero_Click(object sender, MouseButtonEventArgs e)
        {
            if (!string.IsNullOrEmpty(_heroLink)) Util.OpenUrl(_heroLink);
        }

        void NewsSource_Click(object sender, RoutedEventArgs e) => Util.OpenUrl(MinecraftNews.SourceUrl);
        void NewsRetry_Click(object sender, RoutedEventArgs e) => _ = LoadNewsAsync();

        // ---------- 右下角工具 ----------
        void DockSettings_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("settings");

        void DockContrast_Click(object sender, RoutedEventArgs e)
        {
            Theme.ToggleBrightness();
            Glass.Apply(MainWindow.Current, Settings.Glass);
        }

        async void DockTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (_session != null && !_session.HasExited)
                await Dialogs.Long("游戏日志", GameLog.Text, width: 720);
            else await MainWindow.Current.MorePage.ShowLauncherLog();
        }

        void DockHelp_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("more");

        // ---------- 启动 ----------
        void Launch_Click(object sender, RoutedEventArgs e) => _ = LaunchAsync();

        public async Task LaunchAsync(Instance inst = null, string serverOverride = null)
        {
            if (_launchBusy || (_launchTask != null && _launchTask.IsActive)) return;
            if (_session != null && !_session.HasExited)
            {
                RunningCard.Visibility = Visibility.Visible;
                RefreshLaunchState();
                return;
            }

            _launchBusy = true;
            RefreshLaunchState();
            Instance reserved = null;
            Account acc = null;
            Func<Task> afterError = null;
            try
            {
                inst = inst ?? SelectedInstance();
                if (inst == null)
                {
                    if (await Dialogs.Confirm("还没有版本", "当前游戏目录里没有可启动的版本。现在去下载一个？", "去下载"))
                        MainWindow.Current.GoTab("download");
                    return;
                }
                if (!LaunchService.TryBeginLaunch(inst)) { Dialogs.Toast("这个版本正在启动或运行中"); return; }
                reserved = inst;
                acc = AccountStore.Selected;
                if (acc == null)
                {
                    await AccountDialogs.ManageAsync();
                    acc = AccountStore.Selected;
                    if (acc == null) return;
                }

                _lastLaunchError = null;
                LaunchProgressPanel.Visibility = Visibility.Visible;
                LaunchStep.Text = "准备启动…";
                LaunchBar.Value = 0;

                var tcs = new TaskCompletionSource<GameSession>();
                _launchTask = TaskManager.Start($"启动 {inst.Id}", async t =>
                {
                    try { tcs.TrySetResult(await LaunchService.LaunchAsync(inst, acc, t, serverOverride)); }
                    catch (Exception ex) { tcs.TrySetException(ex); throw; }
                }, canRetry: false); // 启动由首页重新建立会话和日志订阅，不能重放旧的 TaskCompletionSource
                _launchTask.PropertyChanged += (s, e2) => Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (_launchTask == null) return;
                    LaunchStep.Text = _launchTask.Detail;
                    LaunchBar.IsIndeterminate = _launchTask.Indeterminate;
                    if (!_launchTask.Indeterminate) LaunchBar.Value = _launchTask.Progress;
                }));

                var session = await tcs.Task;
                AttachSession(session);
            }
            catch (OperationCanceledException) { Dialogs.Toast("已取消启动"); }
            catch (Exception ex)
            {
                _lastLaunchError = TaskManager.Flatten(ex);
                var auth = FindAuthException(ex);
                var actions = new List<ErrorDialogAction>();
                if (auth?.NeedsRelogin == true && acc != null)
                {
                    actions.Add(new ErrorDialogAction("重新登录", () =>
                    {
                        afterError = async () => { if (await AccountDialogs.ReloginAsync(acc)) await LaunchAsync(inst, serverOverride); };
                        return Task.CompletedTask;
                    }, primary: true));
                }
                if (_lastLaunchError.IndexOf("java", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    int major = inst.Info?.JavaMajor > 0 ? inst.Info.JavaMajor : 17;
                    string component = major >= 25 ? "java-runtime-epsilon" : major >= 21 ? "java-runtime-delta" : major >= 17 ? "java-runtime-gamma" : "jre-legacy";
                    actions.Add(new ErrorDialogAction($"自动安装 Java {major}", () =>
                    {
                        TaskManager.Start($"下载 Java {major}", async t =>
                        {
                            await JavaManager.DownloadAsync(major, component, t, 0, 1);
                            await JavaManager.ScanAsync(true);
                        });
                        MainWindow.Current.ToggleTasks(true);
                    }));
                }
                actions.Add(new ErrorDialogAction("重试", () =>
                {
                    afterError = () => LaunchAsync(inst, serverOverride);
                    return Task.CompletedTask;
                }, primary: auth?.NeedsRelogin != true));
                await ErrorDialog.ShowAsync("启动失败", _lastLaunchError,
                    auth?.NeedsRelogin == true ? "保存的登录信息已失效。请重新登录当前账号，成功后会自动重试启动。" : "可根据原因检查网络、账号或游戏文件，然后重试。",
                    ex, actions);
            }
            finally
            {
                LaunchService.EndLaunch(reserved);
                _launchBusy = false;
                LaunchProgressPanel.Visibility = Visibility.Collapsed;
                RefreshLaunchState();
            }
            if (afterError != null) await afterError();
        }

        static AuthException FindAuthException(Exception ex)
        {
            if (ex == null) return null;
            if (ex is AuthException auth) return auth;
            if (ex is AggregateException aggregate)
                return aggregate.InnerExceptions.Select(FindAuthException).FirstOrDefault(a => a != null);
            return FindAuthException(ex.InnerException);
        }

        void CancelLaunch_Click(object sender, RoutedEventArgs e) => _launchTask?.Cancel();

        void DetachSessionEvents(GameSession s)
        {
            if (s == null) return;
            if (_lineHandler != null) s.Line -= _lineHandler;
            if (_windowHandler != null) s.WindowAppeared -= _windowHandler;
            if (_exitHandler != null) s.Exited -= _exitHandler;
            _lineHandler = null;
            _windowHandler = null;
            _exitHandler = null;
        }

        void AttachSession(GameSession s)
        {
            DetachSessionEvents(_session);
            _session = s;
            RunningCard.Visibility = Visibility.Visible;
            RunningTitle.Text = $"游戏运行中：{s.Instance.Id}";
            GameLog.Clear();
            lock (_pendingLog) _pendingLog.Clear();
            _logTimer.Start();
            _lineHandler = line => { lock (_pendingLog) _pendingLog.AppendLine(line); };
            s.Line += _lineHandler;
            var early = s.Output;
            if (!string.IsNullOrEmpty(early)) GameLog.Text = early + "\n";
            _windowHandler = () => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (!ReferenceEquals(_session, s)) return;
                Dialogs.Toast("游戏窗口已打开，玩得开心！", MainWindow.ToastKind.Ok);
                switch (Settings.AfterLaunch)
                {
                    case "minimize": MainWindow.Current.WindowState = WindowState.Minimized; break;
                    case "hide": MainWindow.Current.Hide(); break;
                    case "close": Application.Current.Shutdown(); break;
                }
            }));
            s.WindowAppeared += _windowHandler;
            _exitHandler = code => Dispatcher.BeginInvoke((Action)(() => OnGameExit(s, code)));
            s.Exited += _exitHandler;
            RefreshLaunchState();
        }

        void FlushLog()
        {
            string add;
            lock (_pendingLog)
            {
                if (_pendingLog.Length == 0) return;
                add = _pendingLog.ToString();
                _pendingLog.Clear();
            }
            GameLog.AppendText(add);
            if (GameLog.Text.Length > 200_000) GameLog.Text = GameLog.Text.Substring(GameLog.Text.Length - 100_000);
            GameLog.ScrollToEnd();
        }

        async void OnGameExit(GameSession s, int code)
        {
            if (!ReferenceEquals(_session, s)) return;
            FlushLog();
            _logTimer.Stop();
            DetachSessionEvents(s);
            _session = null;
            RunningCard.Visibility = Visibility.Collapsed;
            RefreshLaunchState();
            if (!MainWindow.Current.IsVisible) MainWindow.Current.Show();
            if (MainWindow.Current.WindowState == WindowState.Minimized && (code != 0 || !s.WindowShown)) MainWindow.Current.WindowState = WindowState.Normal;

            if (s.KilledByUser) { Dialogs.Toast("游戏已结束"); return; }
            if (code == 0 && s.WindowShown) return;
            await CrashDialog.ShowAsync(s, code);
        }

        void KillGame_Click(object sender, RoutedEventArgs e) => _session?.Kill();
    }
}
