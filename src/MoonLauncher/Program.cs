using System;
using Avalonia;

namespace MoonLauncher
{
    /// <summary>
    /// 入口。Avalonia 取代了 WPF 的 App.xaml 启动模型：
    /// Program.Main → BuildAvaloniaApp → App（App.axaml.cs）。
    /// .NET 8 后默认开启长路径，不再需要 App.config 里的 UseLegacyPathHandling 开关。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        // Avalonia 设计器 / 诊断工具也走这里
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
