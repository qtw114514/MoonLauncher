using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    public static class Theme
    {
        /// <summary>默认强调色：月光淡紫蓝。旧版本默认绿首次启动会一次性迁移到这里。</summary>
        public const string DefaultAccentHex = "#93A7FF";
        static readonly Color DefaultAccent = Color.FromRgb(0x93, 0xA7, 0xFF);
        /// <summary>旧版本默认色；仅用于一次性迁移识别，不再是默认色。</summary>
        const string LegacyGreenHex = "#63B850";
        const string LegacyBlueHex = "#2F7BF5";
        static readonly Color DeepNavy = Color.FromRgb(0x08, 0x09, 0x0D);
        static bool _legacyChecked;

        public static readonly (string name, string hex)[] Presets =
        {
            ("月光紫", DefaultAccentHex), ("草方块绿", LegacyGreenHex), ("末影紫", "#7C5CFA"), ("岩浆橙", "#F08A24"),
            ("樱花粉", "#E5568B"), ("海晶青", "#0EA5B7"), ("红石红", "#D9483B"), ("黑曜石", "#3C4454"),
        };

        public static Color Parse(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
        }

        public static void Apply(string hex)
        {
            hex = MigrateLegacyAccent(hex);
            var requested = Parse(hex, DefaultAccent);
            bool isDefault = requested == DefaultAccent;
            var accent = EnsureReadable(requested);
            var hover = EnsureReadable(isDefault ? Color.FromRgb(0xAA, 0xB8, 0xFF) : Mix(accent.Color, Colors.White, 0.18));
            var pressed = EnsureReadable(isDefault ? Color.FromRgb(0x6A, 0x79, 0xE6) : Mix(accent.Color, Colors.Black, 0.22));
            var soft = Color.FromArgb(0x24, accent.Color.R, accent.Color.G, accent.Color.B);
            var soft2 = Color.FromArgb(0x38, accent.Color.R, accent.Color.G, accent.Color.B);

            SetColor("Accent", accent.Color);
            SetColor("AccentHover", hover.Color);
            SetColor("AccentPressed", pressed.Color);
            SetColor("AccentSoft", soft);
            SetColor("AccentSoft2", soft2);
            SetColor("OnAccent", accent.Foreground);
            SetColor("OnAccentHover", hover.Foreground);
            SetColor("OnAccentPressed", pressed.Foreground);

            var glow = Color.FromArgb(0x42, accent.Color.R, accent.Color.G, accent.Color.B);
            Application.Current.Resources["SelectionGlowColor"] = glow;
            Application.Current.Resources["Color.SelectionGlow"] = glow;

            // 顶栏保持近黑半透明，只带极少量强调色。
            var title = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            title.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0x0A, 0x0C, 0x12), 0));
            title.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0x08, 0x09, 0x0D), 1));
            title.Freeze();
            Application.Current.Resources["TitleBrush"] = title;
            ApplyBrightness(Settings.LightTheme);
        }

        public static void ToggleBrightness()
        {
            Settings.LightTheme = !Settings.LightTheme;
            Apply(Settings.ThemeColor);
        }

        public static void ApplyBrightness(bool light)
        {
            if (light)
            {
                SetColor("Background", Color.FromRgb(0xF4, 0xF6, 0xFB));
                SetColor("Card", Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF));
                SetColor("CardHover", Color.FromRgb(0xFF, 0xFF, 0xFF));
                SetColor("SurfaceElevated", Colors.White);
                SetColor("DialogSurface", Color.FromRgb(0xFF, 0xFF, 0xFF));
                SetColor("Scrim", Color.FromArgb(0x66, 0x08, 0x09, 0x0D));
                SetColor("Line", Color.FromArgb(0x14, 0x0A, 0x0E, 0x1A));
                SetColor("LineStrong", Color.FromArgb(0x29, 0x0A, 0x0E, 0x1A));
                SetColor("ControlBorder", Color.FromArgb(0x24, 0x0A, 0x0E, 0x1A));
                SetColor("Text", Color.FromRgb(0x17, 0x1C, 0x29));
                SetColor("Text2", Color.FromRgb(0x46, 0x51, 0x67));
                SetColor("Text3", Color.FromRgb(0x68, 0x74, 0x8A));
                SetColor("TextDisabled", Color.FromRgb(0x8A, 0x94, 0xA6));
                SetColor("ScrollThumb", Color.FromArgb(0x33, 0x0A, 0x0E, 0x1A));
                SetColor("ScrollThumbHover", Color.FromArgb(0x4D, 0x0A, 0x0E, 0x1A));
                Application.Current.Resources["TitleBrush"] = new SolidColorBrush(Color.FromRgb(0xF7, 0xF8, 0xFC));
                return;
            }
            SetColor("Background", DeepNavy);
            SetColor("Card", Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF));
            SetColor("CardHover", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            SetColor("SurfaceElevated", Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
            SetColor("DialogSurface", Color.FromRgb(0x14, 0x17, 0x20));
            SetColor("Scrim", Color.FromArgb(0xB3, 0x08, 0x09, 0x0D));
            SetColor("Line", Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
            SetColor("LineStrong", Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            SetColor("ControlBorder", Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
            SetColor("Text", Color.FromRgb(0xE9, 0xED, 0xF6));
            SetColor("Text2", Color.FromRgb(0xB7, 0xC0, 0xD4));
            SetColor("Text3", Color.FromRgb(0x79, 0x83, 0x9A));
            SetColor("TextDisabled", Color.FromRgb(0x61, 0x6B, 0x80));
            SetColor("ScrollThumb", Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            SetColor("ScrollThumbHover", Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>
        /// 旧默认绿色 / 蓝色 → 月光默认，各自只迁移一次；用户明确选择的其他颜色保持不变。
        /// </summary>
        static string MigrateLegacyAccent(string hex)
        {
            if (_legacyChecked) return hex;
            _legacyChecked = true;
            try
            {
                string value = (hex ?? "").Trim();
                bool marked = Settings.GetBool("ui.theme.migratedToBlue", false);
                if (!marked)
                {
                    Settings.Set("ui.theme.migratedToBlue", true);
                    if (string.Equals(value, LegacyGreenHex, StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.ThemeColor = DefaultAccentHex;
                        return DefaultAccentHex;
                    }
                }
                bool moonlightMarked = Settings.GetBool("ui.theme.migratedToMoonlight", false);
                if (!moonlightMarked)
                {
                    Settings.Set("ui.theme.migratedToMoonlight", true);
                    if (string.Equals(value, LegacyBlueHex, StringComparison.OrdinalIgnoreCase))
                    {
                        Settings.ThemeColor = DefaultAccentHex;
                        return DefaultAccentHex;
                    }
                }
            }
            catch (Exception ex) { Log.Warn("迁移旧主题色失败：" + ex.Message); }
            return hex;
        }

        static void SetColor(string name, Color color)
        {
            var resources = Application.Current.Resources;
            resources[name + "Color"] = color;
            string semantic = name == "Card" ? "Surface" : name == "CardHover" ? "SurfaceHover" : name;
            resources["Color." + semantic] = color;
            string brushKey = name == "Background" ? "Bg" : name;
            if (resources.Contains(brushKey))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                resources[brushKey] = brush;
                resources["Brush." + semantic] = brush;
            }
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

        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public static Color Mix(Color a, Color b, double t)
        {
            byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
        }

        /// <summary>读本地图片为冻结的 BitmapImage（不锁文件）</summary>
        public static BitmapImage LoadImage(string path, int decodeWidth = 0)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                using (var fs = File.OpenRead(path)) { bmp.StreamSource = fs; bmp.EndInit(); }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public static BitmapImage LoadImage(byte[] data, int decodeWidth = 0)
        {
            try
            {
                if (data == null || data.Length == 0) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
