namespace MoonLauncher.Core
{
    /// <summary>
    /// 删除到回收站（给用户反悔的机会）。
    /// Windows 走资源管理器回收站，其他平台没有统一机制，由 Platform 直接删除。
    /// </summary>
    public static class RecycleBin
    {
        public static void Delete(string path) => Platform.DeleteToRecycleBin(path);
    }
}
