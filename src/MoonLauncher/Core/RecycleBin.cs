namespace MoonLauncher.Core
{
    /// <summary>
    /// 删除到回收站（给用户反悔的机会）。
    /// Windows 走 shell32 的 SHFileOperation，macOS 走 Finder，Linux 走 XDG Trash 规范；
    /// 实现集中在 Platform.MoveToRecycleBin。
    /// </summary>
    public static class RecycleBin
    {
        public static void Delete(string path) => Platform.MoveToRecycleBin(path);
    }
}
