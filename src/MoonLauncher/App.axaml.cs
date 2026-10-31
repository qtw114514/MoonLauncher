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

            TaskManager.Dispatcher = Dispatcher.UIThread;

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
                var win = new MainWindow();
                desktop.MainWindow = win;
                if (desktop.Args != null) MainWindow.StartupPages = desktop.Args;
                win.Show();
            }

            base.OnFrameworkInitializationCompleted();
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
