using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OpenLauncher.Core;

namespace OpenLauncher.UI
{    /// <summary>
    /// 液态玻璃（仿 macOS / iOS）：窗口亚克力模糊 + 半透明卡片 + 渐变描边 + 主题色半透明标题栏。
    /// 全部通过运行时替换 Application 级资源实现（页面用的是 DynamicResource，立即生效，无需重启）。
    /// </summary>
    public static class Glass
    {
        public static bool Active { get; private set; }

        // ---------- 可调参数（设置 → 个性化 → 液态玻璃调试，0-100；亚克力开关） ----------
        static int A(string key, int def) => Math.Max(0, Math.Min(100, Settings.GetInt(key, def)));
        static byte Alpha(int pct) => (byte)Math.Round(pct * 255.0 / 100.0);
        static bool Acrylic => Settings.GetBool("ui.glass.acrylic", true);

        // ---------- DWM 亚克力模糊 ----------
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WcaData data);

        [StructLayout(LayoutKind.Sequential)]
        struct WcaData
        {
            public int Attribute; // WCA_ACCENT_POLICY = 19
            public IntPtr Data;
            public int Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor; // ABGR
            public int AnimationId;
        }

        const int WcaAccentPolicy = 19;
        const int AccentEnableAcrylicBlurBehind = 4; // 失败时回落到 3（普通模糊）
        const int AccentEnableBlurBehind = 3;
        const int AccentDisabled = 0;
        const int AccentFlagsDrawLeftBorder = 0x20; // 让模糊在边框线上也生效

        /// <summary>开启 / 关闭。窗口句柄还没准备好时会等 SourceInitialized 再应用模糊。</summary>
        public static void Apply(Window win, bool on)
        {
            Active = on;
            var r = Application.Current.Resources;
            if (on)
            {
                r["Bg"] = Solid(Alpha(A("ui.glass.bg", 35)), 0xF2, 0xF5, 0xFA);            // 窗口底：半透明白，透出亚克力模糊
                var card = A("ui.glass.card", 65);
                r["Card"] = Solid(Alpha(card), 0xFF, 0xFF, 0xFF);                        // 卡片
                r["CardHover"] = Solid(Alpha(Math.Min(100, card + 40)), 0xFF, 0xFF, 0xFF); // 悬停：更亮
                r["Line"] = GlassEdge(A("ui.glass.edge", 90));                           // 描边：左上亮 → 右下暗的渐变（玻璃的"边"）
                r["TitleBrush"] = TitleGlass();                                          // 标题栏：主题色半透明渐变
            }
            else
            {
                r["Bg"] = Solid(0xFF, 0xEE, 0xF2, 0xF8);
                r["Card"] = Solid(0xFF, 0xFF, 0xFF, 0xFF);
                r["CardHover"] = Solid(0xFF, 0xF7, 0xF9, 0xFC);
                r["Line"] = Solid(0xFF, 0xE3, 0xE8, 0xF0);
                Theme.Apply(Settings.ThemeColor); // TitleBrush 恢复不透明
            }

            var mw = win as MainWindow;
            if (mw != null) mw.BgImage.Opacity = on ? A("ui.glass.img", 45) / 100.0 : 0.28; // 玻璃模式下背景图透出纵深，关闭时恢复默认

            var helper = new WindowInteropHelper(win);
            if (helper.Handle == IntPtr.Zero)
            {
                win.SourceInitialized += (s, e) => SetBlur(new WindowInteropHelper(win).Handle, on);
            }
            else SetBlur(helper.Handle, on);
        }

        /// <summary>换主题色后调用：液态玻璃开着时标题栏跟着换色。</summary>
        public static void RefreshTitle() { if (Active) Application.Current.Resources["TitleBrush"] = TitleGlass(); }

        static void SetBlur(IntPtr hwnd, bool on)
        {
            try
            {
                if (on)
                {
                    // 先尝试亚克力（Win10 1803+ / Win11），关闭或失败时回落普通模糊
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
                GradientColor = unchecked((int)0x99FAF5F2), // 极淡的冷白罩色（ABGR）
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(policy));
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WcaData { Attribute = WcaAccentPolicy, Data = ptr, Size = Marshal.SizeOf(policy) };
                var hr = SetWindowCompositionAttribute(hwnd, ref data);
                return hr == 0; // S_OK
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        // ---------- 画刷 ----------
        static SolidColorBrush Solid(byte a, byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }

        /// <summary>玻璃描边：左上边缘高亮（反光），向右下渐隐——液态玻璃的关键视觉。edge 为整体亮度（0-100）。</summary>
        static LinearGradientBrush GlassEdge(int edge)
        {
            var br = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
            };
            br.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(edge), 0xFF, 0xFF, 0xFF), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(edge * 55 / 100), 0xFF, 0xFF, 0xFF), 0.45));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(Alpha(edge * 33 / 100), 0xD8, 0xDE, 0xE8), 1));
            br.Freeze();
            return br;
        }

        /// <summary>标题栏：主题色渐变压半透明，透出背后的模糊。</summary>
        static LinearGradientBrush TitleGlass()
        {
            var c = Theme.Parse(Settings.ThemeColor, Color.FromRgb(0x2F, 0x7B, 0xF5));
            var to = Theme.Mix(c, Color.FromRgb(0x7C, 0x5C, 0xFA), 0.22);
            var br = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
            };
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, c.R, c.G, c.B), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(0xAE, to.R, to.G, to.B), 1));
            br.Freeze();
            return br;
        }
    }
}
