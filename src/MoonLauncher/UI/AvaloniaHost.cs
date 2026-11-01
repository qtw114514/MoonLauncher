using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    /// <summary>
    /// Avalonia 侧的接口实现集合：业务层只认 Core 里的抽象，具体怎么落到 Avalonia 全在这里。
    /// 与 WPF 分支的 WpfHost 对应，换框架时替换的就是这一个文件里的实现。
    /// </summary>
    internal static class AvaloniaHost
    {
        /// <summary>把 Core 的 UI 调度与图像解码接到 Avalonia 上</summary>
        public static void Attach()
        {
            TaskManager.Ui = new AvaloniaDispatcher();
            Imaging.Decoder = new AvaloniaImageDecoder();
        }

        /// <summary>Core 的 BGRA32 像素转成 Avalonia 可绑定的位图</summary>
        public static Bitmap ToBitmap(Bitmap32 bmp)
        {
            if (bmp == null) return null;
            var wb = new WriteableBitmap(new PixelSize(bmp.Width, bmp.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var fb = wb.Lock())
            {
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(bmp.Pixels, y * bmp.Width * 4, IntPtr.Add(fb.Address, y * fb.RowBytes), bmp.Width * 4);
            }
            return wb;
        }
    }

    internal sealed class AvaloniaDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();

        public void Post(Action action) => Dispatcher.UIThread.Post(action);

        // 用 TaskCompletionSource 包一层，不依赖 Dispatcher.InvokeAsync 的具体返回类型
        public Task InvokeAsync(Action action)
        {
            var tcs = new TaskCompletionSource<bool>();
            Dispatcher.UIThread.Post(() =>
            {
                try { action(); tcs.SetResult(true); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }

    internal sealed class AvaloniaImageDecoder : IImageDecoder
    {
        public Bitmap32 Decode(byte[] data, int decodeWidth = 0)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            using (var bmp = new Bitmap(ms))
            {
                int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
                if (w <= 0 || h <= 0) return null;

                int stride = w * 4;
                var px = new byte[stride * h];
                var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
                try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), px.Length, stride); }
                finally { handle.Free(); }

                // Avalonia 可能以 RGBA 顺序给出像素，统一成 BGRA
                if (bmp.Format == PixelFormat.Rgba8888)
                    for (int i = 0; i < px.Length; i += 4) { var t = px[i]; px[i] = px[i + 2]; px[i + 2] = t; }

                // 需要缩小时用最近邻，省内存也避免图标被糊掉
                if (decodeWidth > 0 && decodeWidth < w)
                {
                    int nh = Math.Max(1, h * decodeWidth / w);
                    var dst = new byte[decodeWidth * nh * 4];
                    for (int y = 0; y < nh; y++)
                    {
                        int sy = y * h / nh;
                        for (int x = 0; x < decodeWidth; x++)
                        {
                            int sx = x * w / decodeWidth;
                            Buffer.BlockCopy(px, (sy * w + sx) * 4, dst, (y * decodeWidth + x) * 4, 4);
                        }
                    }
                    px = dst;
                    w = decodeWidth;
                    h = nh;
                }

                return new Bitmap32(w, h, px);
            }
        }
    }

    /// <summary>XAML 绑定用：把 <see cref="Bitmap32"/> 转成 Image 能用的位图</summary>
    public sealed class Bitmap32Converter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            AvaloniaHost.ToBitmap(value as Bitmap32);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
