using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 主题色与图片加载（Avalonia 版）。
    /// 原来靠 WPF 的 Application.Resources + DynamicResource 即时换色，Avalonia 机制相同。
    /// </summary>
    public static class Theme
    {
        public static readonly (string name, string hex)[] Presets =
        {
            ("天空蓝", "#2F7BF5"), ("草方块绿", "#22A06B"), ("末影紫", "#7C5CFA"), ("岩浆橙", "#F08A24"),
            ("樱花粉", "#E5568B"), ("海晶青", "#0EA5B7"), ("红石红", "#D9483B"), ("黑曜石", "#3C4454"),
        };

        public static Color Parse(string hex, Color fallback)
        {
            try { return Color.Parse(hex); } catch { return fallback; }
        }

        public static void Apply(string hex)
        {
            var app = Application.Current;
            if (app == null) return;
            var c = Parse(hex, Color.FromRgb(0x2F, 0x7B, 0xF5));
            var r = app.Resources;
            r["AccentColor"] = c;
            r["Accent"] = Frozen(c);
            r["AccentHover"] = Frozen(Mix(c, Colors.White, 0.14));
            r["AccentPressed"] = Frozen(Mix(c, Colors.Black, 0.12));
            r["AccentSoft"] = Frozen(Mix(c, Colors.White, 0.9));
            r["AccentSoft2"] = Frozen(Mix(c, Colors.White, 0.78));
            var title = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(c, 0),
                    new GradientStop(Mix(c, Color.FromRgb(0x7C, 0x5C, 0xFA), 0.22), 1),
                },
            };
            r["TitleBrush"] = title;
            // 半透明标题栏文字在浅色主题下也要能看清
            r["TitleForeground"] = new SolidColorBrush(Colors.White);
        }

        static SolidColorBrush Frozen(Color c) => new SolidColorBrush(c);

        public static Color Mix(Color a, Color b, double t)
        {
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        /// <summary>读本地图片（不锁文件）</summary>
        public static Bitmap LoadImage(string path, int decodeWidth = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var fs = File.OpenRead(path))
                    return decodeWidth > 0 ? Bitmap.DecodeToWidth(fs, decodeWidth) : new Bitmap(fs);
            }
            catch { return null; }
        }

        public static Bitmap LoadImage(byte[] data, int decodeWidth = 0)
        {
            try
            {
                if (data == null || data.Length == 0) return null;
                using (var ms = new MemoryStream(data))
                    return decodeWidth > 0 ? Bitmap.DecodeToWidth(ms, decodeWidth) : new Bitmap(ms);
            }
            catch { return null; }
        }
    }
}
