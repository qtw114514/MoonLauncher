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
                if (head != null && AccountStore.Selected == acc) { Avatar.Source = head; AvatarFallback.IsVisible = false; }
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
                InstanceCard.IsVisible = true;
                InstanceDesc.Text = $"尚未安装版本\n游戏目录：{GameRepo.Current.Root}\n点击“浏览全部版本”开始安装。";
                RefreshLaunchState();
                return;
            }
            LaunchSub.Text = inst.Id;
            InstanceCard.IsVisible = true;
            InstanceDesc.Text = $"{inst.Id}\n{inst.Description}\n游戏目录：{inst.GameDir}" + (inst.Isolated ? "（已开启版本隔离）" : "");
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
                var list = await Task.Run(() => VersionList.GetMinecraftAsync());
                _latestLoaded = true;
                var rel = list.FirstOrDefault(v => v.Type == "release");
                var snap = list.FirstOrDefault(v => v.Type == "snapshot" && !v.IsAprilFools);
                var repo = GameRepo.Current;
                if (rel != null)
                {
                    LatestRelease.Content = repo.HasVersion(rel.Id) ? $"最新正式版 {rel.Id}（已安装）" : $"安装最新正式版 {rel.Id}";
                    LatestRelease.Tag = rel.Id;
                    LatestRelease.IsEnabled = !repo.HasVersion(rel.Id);
                    LatestRelease.IsVisible = true;
                }
                if (snap != null && list.IndexOf(snap) < list.IndexOf(rel))
                {
                    LatestSnapshot.Content = $"最新快照 {snap.Id}";
                    LatestSnapshot.Tag = snap.Id;
                    LatestSnapshot.IsEnabled = !repo.HasVersion(snap.Id);
                    LatestSnapshot.IsVisible = true;
                }
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

        public async Task LaunchAsync(Instance inst = null)
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
                    try { tcs.TrySetResult(await LaunchService.LaunchAsync(inst, acc, t)); }
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
                        afterError = async () => { if (await AccountDialogs.ReloginAsync(acc)) await LaunchAsync(inst); };
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
                    afterError = () => LaunchAsync(inst);
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
    }
}
