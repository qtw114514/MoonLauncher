using System;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 主题色与图片加载（Avalonia 版）。
    /// 与 WPF 分支同一套算法：强调色在运行时写入 Application.Resources，
    /// 界面用 DynamicResource 引用，切换主题色立即生效。
    /// </summary>
    public static class Theme
    {
        /// <summary>默认强调色：清晰蓝。旧版本默认绿首次启动会一次性迁移到这里。</summary>
        public const string DefaultAccentHex = "#2F7BF5";
        static readonly Color DefaultAccent = Color.FromRgb(0x2F, 0x7B, 0xF5);
        /// <summary>旧版本默认绿；仅用于一次性迁移识别，不再是默认色。</summary>
        const string LegacyGreenHex = "#63B850";
        /// <summary>深海军蓝底：所有叠加色都以它为基底派生，保证深色主题不跑偏。</summary>
        static readonly Color DeepNavy = Color.FromRgb(0x0A, 0x15, 0x24);
        static bool _legacyChecked;

        public static readonly (string name, string hex)[] Presets =
        {
            ("默认蓝", DefaultAccentHex), ("草方块绿", LegacyGreenHex), ("末影紫", "#7C5CFA"), ("岩浆橙", "#F08A24"),
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

            hex = MigrateLegacyAccent(hex);
            var requested = Parse(hex, DefaultAccent);
            bool isDefault = requested == DefaultAccent;
            var accent = EnsureReadable(requested);
            var hover = EnsureReadable(isDefault ? Color.FromRgb(0x54, 0x93, 0xF7) : Mix(accent.Color, Colors.White, 0.18));
            var pressed = EnsureReadable(isDefault ? Color.FromRgb(0x25, 0x60, 0xBF) : Mix(accent.Color, Colors.Black, 0.22));
            var soft = Mix(accent.Color, DeepNavy, 0.82);
            var soft2 = Mix(accent.Color, DeepNavy, 0.68);

            SetColor("Accent", accent.Color);
            SetColor("AccentHover", hover.Color);
            SetColor("AccentPressed", pressed.Color);
            SetColor("AccentSoft", soft);
            SetColor("AccentSoft2", soft2);
            SetColor("OnAccent", accent.Foreground);
            SetColor("OnAccentHover", hover.Foreground);
            SetColor("OnAccentPressed", pressed.Foreground);

            var glow = Color.FromArgb(0x59, accent.Color.R, accent.Color.G, accent.Color.B);
            app.Resources["SelectionGlow"] = new SolidColorBrush(glow);

            // 顶栏仍以深海军蓝为主，只带少量强调色，避免整窗变成第二套亮色主题。
            app.Resources["TitleBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Mix(DeepNavy, accent.Color, 0.16), 0),
                    new GradientStop(Mix(DeepNavy, accent.Color, 0.05), 1),
                },
            };
        }

        /// <summary>
        /// 旧绿色配置 → 蓝白默认，仅迁移一次（用 ui.theme.migratedToBlue 标记）。
        /// 迁移之后用户若在设置里明确选回草方块绿，保持用户选择。
        /// </summary>
        static string MigrateLegacyAccent(string hex)
        {
            if (_legacyChecked) return hex;
            _legacyChecked = true;
            try
            {
                bool marked = Core.Settings.GetBool("ui.theme.migratedToBlue", false);
                if (!marked)
                {
                    Core.Settings.Set("ui.theme.migratedToBlue", true);
                    if (string.Equals((hex ?? "").Trim(), LegacyGreenHex, StringComparison.OrdinalIgnoreCase))
                    {
                        Core.Settings.ThemeColor = DefaultAccentHex;
                        return DefaultAccentHex;
                    }
                }
            }
            catch (Exception ex) { Core.Log.Warn("迁移旧主题色失败：" + ex.Message); }
            return hex;
        }

        static void SetColor(string name, Color color)
        {
            var resources = Application.Current.Resources;
            resources[name + "Color"] = color;
            resources[name] = new SolidColorBrush(color);
        }

        /// <summary>逐状态选择深海军蓝/白前景；中间亮度颜色会继续派生，直到达到 4.5:1。</summary>
        static (Color Color, Color Foreground) EnsureReadable(Color color)
        {
            const double minimum = 4.5;
            for (int i = 0; i < 24; i++)
            {
                double darkContrast = Contrast(color, DeepNavy);
                double lightContrast = Contrast(color, Colors.White);
                bool useDark = darkContrast >= lightContrast;
                if (Math.Max(darkContrast, lightContrast) >= minimum)
                    return (color, useDark ? DeepNavy : Colors.White);
                color = Mix(color, useDark ? Colors.White : Colors.Black, 0.06);
            }
            // 理论上不会到达；返回黑白端点之一也不留下低对比组合。
            return Contrast(color, DeepNavy) >= Contrast(color, Colors.White)
                ? (Colors.White, DeepNavy)
                : (Colors.Black, Colors.White);
        }

        static double Contrast(Color a, Color b)
        {
            double L(Color c)
            {
                double Channel(byte value)
                {
                    double v = value / 255.0;
                    return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
            }
            double la = L(a), lb = L(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

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
