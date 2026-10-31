using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MoonLauncher.Core;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 附加属性：&lt;Image ui:RemoteImage.Url="{Binding IconUrl}"/&gt;
    /// 用自己的 HttpClient 下载（走镜像、限并发、磁盘缓存），解码失败就留空。
    /// Avalonia 版：AvaloniaProperty.RegisterAttached 取代 WPF 的 DependencyProperty.RegisterAttached。
    /// </summary>
    public static class RemoteImage
    {
        public static readonly AttachedProperty<string> UrlProperty =
            AvaloniaProperty.RegisterAttached<Image, string>("Url", typeof(RemoteImage));

        public static string GetUrl(Image o) => o.GetValue(UrlProperty);
        public static void SetUrl(Image o, string v) => o.SetValue(UrlProperty, v);

        static readonly ConcurrentDictionary<string, Bitmap> Memory = new ConcurrentDictionary<string, Bitmap>();
        static readonly SemaphoreSlim Gate = new SemaphoreSlim(16);

        static RemoteImage()
        {
            UrlProperty.Changed.AddClassHandler<Image>(OnUrlChanged);
        }

        static async void OnUrlChanged(Image img, AvaloniaPropertyChangedEventArgs e)
        {
            var url = e.NewValue as string;
            img.Source = null;
            if (string.IsNullOrEmpty(url)) return;
            var src = await LoadAsync(url);
            if (GetUrl(img) == url) img.Source = src;
        }

        /// <summary>按文件头判断是不是图片（PNG / JPEG / GIF / WebP / BMP / ICO）</summary>
        static bool LooksLikeImage(byte[] b)
        {
            if (b == null || b.Length < 12) return false;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            if (b[0] == 0xFF && b[1] == 0xD8) return true;
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return true;
            if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return true;
            if (b[0] == 'B' && b[1] == 'M') return true;
            if (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return true;
            return false;
        }

        public static async Task<Bitmap> LoadAsync(string url, int decode = 96)
        {
            if (Memory.TryGetValue(url, out var cached)) return cached;
            var file = Path.Combine(Paths.Ensure(Path.Combine(Paths.Cache, "icons")),
                Util.Sha256(System.Text.Encoding.UTF8.GetBytes(url)).Substring(0, 24));
            try
            {
                byte[] data = null;
                if (File.Exists(file))
                {
                    data = File.ReadAllBytes(file);
                    if (!LooksLikeImage(data)) { data = null; Util.TryDelete(file); }
                }
                if (data == null)
                {
                    await Gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        foreach (var u in Mirrors.Expand(url))
                        {
                            try
                            {
                                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                                using (var resp = await Http.Client.GetAsync(u, cts.Token).ConfigureAwait(false))
                                {
                                    if (!resp.IsSuccessStatusCode) continue;
                                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                                    if (!LooksLikeImage(bytes)) { Mirrors.ReportFailure(u); continue; }
                                    data = bytes;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (data == null) return null;
                        try { File.WriteAllBytes(file, data); } catch { }
                    }
                    finally { Gate.Release(); }
                }
                var bmp = Theme.LoadImage(data, decode);
                if (bmp != null) Memory[url] = bmp;
                return bmp;
            }
            catch { return null; }
        }
    }
}
