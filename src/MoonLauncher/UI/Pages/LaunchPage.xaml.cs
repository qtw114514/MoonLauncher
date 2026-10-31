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
            Settings.Changed += RefreshHomeCard;
            RefreshHomeCard();
            _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _logTimer.Tick += (s, e) => FlushLog();
            var h = DateTime.Now.Hour;
            Greeting.Text = h < 6 ? "夜深了，注意休息" : h < 12 ? "早上好" : h < 14 ? "中午好" : h < 18 ? "下午好" : "晚上好";
            // 圆角同心裁剪：卡片圆角取自 Radius.Card 令牌，头图不外溢。
            NewsCard.SizeChanged += (s, e) =>
            {
                double r = ((CornerRadius)FindResource("Radius.Card")).TopLeft;
                NewsCard.Clip = new RectangleGeometry(new Rect(NewsCard.RenderSize), r, r);
            };
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

            var acc = AccountStore.Selected;
            var inst = SelectedInstance();
            if (running)
            {
                StatusIcon.Text = "\uE73E";
                StatusTitle.Text = "游戏运行中";
                StatusDescription.Text = "运行日志已显示在下方，可打开目录或结束游戏。";
            }
            else if (starting)
            {
                StatusIcon.Text = "\uE916";
                StatusTitle.Text = "正在启动游戏";
                StatusDescription.Text = LaunchStep.Text;
            }
            else if (_lastLaunchError != null)
            {
                StatusIcon.Text = "\uE783";
                StatusTitle.Text = "上次启动失败";
                StatusDescription.Text = "点击左侧“重试启动”恢复；详细原因与修复操作见错误提示。";
            }
            else if (acc == null)
            {
                StatusIcon.Text = "\uE77B";
                StatusTitle.Text = "需要添加账户";
                StatusDescription.Text = "点击左侧“切换 / 管理账户”，支持离线、微软与外置账户。";
            }
            else if (inst == null)
            {
                StatusIcon.Text = "\uE7FC";
                StatusTitle.Text = "还没有可用实例";
                StatusDescription.Text = "点击左侧“切换”管理游戏目录，或前往下载页安装版本。";
            }
            else
            {
                StatusIcon.Text = "\uE73E";
                StatusTitle.Text = "一切准备就绪";
                StatusDescription.Text = $"{acc.Name} · {inst.Id}";
            }
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
            AccountType.Text = acc.TypeText + "账号";
            Avatar.Source = null;
            AvatarFallback.Visibility = Visibility.Visible;
            RefreshLaunchState();
            try
            {
                var head = await Skins.GetHeadAsync(acc, 64);
                if (head != null && AccountStore.Selected == acc) { Avatar.Source = head; AvatarFallback.Visibility = Visibility.Collapsed; }
            }
            catch (Exception ex) { Log.Warn("获取头像失败：" + ex.Message); }
        }

        void Accounts_Click(object sender, RoutedEventArgs e) => _ = AccountDialogs.ManageAsync();

        // ---------- 自定义主页 ----------
        string _homeNavigated;
        void RefreshHomeCard()
        {
            if (HomeCard == null) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshHomeCard); return; }
            var raw = (Settings.Get("home.url") ?? "").Trim();
            if (string.IsNullOrEmpty(raw))
            {
                HomeCard.Visibility = Visibility.Collapsed;
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
                HomeCard.Visibility = Visibility.Visible;
                HomeWeb.Visibility = Visibility.Collapsed;
                HomeHint.Visibility = Visibility.Visible;
                _homeNavigated = null;
                return;
            }
            HomeCard.Visibility = Visibility.Visible;
            HomeHint.Visibility = Visibility.Collapsed;
            HomeWeb.Visibility = Visibility.Visible;
            var key = uri.ToString();
            if (_homeNavigated != key) { _homeNavigated = key; try { HomeWeb.Navigate(uri); } catch { HomeHint.Visibility = Visibility.Visible; } }
        }

        void OpenHome_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_homeNavigated)) Util.OpenUrl(_homeNavigated);
        }

        void HomeWeb_LoadCompleted(object sender, System.Windows.Navigation.NavigationEventArgs e) => HomeHint.Visibility = Visibility.Collapsed;

        /// <summary>屏蔽 IE 脚本错误弹窗（WPF WebBrowser 没有公开的开关，反射设置 IWebBrowser2.Silent）</summary>
        void HomeWeb_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var fi = typeof(WebBrowser).GetField("_axIWebBrowser2", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var com = fi?.GetValue(HomeWeb);
                com?.GetType().InvokeMember("Silent", System.Reflection.BindingFlags.SetProperty, null, com, new object[] { true });
            }
            catch { }
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

        void RefreshCurrentInstance()
        {
            var inst = SelectedInstance();
            if (inst == null)
            {
                LaunchSub.Text = "还没有安装任何版本";
                InstanceName.Text = "尚未安装实例";
                InstanceDesc.Text = "去下载页安装一个版本即可开始。";
                InstanceCard.ToolTip = $"游戏目录：{GameRepo.Current.Root}";
                RefreshLaunchState();
                return;
            }
            LaunchSub.Text = inst.Id;
            InstanceName.Text = inst.Id;
            InstanceDesc.Text = (string.IsNullOrWhiteSpace(inst.Description) ? $"Minecraft {inst.McVersion}" : inst.Description) + (inst.Isolated ? " · 版本隔离" : "");
            InstanceCard.ToolTip = $"{inst.Id}\n{inst.Description}\n游戏目录：{inst.GameDir}";
            RefreshLaunchState();
        }

        // ---------- 实例列表（左栏，虚拟化+回收，只滚动自身） ----------
        bool _syncingList;

        public void RefreshInstances()
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)RefreshInstances); return; }
            var items = GameRepo.Current.ListInstances().Select(InstanceItem.From).ToList();
            var current = SelectedInstance();
            _syncingList = true;
            try
            {
                InstanceList.ItemsSource = items;
                InstanceEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                InstanceList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                InstanceList.SelectedItem = current == null ? null : items.FirstOrDefault(x => x.Id == current.Id);
            }
            finally { _syncingList = false; }
        }

        void InstanceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingList) return;
            if (!(InstanceList.SelectedItem is InstanceItem item) || !item.Instance.IsValid) return;
            Settings.SelectedVersion = item.Id;
            RefreshCurrentInstance(); // 只更新当前实例卡片与启动按钮，不重建列表（保留滚动位置）
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
            if (inst == null) { Dialogs.Toast("先选择或安装一个版本，再管理 Mod"); return; }
            MainWindow.Current.Push(new InstancePage(inst, "mods"));
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
            if (inst == null) { Dialogs.Toast("还没有可打开的实例目录，请先安装版本"); return; }
            Util.OpenFolder(inst.GameDir);
        }

        void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindow.Current.GoTab("download");

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
                    NewsItems.Visibility = Visibility.Collapsed;
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
                NewsItems.Children.Clear();
                if (result.FromCache)
                {
                    NewsItems.Children.Add(new TextBlock
                    {
                        Text = "暂时连不上官网，以下为本地缓存（1 小时内）内容。",
                        Style = (Style)FindResource("Sub")
                    });
                }

                // 首条新闻：顶部大图横幅（标题/日期/摘要压在深蓝黑渐变承托上），点击打开原文。
                var hero = result.Items[0];
                NewsHeroDate.Text = hero.Date;
                NewsHeroTitle.Text = hero.Title;
                NewsHeroDesc.Text = hero.Description;
                _heroLink = hero.Link;
                NewsHero.ToolTip = string.IsNullOrEmpty(hero.Link) ? null : hero.Link + "（点击打开原文）";
                NewsHero.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(hero.ImageUrl))
                {
                    _heroImageUrl = hero.ImageUrl;
                    _ = LoadHeroImageAsync(hero.ImageUrl);
                }

                // 其余新闻保留为下方列表。
                bool firstRow = true;
                foreach (var item in result.Items.Skip(1))
                {
                    if (!firstRow)
                        NewsItems.Children.Add(new Border { Height = 1, Background = (System.Windows.Media.Brush)FindResource("Line") });
                    AddNewsItem(item);
                    firstRow = false;
                }
                NewsItems.Visibility = Visibility.Visible;
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

        void AddNewsItem(MinecraftNews.Item item)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("Text"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Title
            });
            var meta = string.IsNullOrEmpty(item.Date) ? "阅读全文 →" : $"{item.Date} · 阅读全文 →";
            panel.Children.Add(new TextBlock { Text = meta, Style = (Style)FindResource("Sub"), Margin = new Thickness(0, 2, 0, 0) });
            if (!string.IsNullOrEmpty(item.Description))
                panel.Children.Add(new TextBlock
                {
                    Text = item.Description,
                    Style = (Style)FindResource("Sub"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = item.Description
                });
            var btn = new Button
            {
                Content = panel,
                Style = (Style)FindResource("Ghost"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = item.Link
            };
            btn.Click += (s, e) => Util.OpenUrl(item.Link);
            NewsItems.Children.Add(btn);
        }

        void NewsSource_Click(object sender, RoutedEventArgs e) => Util.OpenUrl(MinecraftNews.SourceUrl);
        void NewsRetry_Click(object sender, RoutedEventArgs e) => _ = LoadNewsAsync();

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
