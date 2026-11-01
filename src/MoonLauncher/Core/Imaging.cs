using System;

namespace MoonLauncher.Core
{
    /// <summary>
    /// 一张已解码的 32 位位图：BGRA8888，非预乘，自上而下逐行排列。
    ///
    /// 业务层只处理这种纯数据，不认识 WPF 的 BitmapSource / Avalonia 的 Bitmap。
    /// 界面层拿到之后自行转换成各自的图像类型。
    /// </summary>
    public sealed class Bitmap32
    {
        public Bitmap32(int width, int height, byte[] pixels)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (pixels.Length != width * height * 4)
                throw new ArgumentException("像素缓冲区大小与尺寸不符", nameof(pixels));
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }
        public int Height { get; }
        /// <summary>BGRA8888，长度 = Width * Height * 4</summary>
        public byte[] Pixels { get; }
        public int Stride => Width * 4;
    }

    /// <summary>
    /// 把 PNG / JPEG 之类的压缩图像解码成 <see cref="Bitmap32"/>。
    ///
    /// net48 自己没有跨平台的图像解码器，唯一现成的是 WPF 的 BitmapDecoder，
    /// 所以解码这件事必须由界面层提供实现，业务层只声明需求。
    /// </summary>
    public interface IImageDecoder
    {
        /// <summary>
        /// 解码成 BGRA32。<paramref name="decodeWidth"/> 大于 0 时按它缩放，用于省内存。
        /// 解码失败返回 null。
        /// </summary>
        Bitmap32 Decode(byte[] data, int decodeWidth = 0);
    }

    /// <summary>图像解码器的注入点。</summary>
    public static class Imaging
    {
        /// <summary>由启动代码注入；为 null 时所有解码调用返回 null，调用方按"没有图标"处理</summary>
        public static IImageDecoder Decoder { get; set; }

        public static Bitmap32 Decode(byte[] data, int decodeWidth = 0)
        {
            if (data == null || data.Length == 0 || Decoder == null) return null;
            try { return Decoder.Decode(data, decodeWidth); }
            catch (Exception ex) { Log.Warn("图像解码失败：" + ex.Message); return null; }
        }
    }
}
