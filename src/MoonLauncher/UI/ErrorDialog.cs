using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    /// <summary>错误对话框中的一个可配置操作。操作会在对话框关闭后执行。</summary>
    public class ErrorDialogAction
    {
        public string Text { get; }
        public Func<Task> Action { get; }
        public bool Primary { get; }

        public ErrorDialogAction(string text, Func<Task> action, bool primary = false)
        {
            Text = text;
            Action = action;
            Primary = primary;
        }

        public ErrorDialogAction(string text, Action action, bool primary = false)
            : this(text, () => { action(); return Task.CompletedTask; }, primary) { }
    }

    /// <summary>轻量结构化错误：原因摘要、推荐操作、可复制的技术详情。</summary>
    public static class ErrorDialog
    {
        static string S(string key) => key.ToLowerInvariant();

        public static Task ShowAsync(string title, string summary, string recommendation, Exception error,
            IEnumerable<ErrorDialogAction> actions = null)
            => ShowAsync(title, summary, recommendation, error?.ToString(), actions);

        public static async Task ShowAsync(string title, string summary, string recommendation, string technicalDetails,
            IEnumerable<ErrorDialogAction> actions = null)
        {
            summary = string.IsNullOrWhiteSpace(summary) ? "操作未能完成。" : summary.Trim();
            technicalDetails = string.IsNullOrWhiteSpace(technicalDetails) ? summary : technicalDetails;
            var actionList = actions?.Where(a => a != null && a.Action != null && !string.IsNullOrWhiteSpace(a.Text)).ToList()
                             ?? new List<ErrorDialogAction>();

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "原因摘要", Classes = { S("Sub") }, FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = summary, Classes = { S("Body") }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) });
            if (!string.IsNullOrWhiteSpace(recommendation))
            {
                panel.Children.Add(new TextBlock { Text = "推荐操作", Classes = { S("Sub") }, FontWeight = FontWeight.SemiBold });
                panel.Children.Add(new TextBlock { Text = recommendation, Classes = { S("Body") }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 10) });
            }

            var detailBox = new TextBox
            {
                Text = technicalDetails,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 72,
                MaxHeight = 180,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            panel.Children.Add(new Expander { Header = "技术详情（可复制）", Content = detailBox, IsExpanded = false });

            var buttons = new List<string> { "关闭", "复制详情" };
            buttons.AddRange(actionList.Select(a => a.Text));
            int primary = actionList.FindIndex(a => a.Primary);
            primary = primary >= 0 ? primary + 2 : 0;
            var choice = await Dialogs.ShowAsync(title,
                new ScrollViewer { Content = panel, MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                buttons.ToArray(), primary: primary, width: 560);
            if (choice == 1)
            {
                try { _ = MainWindow.Current?.Clipboard?.SetTextAsync(technicalDetails); Dialogs.Toast("技术详情已复制", MainWindow.ToastKind.Ok); }
                catch (Exception ex) { Dialogs.Toast("复制失败：" + ex.Message, MainWindow.ToastKind.Error); }
                return;
            }
            if (choice < 2 || choice - 2 >= actionList.Count) return;
            try { await actionList[choice - 2].Action(); }
            catch (Exception ex)
            {
                await ShowAsync("操作失败", TaskManager.Flatten(ex), "请检查当前状态后重试。", ex);
            }
        }
    }
}
