using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace MoonLauncher.UI
{
    /// <summary>
    /// 文件 / 文件夹选择（Avalonia StorageProvider）。
    /// 原实现用 Vista 风格的 IFileOpenDialog COM 接口，Windows 专属；
    /// Avalonia 的 StorageProvider 在三个平台上都映射到系统原生对话框。
    /// 注意：Avalonia 的挑框是异步的，调用处要 await。
    /// </summary>
    public static class Pickers
    {
        static TopLevel Top => MainWindow.Current;

        public static async Task<string> PickFileAsync(string title, string name = null, params string[] extensions)
        {
            var top = Top;
            if (top == null) return null;
            var types = BuildTypes(name, extensions);
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = types,
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }

        public static async Task<string> SaveFileAsync(string title, string suggestedName, string name = null, params string[] extensions)
        {
            var top = Top;
            if (top == null) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = BuildTypes(name, extensions),
            });
            return file?.TryGetLocalPath();
        }

        public static async Task<string> PickFolderAsync(string title)
        {
            var top = Top;
            if (top == null) return null;
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        static List<FilePickerFileType> BuildTypes(string name, string[] extensions)
        {
            if (extensions == null || extensions.Length == 0) return null;
            var patterns = extensions.Select(e => e.StartsWith(".") ? "*" + e : "*." + e).ToArray();
            return new List<FilePickerFileType>
            {
                new FilePickerFileType(name ?? "文件") { Patterns = patterns },
            };
        }
    }
}
