using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
        bool _latestLoaded, _launchBusy;
        string _lastLaunchError;
        Action<string> _lineHandler;
        Action _windowHandler;
        Action<int> _exitHandler;

        public LaunchPage()
        {
            InitializeComponent();
            AccountStore.Changed += () => Dispatcher.UIThread.Post((Action)RefreshAccount);
            Settings.Changed += RefreshHomeCard;
            RefreshHomeCard();
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (s, e) => FlushLog();
            var h = DateTime.Now.Hour;
            Greeting.Text = h < 6 ? "夜深了，注意休息" : h < 12 ? "早上好" : h < 14 ? "中午好" : h < 18 ? "下午好" : "晚上好";
        }

        public void OnShown()
        {
            RefreshAccount();
            RefreshVersion();
            RefreshLaunchState();
            if (!_latestLoaded) _ = LoadLatestAsync();
            if (!_newsLoaded && !_newsLoading) _ = LoadNewsAsync();
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
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post((Action)RefreshLaunchState); return; }
            bool running = _session != null && !_session.HasExited;
            bool starting = _launchBusy || (_launchTask != null && _launchTask.IsActive);
            LaunchButton.IsEnabled = !running && !starting;
            LaunchText.Text = running ? "游戏运行中" : starting ? "正在启动…" : _lastLaunchError != null ? "重试启动" : "启动游戏";
            if (running) RunningCard.IsVisible = true;

            // 状态卡：把“差什么”直接说清楚，而不是让用户自己排查
            var acc = AccountStore.Selected;
            var inst = SelectedInstance();
            string title, desc;
            if (running) { title = "游戏正在运行"; desc = "需要修改变量时先结束游戏。"; }
            else if (starting) { title = "正在启动游戏"; desc = "首次启动需要准备资源文件，可能需要几分钟。"; }
            else if (acc == null) { title = "还没有登录账户"; desc = "添加一个账户后即可启动游戏。"; }
            else if (inst == null) { title = "还没有安装版本"; desc = "去资源页挑一个版本安装，原版与常见加载器都支持。"; }
            else if (_lastLaunchError != null) { title = "上次启动失败"; desc = _lastLaunchError; }
            else { title = "准备就绪"; desc = $"将以 {acc.Name} 启动 {inst.Id}。"; }
            StatusTitle.Text = title;
            StatusDescription.Text = desc;
        }

        // ---------- 账号 ----------
        public async void RefreshAccount()
        {
            var acc = AccountStore.Selected;
            if (acc == null)
            {
                PlayerName.Text = "未登录";
                AccountType.Text = "点击下方添加账号";
                Avatar.Source = null;
                AvatarFallback.IsVisible = true;
                return;
            }
            PlayerName.Text = acc.Name;
            AccountType.Text = acc.TypeText + "账号";
            Avatar.Source = null;
            AvatarFallback.IsVisible = true;
            try
            {
                var head = await Skins.GetHeadAsync(acc, 64);
                if (head != null && AccountStore.Selected == acc) { Avatar.Source = AvaloniaHost.ToBitmap(head); AvatarFallback.IsVisible = false; }
            }
            catch (Exception ex) { Log.Warn("获取头像失败：" + ex.Message); }
        }

        void Accounts_Click(object sender, RoutedEventArgs e) => _ = AccountDialogs.ManageAsync();

        // ---------- 自定义主页 ----------
        string _homeNavigated;
        void RefreshHomeCard()
        {
            if (HomeCard == null) return;
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post((Action)RefreshHomeCard); return; }
            var raw = (Settings.Get("home.url") ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                HomeCard.IsVisible = false;
                _homeNavigated = null;
                return;
            }
            Uri uri = null;
            try
            {
                uri = System.IO.File.Exists(raw)
                    ? new Uri(System.IO.Path.GetFullPath(raw))
                    : new Uri(raw, UriKind.Absolute);
                if (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "file") uri = null;
            }
            catch { uri = null; }
            if (uri == null)
            {
                HomeCard.IsVisible = true;
                HomeHint.IsVisible = true;
                _homeNavigated = null;
                return;
            }
            HomeCard.IsVisible = true;
            HomeHint.IsVisible = false;
            var key = uri.ToString();
            if (_homeNavigated != key) _homeNavigated = key;
        }

        void OpenHome_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_homeNavigated)) Util.OpenUrl(_homeNavigated);
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
            var inst = SelectedInstance();
            if (inst == null)
            {
                LaunchSub.Text = "还没有安装任何版本";
                InstanceName.Text = "尚未安装实例";
                InstanceCard.IsVisible = true;
                InstanceDesc.Text = $"尚未安装版本\n游戏目录：{GameRepo.Current.Root}\n去资源页安装一个版本即可开始。";
                RefreshInstances();
                RefreshLaunchState();
                return;
            }
            LaunchSub.Text = inst.Id;
            InstanceName.Text = inst.Id;
            InstanceCard.IsVisible = true;
            InstanceDesc.Text = $"{inst.Description}\n游戏目录：{inst.GameDir}" + (inst.Isolated ? "（已开启版本隔离）" : "");
            RefreshInstances();
            RefreshLaunchState();
        }

        /// <summary>刷新左下角的实例列表；只列有效实例，按目录修改时间倒序。</summary>
        public void RefreshInstances()
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post((Action)RefreshInstances); return; }
            var items = GameRepo.Current.ListInstances()
                .Where(i => i.IsValid)
                .OrderByDescending(i => Directory_LastWrite(i.Dir))
                .ToList();
            InstanceList.ItemsSource = items;
            bool empty = items.Count == 0;
            InstanceEmpty.IsVisible = empty;
            InstanceList.IsVisible = !empty;
            var cur = SelectedInstance();
            if (cur != null) InstanceList.SelectedItem = items.FirstOrDefault(i => i.Id == cur.Id);
        }

        void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InstanceList.SelectedItem is not Instance inst) return;
            if (Settings.SelectedVersion == inst.Id) return;
            Settings.SelectedVersion = inst.Id;
            RefreshVersion();
            RefreshLaunchState();
        }

        void Versions_Click(object sender, RoutedEventArgs e) => MainWindow.Current.Push(new VersionsPage());

        void VersionSettings_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            if (inst == null) { Dialogs.Toast("先选择或安装一个版本"); return; }
            MainWindow.Current.Push(new InstancePage(inst));
        }

        void ManageMods_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            if (inst != null) MainWindow.Current.Push(new InstancePage(inst, "mods"));
        }

        void DownloadMods_Click(object sender, RoutedEventArgs e)
        {
            var inst = SelectedInstance();
            MainWindow.Current.GoTab("download");
            MainWindow.Current.DownloadPage.OpenContent(ContentType.Mod, inst);
        }

        void OpenGameDir_Click(object sender, RoutedEventArgs e)
        {
            var inst = _session?.Instance ?? SelectedInstance();
            if (inst != null) Util.OpenFolder(inst.GameDir);
        }

        void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("download");

        async Task LoadLatestAsync()
        {
            try
            {
                // 只做缓存预热：最新版本的快捷入口在新版资源页里，不在启动台
                await Task.Run(() => VersionList.GetMinecraftAsync());
                _latestLoaded = true;
            }
            catch (Exception ex) { Log.Warn("获取最新版本失败：" + ex.Message); }
        }

        async void InstallLatest_Click(object sender, RoutedEventArgs e)
        {
            var id = (string)((Button)sender).Tag;
            ((Button)sender).IsEnabled = false;
            var repo = GameRepo.Current;
            TaskManager.Start($"安装 {id}", async t =>
            {
                var name = await Installer.InstallAsync(new InstallRequest { Repo = repo, McVersion = id }, t);
                Settings.SelectedVersion = name;
                TaskManager.OnUi(RefreshVersion);
            });
            MainWindow.Current.ToggleTasks(true);
        }

        // ---------- 启动 ----------
        void Launch_Click(object sender, RoutedEventArgs e) => _ = LaunchAsync();

        public async Task LaunchAsync(Instance inst = null, string serverOverride = null)
        {
            if (_launchBusy || (_launchTask != null && _launchTask.IsActive)) return;
            if (_session != null && !_session.HasExited)
            {
                RunningCard.IsVisible = true;
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
                LaunchProgressPanel.IsVisible = true;
                LaunchStep.Text = "准备启动…";
                LaunchBar.Value = 0;

                var tcs = new TaskCompletionSource<GameSession>();
                _launchTask = TaskManager.Start($"启动 {inst.Id}", async t =>
                {
                    try { tcs.TrySetResult(await LaunchService.LaunchAsync(inst, acc, t, serverOverride)); }
                    catch (Exception ex) { tcs.TrySetException(ex); throw; }
                }, canRetry: false); // 启动由首页重新建立会话和日志订阅，不能重放旧的 TaskCompletionSource
                _launchTask.PropertyChanged += (s, e2) => Dispatcher.UIThread.Post((Action)(() =>
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
                LaunchProgressPanel.IsVisible = false;
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
            RunningCard.IsVisible = true;
            RunningTitle.Text = $"游戏运行中：{s.Instance.Id}";
            GameLog.Clear();
            lock (_pendingLog) _pendingLog.Clear();
            _logTimer.Start();
            _lineHandler = line => { lock (_pendingLog) _pendingLog.AppendLine(line); };
            s.Line += _lineHandler;
            var early = s.Output;
            if (!string.IsNullOrEmpty(early)) GameLog.Text = early + "\n";
            _windowHandler = () => Dispatcher.UIThread.Post((Action)(() =>
            {
                if (!ReferenceEquals(_session, s)) return;
                Dialogs.Toast("游戏窗口已打开，玩得开心！", MainWindow.ToastKind.Ok);
                switch (Settings.AfterLaunch)
                {
                    case "minimize": MainWindow.Current.WindowState = WindowState.Minimized; break;
                    case "hide": MainWindow.Current.Hide(); break;
                    case "close": (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown(); break;
                }
            }));
            s.WindowAppeared += _windowHandler;
            _exitHandler = code => Dispatcher.UIThread.Post((Action)(() => OnGameExit(s, code)));
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
            GameLog.Text += add;
            if (GameLog.Text.Length > 200_000) GameLog.Text = GameLog.Text.Substring(GameLog.Text.Length - 100_000);
            GameLog.CaretIndex = GameLog.Text.Length;
        }

        async void OnGameExit(GameSession s, int code)
        {
            if (!ReferenceEquals(_session, s)) return;
            FlushLog();
            _logTimer.Stop();
            DetachSessionEvents(s);
            _session = null;
            RunningCard.IsVisible = false;
            RefreshLaunchState();
            if (!MainWindow.Current.IsVisible) MainWindow.Current.Show();
            if (MainWindow.Current.WindowState == WindowState.Minimized && (code != 0 || !s.WindowShown)) MainWindow.Current.WindowState = WindowState.Normal;

            if (s.KilledByUser) { Dialogs.Toast("游戏已结束"); return; }
            if (code == 0 && s.WindowShown) return;
            await CrashDialog.ShowAsync(s, code);
        }

        void KillGame_Click(object sender, RoutedEventArgs e) => _session?.Kill();

        // ---------- 官方新闻 ----------
        bool _newsLoading, _newsLoaded;
        string _heroLink, _heroImageUrl;

        async Task LoadNewsAsync()
        {
            if (_newsLoading) return;
            _newsLoading = true;
            try
            {
                NewsLoading.IsVisible = true;
                NewsError.IsVisible = false;
                NewsItems.IsVisible = false;
                NewsHero.IsVisible = false;
                NewsHeroImage.Source = null;
                _heroLink = null;
                _heroImageUrl = null;

                MinecraftNews.Result result;
                try { result = await Task.Run(() => MinecraftNews.LoadAsync()); }
                catch (Exception ex) { result = new MinecraftNews.Result { Error = "加载官方新闻失败：" + ex.Message }; }

                NewsLoading.IsVisible = false;
                if (result.Error != null)
                {
                    NewsErrorText.Text = result.Error;
                    NewsError.IsVisible = true;
                    return;
                }

                NewsItems.Children.Clear();
                if (result.FromCache)
                {
                    NewsItems.Children.Add(new TextBlock
                    {
                        Text = "暂时连不上官网，以下为本地缓存（1 小时内）内容。",
                        Classes = { "sub" },
                        Margin = new Thickness(0, 0, 0, 6),
                    });
                }

                // 首条新闻：顶部大图横幅（标题/日期/摘要压在深蓝黑渐变承托上），点击打开原文。
                if (result.Items.Count > 0)
                {
                    var hero = result.Items[0];
                    NewsHeroDate.Text = hero.Date;
                    NewsHeroTitle.Text = hero.Title;
                    NewsHeroDesc.Text = hero.Description;
                    _heroLink = hero.Link;
                    ToolTip.SetTip(NewsHero, string.IsNullOrEmpty(hero.Link) ? null : hero.Link + "（点击打开原文）");
                    NewsHero.IsVisible = true;
                    if (!string.IsNullOrEmpty(hero.ImageUrl))
                    {
                        _heroImageUrl = hero.ImageUrl;
                        _ = LoadHeroImageAsync(hero.ImageUrl);
                    }
                }

                bool first = true;
                foreach (var item in result.Items.Skip(1))
                {
                    if (!first)
                        NewsItems.Children.Add(new Border { Height = 1, Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 0, 4), IsHitTestVisible = false });
                    AddNewsItem(item);
                    first = false;
                }

                NewsItems.IsVisible = true;
                _newsLoaded = true;
            }
            catch (Exception ex) { Log.Warn("更新官方新闻界面失败：" + ex.Message); }
            finally { _newsLoading = false; }
        }

        /// <summary>后台下载横幅图（磁盘 + 内存缓存）；失败时保留图标兜底，不崩。</summary>
        async Task LoadHeroImageAsync(string url)
        {
            try
            {
                var src = await RemoteImage.LoadAsync(url, 960);
                if (src != null && _heroImageUrl == url) NewsHeroImage.Source = src;
            }
            catch (Exception ex) { Log.Warn("加载新闻头图失败：" + ex.Message); }
        }

        void AddNewsItem(MinecraftNews.Item item)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = item.Date, Classes = { "sub" } });
            panel.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(item.Description))
                panel.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    Classes = { "sub" },
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                    MaxHeight = 36,
                });

            var button = new Button
            {
                Content = panel,
                Classes = { "ghost" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(0, 6),
                Background = Avalonia.Media.Brushes.Transparent,
            };
            var link = item.Link;
            button.Click += (s, e) => { if (!string.IsNullOrEmpty(link)) Util.OpenUrl(link); };
            NewsItems.Children.Add(button);
        }

        void NewsHero_Click(object sender, Avalonia.Input.PointerPressedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_heroLink)) Util.OpenUrl(_heroLink);
        }

        void NewsSource_Click(object sender, RoutedEventArgs e) => Util.OpenUrl(MinecraftNews.SourceUrl);

        void NewsRetry_Click(object sender, RoutedEventArgs e) => _ = LoadNewsAsync();
    }
}
