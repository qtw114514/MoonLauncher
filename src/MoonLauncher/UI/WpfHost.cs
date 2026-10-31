using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    /// <summary>
    /// WPF 侧的接口实现集合：业务层只认 Core 里的抽象，具体怎么落到 WPF 全在这里。
    /// 换到 Avalonia 时替换的也是这一个文件对应的实现。
    /// </summary>
    internal static class WpfHost
    {
        /// <summary>把 Core 的 UI 调度与图像解码接到 WPF 上</summary>
        public static void Attach(Dispatcher dispatcher)
        {
            TaskManager.Ui = new WpfDispatcher(dispatcher);
            Imaging.Decoder = new WpfImageDecoder();
        }

        /// <summary>Core 的 BGRA32 像素转成 WPF 可绑定的位图</summary>
        public static BitmapSource ToBitmapSource(Bitmap32 bmp)
        {
            if (bmp == null) return null;
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, bmp.Pixels, bmp.Stride);
            src.Freeze();
            return src;
        }
    }

    internal sealed class WpfDispatcher : IUiDispatcher
    {
        readonly Dispatcher _dispatcher;

        public WpfDispatcher(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public bool IsOnUiThread => _dispatcher.CheckAccess();

        public void Post(Action action) => _dispatcher.BeginInvoke(action);

        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
    }

    internal sealed class WpfImageDecoder : IImageDecoder
    {
        public Bitmap32 Decode(byte[] data, int decodeWidth = 0)
        {
            if (data == null || data.Length == 0) return null;
            using (var ms = new MemoryStream(data))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();

                var src = bi.Format == PixelFormats.Bgra32
                    ? (BitmapSource)bi
                    : new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
                int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                src.CopyPixels(px, stride, 0);
                return new Bitmap32(w, h, px);
            }
        }
    }

    /// <summary>XAML 绑定用：把 <see cref="Bitmap32"/> 转成 Image 能用的 ImageSource</summary>
    public sealed class Bitmap32Converter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            WpfHost.ToBitmapSource(value as Bitmap32);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
