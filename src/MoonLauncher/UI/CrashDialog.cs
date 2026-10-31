using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MoonLauncher.Content;
using MoonLauncher.Core;
using MoonLauncher.Game;
using MoonLauncher.UI.Pages;

namespace MoonLauncher.UI
{
    public static class CrashDialog
    {
        public static async Task ShowAsync(GameSession s, int code)
        {
            CrashResult r;
            try { r = await Task.Run(() => CrashAnalyzer.Analyze(s.Instance, s.Output, code, s.StartTime, s.Java)); }
            catch (Exception ex)
            {
                Log.Error("崩溃分析失败", ex);
                r = new CrashResult();
                r.Reasons.Add("崩溃分析本身出错了：" + ex.Message);
            }
            if (r.IsUserTriggered) { Dialogs.Toast("检测到手动触发的调试崩溃（F3+C），不是错误"); return; }

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = s.WindowShown ? $"游戏运行中崩溃了（退出码 {code}）" : $"游戏没能启动起来（退出码 {code}）",
                Style = (Style)Application.Current.FindResource("Sub"),
                Margin = new Thickness(0, 0, 0, 10)
            });
            void section(string title, IEnumerable<string> items, Brush color)
            {
                var list = items?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                if (list == null || list.Count == 0) return;
                panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Foreground = color, Margin = new Thickness(0, 6, 0, 4) });
                foreach (var it in list)
                    panel.Children.Add(new TextBlock { Text = "• " + it, Style = (Style)Application.Current.FindResource("Body"), Margin = new Thickness(4, 0, 0, 2) });
            }
            section("可能的原因", r.Reasons, (Brush)Application.Current.FindResource("Danger"));
            section("建议这样做", r.Suggestions, (Brush)Application.Current.FindResource("Accent"));
            section("可疑的 Mod", r.SuspectMods, (Brush)Application.Current.FindResource("Warn"));

            var buttons = new List<string> { "关闭", "查看日志", "导出错误报告" };
            var operations = new List<Func<Task>> { null, null, null };
            void addAction(string text, Action action)
            {
                buttons.Add(text);
                operations.Add(() => { action(); return Task.CompletedTask; });
            }
            var analysis = string.Join("\n", r.Reasons.Concat(r.Suggestions));
            if (analysis.IndexOf("内存", StringComparison.Ordinal) >= 0 || analysis.IndexOf("-Xm", StringComparison.OrdinalIgnoreCase) >= 0)
                addAction("内存设置", () => MainWindow.Current.GoTab("launch", new InstancePage(s.Instance, "settings")));

            bool searchMods = r.Suggestions.Any(x => x.Contains("下载") || x.Contains("安装") || x.Contains("前置") || x.Contains("更新") || x.Contains("改用"));
            bool manageMods = r.SuspectMods.Count > 0 || analysis.IndexOf("Mod", StringComparison.OrdinalIgnoreCase) >= 0;
            if (searchMods)
                addAction("搜索 Mod", () =>
                {
                    MainWindow.Current.GoTab("download");
                    MainWindow.Current.DownloadPage.OpenContent(ContentType.Mod, s.Instance);
                });
            else if (manageMods)
                addAction("管理 Mod", () => MainWindow.Current.GoTab("launch", new InstancePage(s.Instance, "mods")));

            buttons.Add("重试启动");
            operations.Add(() => MainWindow.Current.LaunchPage.LaunchAsync(s.Instance));
            var choice = await Dialogs.ShowAsync("游戏崩溃了", new ScrollViewer { Content = panel, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                buttons.ToArray(), primary: buttons.Count - 1, width: 640);
            if (choice == 1)
            {
                var text = string.IsNullOrWhiteSpace(r.KeyLines) ? s.Output : r.KeyLines + "\n\n========== 完整输出 ==========\n" + s.Output;
                await Dialogs.Long("游戏日志", text.Length > 150_000 ? text.Substring(text.Length - 150_000) : text);
            }
            else if (choice == 2)
            {
                try
                {
                    var path = CrashAnalyzer.ExportReport(s.Instance, s.Output, r, s.Java);
                    Dialogs.Toast("错误报告已保存到桌面", MainWindow.ToastKind.Ok);
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception ex)
                {
                    await ErrorDialog.ShowAsync("导出失败", TaskManager.Flatten(ex), "检查桌面或日志目录是否可写后重试。", ex);
                }
            }
            else if (choice >= 3 && choice < operations.Count) await operations[choice]();
        }
    }
}
