using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using OpenLauncher.Core;
using OpenLauncher.Update;

namespace OpenLauncher.UI
{
    /// <summary>窗内对话框（不弹系统窗口），全部 async。</summary>
    public static class Dialogs
    {
        static Style S(string key) => (Style)Application.Current.FindResource(key);
        static Brush B(string key) => (Brush)Application.Current.FindResource(key);

        /// <summary>通用对话框：标题 + 任意内容 + 一排按钮。返回点击的按钮下标（Esc = -1）。</summary>
        public static Task<int> ShowAsync(string title, UIElement body, string[] buttons, int primary = 0, int danger = -1, double width = 440)
        {
            var tcs = new TaskCompletionSource<int>();
            var card = new Border
            {
                Background = B("Card"),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(24, 20, 24, 18),
                Width = width,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.25 },
            };
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = title, Style = S("H1"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
            if (body != null) root.Children.Add(body);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            void close(int idx)
            {
                MainWindow.Current.CloseDialogContent(card);
                tcs.TrySetResult(idx);
            }
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Content = buttons[i],
                    Margin = new Thickness(8, 0, 0, 0),
                    MinWidth = 76,
                    Style = i == danger ? S("DangerButton") : i == primary ? S("Primary") : S("BaseButton"),
                    IsDefault = i == primary,
                };
                btn.Click += (s, e) => close(idx);
                row.Children.Add(btn);
            }
            root.Children.Add(row);
            card.Child = root;
            card.KeyDown += (s, e) => { if (e.Key == Key.Escape) close(-1); };
            MainWindow.Current.ShowDialogContent(card);
            card.Focusable = true;
            card.Loaded += (s, e) => card.Focus();
            return tcs.Task;
        }

        public static TextBlock Text(string text) => new TextBlock { Text = text, Style = S("Body"), TextWrapping = TextWrapping.Wrap };

        public static Task Alert(string title, string text) => ShowAsync(title, Text(text), new[] { "知道了" });

        public static async Task<bool> Confirm(string title, string text, string ok = "确定", string cancel = "取消", bool danger = false)
            => await ShowAsync(title, Text(text), new[] { cancel, ok }, primary: 1, danger: danger ? 1 : -1) == 1;

        public static async Task<string> Prompt(string title, string label, string value = "", string placeholder = "")
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(label)) panel.Children.Add(new TextBlock { Text = label, Style = S("Body"), Margin = new Thickness(0, 0, 0, 8) });
            var box = new TextBox { Text = value, Tag = placeholder };
            panel.Children.Add(box);
            box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            var r = await ShowAsync(title, panel, new[] { "取消", "确定" }, primary: 1);
            return r == 1 ? box.Text.Trim() : null;
        }

        public static async Task<int> Choose(string title, string text, IList<string> options)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(text)) panel.Children.Add(new TextBlock { Text = text, Style = S("Body"), Margin = new Thickness(0, 0, 0, 10) });
            var list = new ListBox { Style = S("CardList"), MaxHeight = 320 };
            foreach (var o in options) list.Items.Add(new TextBlock { Text = o, TextWrapping = TextWrapping.Wrap });
            if (options.Count > 0) list.SelectedIndex = 0;
            panel.Children.Add(list);
            var r = await ShowAsync(title, panel, new[] { "取消", "确定" }, primary: 1);
            return r == 1 ? list.SelectedIndex : -1;
        }

        /// <summary>大段文字（日志、说明），可复制</summary>
        public static Task<int> Long(string title, string text, string[] buttons = null, double width = 620)
        {
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 320,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            return ShowAsync(title, box, buttons ?? new[] { "关闭" }, width: width);
        }

        public static void Toast(string text, MainWindow.ToastKind kind = MainWindow.ToastKind.Info) => MainWindow.Current?.Toast(text, kind);

        // ---------- 更新 ----------
        static bool IsWebUrl(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        static void AddMarkdownInlines(InlineCollection inlines, string text)
        {
            int plainStart = 0;
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '`')
                {
                    int end = text.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        if (i > plainStart) inlines.Add(new Run(text.Substring(plainStart, i - plainStart)));
                        inlines.Add(new Run(text.Substring(i + 1, end - i - 1))
                        {
                            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                            FontSize = 12,
                            Foreground = B("Text"),
                            Background = B("AccentSoft"),
                        });
                        i = plainStart = end + 1;
                        continue;
                    }
                }
                if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        if (i > plainStart) inlines.Add(new Run(text.Substring(plainStart, i - plainStart)));
                        var bold = new Bold();
                        AddMarkdownInlines(bold.Inlines, text.Substring(i + 2, end - i - 2));
                        inlines.Add(bold);
                        i = plainStart = end + 2;
                        continue;
                    }
                }
                if (text[i] == '[')
                {
                    int labelEnd = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                    int urlEnd = labelEnd < 0 ? -1 : text.IndexOf(')', labelEnd + 2);
                    if (labelEnd > i + 1 && urlEnd > labelEnd + 2)
                    {
                        string url = text.Substring(labelEnd + 2, urlEnd - labelEnd - 2);
                        if (IsWebUrl(url, out Uri uri))
                        {
                            if (i > plainStart) inlines.Add(new Run(text.Substring(plainStart, i - plainStart)));
                            var link = new Hyperlink(new Run(text.Substring(i + 1, labelEnd - i - 1)))
                            {
                                NavigateUri = uri,
                                Foreground = B("Accent"),
                                ToolTip = uri.AbsoluteUri,
                            };
                            link.RequestNavigate += (s, e) =>
                            {
                                e.Handled = true;
                                if (e.Uri != null && (e.Uri.Scheme == Uri.UriSchemeHttp || e.Uri.Scheme == Uri.UriSchemeHttps))
                                    Util.OpenUrl(e.Uri.AbsoluteUri);
                            };
                            inlines.Add(link);
                            i = plainStart = urlEnd + 1;
                            continue;
                        }
                    }
                }
                i++;
            }
            if (plainStart < text.Length) inlines.Add(new Run(text.Substring(plainStart)));
        }

        static FlowDocument MarkdownDocument(string markdown)
        {
            var document = new FlowDocument
            {
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 13,
                Foreground = B("Text2"),
                LineHeight = 20,
                PagePadding = new Thickness(12, 8, 12, 8),
            };
            var paragraphLines = new List<string>();
            System.Windows.Documents.List list = null;

            void FlushParagraph()
            {
                if (paragraphLines.Count == 0) return;
                var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 8) };
                AddMarkdownInlines(paragraph.Inlines, string.Join(" ", paragraphLines));
                document.Blocks.Add(paragraph);
                paragraphLines.Clear();
            }
            void FlushList()
            {
                if (list == null) return;
                document.Blocks.Add(list);
                list = null;
            }

            string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    FlushParagraph();
                    FlushList();
                    continue;
                }

                int heading = 0;
                while (heading < line.Length && heading < 6 && line[heading] == '#') heading++;
                if (heading > 0 && heading < line.Length && line[heading] == ' ')
                {
                    FlushParagraph();
                    FlushList();
                    double size = heading == 1 ? 22 : heading == 2 ? 18 : heading == 3 ? 16 : 14;
                    var paragraph = new Paragraph
                    {
                        FontSize = size,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = B("Text"),
                        LineHeight = size * 1.4,
                        Margin = new Thickness(0, document.Blocks.Count == 0 ? 2 : 10, 0, 7),
                        Padding = new Thickness(0, 0, 0, heading <= 2 ? 5 : 0),
                        BorderBrush = heading <= 2 ? B("Line") : null,
                        BorderThickness = new Thickness(0, 0, 0, heading <= 2 ? 1 : 0),
                    };
                    AddMarkdownInlines(paragraph.Inlines, line.Substring(heading + 1).Trim());
                    document.Blocks.Add(paragraph);
                    continue;
                }

                if (line.Length > 2 && (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
                {
                    FlushParagraph();
                    if (list == null) list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(20, 2, 0, 8), Padding = new Thickness(0) };
                    var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 3) };
                    AddMarkdownInlines(paragraph.Inlines, line.Substring(2).Trim());
                    list.ListItems.Add(new ListItem(paragraph));
                    continue;
                }

                FlushList();
                paragraphLines.Add(line);
            }
            FlushParagraph();
            FlushList();
            return document;
        }

        public static async Task ShowUpdateAsync(UpdateInfo info)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"当前 {AppInfo.VersionString}  →  新版本 {info.Version}", Style = S("Body"), FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"签名者 {info.Signer} · 来源 {new Uri(info.FoundAt).Host} · {Util.FormatSize(info.Size)}", Style = S("Sub"), Margin = new Thickness(0, 4, 0, 10) });
            if (!string.IsNullOrWhiteSpace(info.Notes))
            {
                var notes = new FlowDocumentScrollViewer
                {
                    Document = MarkdownDocument(info.Notes),
                    MaxHeight = 280,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                panel.Children.Add(new Border
                {
                    Child = notes,
                    BorderBrush = B("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                });
            }
            panel.Children.Add(new TextBlock { Text = "更新文件经过发布者签名校验，可以从任意镜像下载。", Style = S("Sub"), Margin = new Thickness(0, 10, 0, 0) });
            var r = await ShowAsync("发现新版本", panel, new[] { "以后再说", "立即更新" }, primary: 1, width: 560);
            if (r == 1)
            {
                TaskManager.Start($"更新到 {info.Version}", t => Updater.DownloadAndApplyAsync(info, t));
                MainWindow.Current.ToggleTasks(true);
            }
        }
    }
}
