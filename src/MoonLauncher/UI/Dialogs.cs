using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MoonLauncher.Core;
using MoonLauncher.Update;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 窗内对话框（Avalonia 版，不弹系统窗口），全部 async。
    /// 接口与原 WPF 版保持一致（Alert / Confirm / Prompt / Long / Toast / ShowUpdateAsync），
    /// 页面的 code-behind 因此可以原样迁移。
    /// </summary>
    public static class Dialogs
    {
        static IBrush B(string key) =>
            Application.Current?.FindResource(key) as IBrush ?? Brushes.White;

        /// <summary>通用对话框：标题 + 任意内容 + 一排按钮。返回点击的按钮下标（关掉返回 -1）。</summary>
        public static Task<int> ShowAsync(string title, Control body, string[] buttons, int primary = 0, int danger = -1, double width = 440)
        {
            var tcs = new TaskCompletionSource<int>();
            var card = new Border
            {
                Background = B("Card"),
                BorderBrush = B("Line"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24, 20, 24, 18),
                Width = width,
            };
            var root = new StackPanel { Spacing = 10 };
            root.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (body != null) root.Children.Add(body);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var btn = new Button { Content = buttons[i], MinWidth = 88 };
                if (i == primary) btn.Classes.Add("primary");
                if (i == danger) btn.Classes.Add("dangerbutton");
                btn.Click += (s, e) =>
                {
                    MainWindow.Current?.CloseDialogContent(card);
                    tcs.TrySetResult(idx);
                };
                row.Children.Add(btn);
            }
            root.Children.Add(row);
            card.Child = root;
            MainWindow.Current?.ShowDialogContent(card);
            return tcs.Task;
        }

        /// <summary>提示框（只有一个“知道了”）</summary>
        public static async Task Alert(string title, string message)
        {
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            await ShowAsync(title, text, new[] { "知道了" }, primary: 0, width: 420);
        }

        /// <summary>确认框，返回是否点了确定</summary>
        public static async Task<bool> Confirm(string title, string message, string ok = "确定", string cancel = "取消", bool danger = false)
        {
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            var r = await ShowAsync(title, text, new[] { cancel, ok }, primary: 1, danger: danger ? 1 : -1, width: 420);
            return r == 1;
        }

        /// <summary>输入一行文字</summary>
        public static async Task<string> Prompt(string title, string label, string initial = "", string watermark = "")
        {
            var box = new TextBox { Text = initial ?? "", Watermark = watermark, MinWidth = 320 };
            var panel = new StackPanel { Spacing = 6 };
            if (!string.IsNullOrEmpty(label))
                panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 });
            panel.Children.Add(box);
            var r = await ShowAsync(title, panel, new[] { "取消", "确定" }, primary: 1, width: 460);
            return r == 1 ? box.Text : null;
        }

        /// <summary>长文本对话框（日志、崩溃分析结果）。返回点击的按钮下标。</summary>
        public static Task<int> Long(string title, string text, string[] buttons = null)
        {
            var box = new TextBox
            {
                Text = text ?? "",
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 12,
                MinHeight = 300,
            };
            var scroll = new ScrollViewer
            {
                Content = box,
                Height = 340,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            var buttonsFinal = buttons ?? new[] { "关闭" };
            return ShowAsync(title, scroll, buttonsFinal, primary: 0, danger: -1, width: 740);
        }

        /// <summary>底部提示（转发到主窗口）</summary>
        public static void Toast(string text, MainWindow.ToastKind kind = MainWindow.ToastKind.Info) =>
            MainWindow.Current?.Toast(text, kind);

        /// <summary>发现新版本的提示（签名清单校验通过后调用）</summary>
        public static async Task ShowUpdateAsync(UpdateInfo info)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"当前版本 {AppInfo.VersionString} → 新版本 {info.Version}",
                FontSize = 13,
            });
            if (!string.IsNullOrWhiteSpace(info.Notes))
                panel.Children.Add(new TextBlock
                {
                    Text = info.Notes,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    MaxHeight = 260,
                });
            panel.Children.Add(new TextBlock
            {
                Text = "更新文件经过发布者签名校验，可以从任意镜像下载。",
                FontSize = 12,
                Opacity = 0.65,
            });

            var r = await ShowAsync("发现新版本", panel, new[] { "以后再说", "立即更新" }, primary: 1, width: 560);
            if (r == 1)
            {
                TaskManager.Start($"更新到 {info.Version}", t => Updater.DownloadAndApplyAsync(info, t));
                MainWindow.Current?.ToggleTasks(true);
            }
        }

        /// <summary>正文段落（原 WPF 版靠 Style 资源，这里挂 class）</summary>
        public static TextBlock Text(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22 };
            t.Classes.Add("body");
            return t;
        }

        /// <summary>从若干选项里选一个，返回下标（取消返回 -1）</summary>
        public static async Task<int> Choose(string title, string text, System.Collections.Generic.IList<string> options)
        {
            var panel = new StackPanel { Spacing = 8 };
            if (!string.IsNullOrEmpty(text)) panel.Children.Add(Text(text));
            var list = new ListBox { MaxHeight = 320 };
            list.Classes.Add("cardlist");
            foreach (var o in options) list.Items.Add(new TextBlock { Text = o, TextWrapping = TextWrapping.Wrap });
            if (options.Count > 0) list.SelectedIndex = 0;
            panel.Children.Add(list);
            var r = await ShowAsync(title, panel, new[] { "取消", "确定" }, primary: 1);
            return r == 1 ? list.SelectedIndex : -1;
        }
    }
}
