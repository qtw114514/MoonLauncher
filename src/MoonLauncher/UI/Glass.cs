using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 液态玻璃（Avalonia 版）。
    /// 原 WPF 实现用 DWM 的 SetWindowCompositionAttribute 开亚克力模糊，那是 Windows 专属；
    /// Avalonia 的跨平台做法是 TransparencyLevelHint，由渲染后端决定实际效果
    /// （Windows 走 DWM、macOS 走 NSVisualEffectView、Linux 通常只有 Transparent）。
    /// 半透明资源替换部分与原版一致，四个调试滑杆继续生效。
    /// </summary>
    public static class Glass
    {
        public static bool Active { get; private set; }

        static int A(string key, int def) => Math.Max(0, Math.Min(100, Settings.GetInt(key, def)));
        static byte Alpha(int pct) => (byte)Math.Round(pct * 255.0 / 100.0);

        public static void Apply(Window win, bool on)
        {
            Active = on;
            var app = Application.Current;
            if (app == null) return;
            var r = app.Resources;

            if (on)
            {
                if (win != null)
                {
                    try
                    {
                        // Transparent 一定有；AcrylicBlur / Mica 由后端决定是否支持
                        win.TransparencyLevelHint = new[]
                        {
                            WindowTransparencyLevel.AcrylicBlur,
                            WindowTransparencyLevel.Blur,
                            WindowTransparencyLevel.Transparent,
                        };
                    }
                    catch { }
                }

                r["Bg"] = Solid(Alpha(A("ui.glass.bg", 35)), 0xF2, 0xF5, 0xFA);
                var card = A("ui.glass.card", 65);
                r["Card"] = Solid(Alpha(card), 0xFF, 0xFF, 0xFF);
                r["CardHover"] = Solid(Alpha(Math.Min(100, card + 40)), 0xFF, 0xFF, 0xFF);
                r["Line"] = Solid(Alpha(A("ui.glass.edge", 90)), 0xD8, 0xDF, 0xEA);
                Theme.Apply(Settings.ThemeColor);
            }
            else
            {
                if (win != null)
                {
                    try { win.TransparencyLevelHint = new[] { WindowTransparencyLevel.None }; } catch { }
                }
                r["Bg"] = Solid(0xFF, 0xEE, 0xF2, 0xF8);
                r["Card"] = Solid(0xFF, 0xFF, 0xFF, 0xFF);
                r["CardHover"] = Solid(0xFF, 0xF7, 0xF9, 0xFC);
                r["Line"] = Solid(0xFF, 0xE3, 0xE8, 0xF0);
                Theme.Apply(Settings.ThemeColor);
            }
        }

        static SolidColorBrush Solid(byte a, byte r, byte g, byte b) =>
            new SolidColorBrush(Color.FromArgb(a, r, g, b));

        /// <summary>主题色变了但玻璃开关没变时，重刷标题栏渐变</summary>
        public static void RefreshTitle() => Theme.Apply(Settings.ThemeColor);
    }
}
