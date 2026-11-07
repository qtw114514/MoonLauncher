using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 月光深色 Liquid Glass：窗口级 DWM 模糊、半透明表面、低调渐变描边。
    /// 控件卡片不做独立实时模糊；DWM 不可用时仍保留不透明的近黑内容层。
    /// </summary>
    public static class Glass
    {
        public static bool Active { get; private set; }

        static int A(string key, int def) => Math.Max(0, Math.Min(100, Settings.GetInt(key, def)));
        static byte Alpha(int pct) => (byte)Math.Round(pct * 255.0 / 100.0);
        static bool Acrylic => Settings.GetBool("ui.glass.acrylic", true);

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WcaData data);

        [StructLayout(LayoutKind.Sequential)]
        struct WcaData
        {
            public int Attribute;
            public IntPtr Data;
            public int Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        const int WcaAccentPolicy = 19;
        const int AccentEnableAcrylicBlurBehind = 4;
        const int AccentEnableBlurBehind = 3;
        const int AccentDisabled = 0;
        const int AccentFlagsDrawLeftBorder = 0x20;

        /// <summary>开启 / 关闭。窗口句柄还没准备好时会等 SourceInitialized 再应用模糊。</summary>
        public static void Apply(Window win, bool on)
        {
            Active = on;
            bool light = Settings.LightTheme;
            if (on)
            {
                SetBaseColor("Background", light
                    ? Color.FromArgb(Alpha(A("ui.glass.bg", 90)), 0xF4, 0xF6, 0xFB)
                    : Color.FromArgb(Alpha(A("ui.glass.bg", 90)), 0x08, 0x09, 0x0D));
                int card = A("ui.glass.card", 4);
                SetBaseColor("Card", Color.FromArgb(Alpha(card), 0xFF, 0xFF, 0xFF));
                SetBaseColor("CardHover", Color.FromArgb(Alpha(Math.Min(100, card + 4)), 0xFF, 0xFF, 0xFF));
                SetBaseColor("Line", light
                    ? Color.FromArgb(Alpha(A("ui.glass.edge", 8)), 0x0A, 0x0E, 0x1A)
                    : Color.FromArgb(Alpha(A("ui.glass.edge", 6)), 0xFF, 0xFF, 0xFF));
                var edge = GlassEdge(A("ui.glass.edge", 9));
                Application.Current.Resources["BorderGradient"] = edge;
                Application.Current.Resources["Brush.BorderGradient"] = edge;
                Application.Current.Resources["TitleBrush"] = TitleGlass();
            }
            else
            {
                SetBaseColor("Background", Color.FromRgb(0x08, 0x09, 0x0D));
                SetBaseColor("Card", Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF));
                SetBaseColor("CardHover", Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                SetBaseColor("Line", Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF));
                var edge = GlassEdge(9);
                Application.Current.Resources["BorderGradient"] = edge;
                Application.Current.Resources["Brush.BorderGradient"] = edge;
                Theme.Apply(Settings.ThemeColor);
            }

            var mw = win as MainWindow;
            if (mw != null) mw.BgImage.Opacity = on ? A("ui.glass.img", 18) / 100.0 : 0.12;

            var helper = new WindowInteropHelper(win);
            if (helper.Handle == IntPtr.Zero)
                win.SourceInitialized += (s, e) => SetBlur(new WindowInteropHelper(win).Handle, on);
            else SetBlur(helper.Handle, on);
        }

        static void SetBaseColor(string name, Color color)
        {
            var resources = Application.Current.Resources;
            resources[name + "Color"] = color;
            string semantic = name == "Background" ? "Background" : name == "Card" ? "Surface" : name == "CardHover" ? "SurfaceHover" : "Line";
            resources["Color." + semantic] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[name == "Background" ? "Bg" : name] = brush;
            resources["Brush." + semantic] = brush;
        }

        public static void RefreshTitle() { if (Active) Application.Current.Resources["TitleBrush"] = TitleGlass(); }

        static void SetBlur(IntPtr hwnd, bool on)
        {
            try
            {
                if (on)
                {
                    // Acrylic 不可用或调用失败时回退普通 Blur；内容资源本身始终有近黑承托。
                    if (!Acrylic || !TryAccent(hwnd, AccentEnableAcrylicBlurBehind))
                        TryAccent(hwnd, AccentEnableBlurBehind);
                }
                else TryAccent(hwnd, AccentDisabled);
            }
            catch (Exception ex) { Log.Warn("设置窗口模糊失败：" + ex.Message); }
        }

        static bool TryAccent(IntPtr hwnd, int state)
        {
            var policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = AccentFlagsDrawLeftBorder,
                GradientColor = unchecked((int)0x990D0908), // ABGR：半透明近黑罩色（#08090D）
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(policy));
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WcaData { Attribute = WcaAccentPolicy, Data = ptr, Size = Marshal.SizeOf(policy) };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        static LinearGradientBrush GlassEdge(int edge)
        {
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            br.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(edge), 0xFF, 0xFF, 0xFF), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(edge * 55 / 100), 0xFF, 0xFF, 0xFF), 0.45));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(edge * 33 / 100), 0x93, 0xA7, 0xFF), 1));
            br.Freeze();
            return br;
        }

        static LinearGradientBrush TitleGlass()
        {
            var c = Theme.Parse(Settings.ThemeColor, Color.FromRgb(0x93, 0xA7, 0xFF));
            var navy = Settings.LightTheme ? Color.FromRgb(0xF4, 0xF6, 0xFB) : Color.FromRgb(0x08, 0x09, 0x0D);
            var from = Theme.Mix(navy, c, 0.05);
            var to = Theme.Mix(navy, c, 0.02);
            var br = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, from.R, from.G, from.B), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, to.R, to.G, to.B), 1));
            br.Freeze();
            return br;
        }
    }
}
