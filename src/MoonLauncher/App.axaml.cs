using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MoonLauncher.Accounts;
using MoonLauncher.Core;
using MoonLauncher.UI;
using MoonLauncher.Update;

namespace MoonLauncher
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // 未处理异常：记日志 + 提示，不让界面线程直接崩掉
            Dispatcher.UIThread.UnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Error("未处理的异常", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Warn("未观察的任务异常：" + ex.Exception?.GetBaseException().Message); ex.SetObserved(); };

            AvaloniaHost.Attach();

            Log.Info($"{Branding.AppName} {AppInfo.VersionString} 启动，系统 {Platform.OsDescription}，数据目录 {Paths.DataDir}");
            Updater.CleanupOld();
            _ = Task.Run(() => Paths.CleanTemp()); // 清理上次运行残留的临时文件，不阻塞启动
            Settings.Load();
            try { AccountStore.Load(); } catch (Exception ex) { Log.Error("读取账号失败", ex); }
            Theme.Apply(Settings.ThemeColor);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.Exit += (s, e) => Settings.SaveNow();
                // 自更新装好后要求退出当前进程，由界面层决定怎么退
                Updater.ExitRequested += () => desktop.Shutdown();

                // --selftest：逐个实例化所有页面，确认 XAML 能在运行时加载。
                // 用于在无窗口环境（CI、发布前）发现绑定/资源/模版错误。
                if (desktop.Args != null && Array.IndexOf(desktop.Args, "--selftest") >= 0)
                {
                    int failed = SelfTest();
                    Console.Out.Flush();
                    // 不用 desktop.Shutdown()：它会在 Start 的 MainLoop 里抛 Dispatcher 已关闭
                    Environment.Exit(failed);
                    return;
                }

                var win = new MainWindow();
                desktop.MainWindow = win;
                if (desktop.Args != null) MainWindow.StartupPages = desktop.Args;
                win.Show();
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// 逐个构造页面，捕获 XAML 加载异常。返回失败个数（供退出码使用）。
        /// 参数依赖的页面用 null 试构造：构造函数不应在参数为空时就崩。
        /// </summary>
        static int SelfTest()
        {
            var cases = new (string name, Func<object> make)[]
            {
                ("MainWindow", () => new MainWindow()),
                ("LaunchPage", () => new UI.Pages.LaunchPage()),
                ("DownloadPage", () => new UI.Pages.DownloadPage()),
                ("MultiplayerPage", () => new UI.Pages.MultiplayerPage()),
                ("SettingsPage", () => new UI.Pages.SettingsPage()),
                ("MorePage", () => new UI.Pages.MorePage()),
                ("VersionsPage", () => new UI.Pages.VersionsPage()),
                ("TasksPage", () => new UI.Pages.TasksPage()),
                ("GameListView", () => new UI.Pages.GameListView()),
                // 下面几个页面依赖真实数据；给最小桩对象，目的只是验证 XAML 能加载
                ("InstancePage", () => new UI.Pages.InstancePage(new Game.Instance(Game.GameRepo.Current, "selftest"))),
                ("GameInstallPage", () => new UI.Pages.GameInstallPage(new Game.McVersionEntry { Id = "selftest", Type = "release" })),
                ("ProjectPage", () => new UI.Pages.ProjectPage(new Content.ContentProject { Id = "selftest", Title = "selftest" }, Content.ContentType.Mod, null, null, null)),
                ("ContentSearchView(Mod)", () => new UI.Pages.ContentSearchView(Content.ContentType.Mod)),
                ("ContentSearchView(Modpack)", () => new UI.Pages.ContentSearchView(Content.ContentType.Modpack)),
            };

            int failed = 0;
            foreach (var (name, make) in cases)
            {
                try
                {
                    var page = make();
                    Console.WriteLine($"PASS {name}");
                    (page as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Console.WriteLine(failed == 0 ? "全部页面加载成功" : $"{failed} 个页面加载失败");
            return failed;
        }

        // 退出时落盘设置：Avalonia 的 Application 没有 OnExit 可重写，改在生命周期 Exit 事件里做

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面异常", e.Exception);
            e.Handled = true;
            try { _ = Dialogs.Alert("出错了", e.Exception.GetBaseException().Message + "\n\n详细信息已写入启动器日志。"); }
            catch { }
        }
    }
}
