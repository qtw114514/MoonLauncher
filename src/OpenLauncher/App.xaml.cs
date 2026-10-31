using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OpenLauncher.Accounts;
using OpenLauncher.Core;
using OpenLauncher.Multiplayer;
using OpenLauncher.UI;
using OpenLauncher.Update;

namespace OpenLauncher
{
    public partial class App : Application
    {
        static App()
        {
            // 与 App.config 相同的长路径开关；写在代码里，单独拷走 exe 也生效
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Error("未处理的异常", ex.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, ex) => { Log.Warn("未观察的任务异常：" + ex.Exception?.GetBaseException().Message); ex.SetObserved(); };

            TaskManager.Dispatcher = Dispatcher;
            Log.Info($"{Branding.AppName} {AppInfo.VersionString} 启动，系统 {Util.OsVersion} {Util.OsArch}，数据目录 {Paths.DataDir}");
            Updater.CleanupOld();
            Task.Run(() => Paths.CleanTemp()); // 清理上次运行残留的临时文件，不阻塞启动
            Settings.Load();
            // 仅迁移“从未设置过主题色”的旧配置；用户明确选择的自定义颜色保持不变。
            if (Settings.GetNode("ui.theme").IsNull) Settings.ThemeColor = Theme.DefaultAccentHex;
            try { AccountStore.Load(); } catch (Exception ex) { Log.Error("读取账号失败", ex); }
            Theme.Apply(Settings.ThemeColor);
#if DEBUG
            DemoCapture.Init(e.Args);
#endif

            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 有界等待和平清理，确保 /panic 在进程退出前真正发出；本地 HTTP 异常最多阻塞 2 秒。
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { TerracottaService.ShutdownAsync(timeout.Token).GetAwaiter().GetResult(); }
                catch (Exception ex) { Log.Warn("退出时清理 Terracotta 失败：" + TaskManager.Flatten(ex)); }
            }
            Settings.SaveNow();
            base.OnExit(e);
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面异常", e.Exception);
            e.Handled = true;
            try { Dialogs.Alert("出错了", e.Exception.GetBaseException().Message + "\n\n详细信息已写入启动器日志。"); }
            catch { MessageBox.Show(e.Exception.ToString(), Branding.AppName); }
        }
    }
}
