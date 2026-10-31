// 后端跨平台冒烟测试：在 macOS / Linux 上直接跑，验证 Platform 的非 Windows 分支不会抛异常。
//
// 编译与运行（macOS 上用 Mono）：
//   dotnet build tools/Backend/MoonLauncher.Backend.csproj
//   mcs -r:tools/Backend/bin/Debug/MoonLauncher.Backend.dll -out:/tmp/smoke.exe tests/backend-smoke/smoke.cs
//   mono /tmp/smoke.exe
//
// 在 Windows 上跑时，断言会自动取 Windows 的分支。
using System;
using System.IO;
using MoonLauncher.Core;

static class Smoke
{
    static int failed;

    static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? "  -> " + detail : ""));
        if (!ok) failed++;
    }

    static int Main()
    {
        Check("IsWindows 与运行环境一致", Platform.IsWindows == (Environment.OSVersion.Platform == PlatformID.Win32NT));
        Check("PathComparison 平台规则",
            Platform.PathComparison == (Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        Check("PathComparer 与 PathComparison 一致",
            Platform.PathComparer.Equals("A", "a") == Platform.IsWindows);

        var mem = Platform.Memory();
        Check("Memory 返回正数", mem.totalMb > 0 && mem.availMb > 0, mem.totalMb + "/" + mem.availMb + " MB");

        Check("OsArch 非空", !string.IsNullOrEmpty(Platform.OsArch), Platform.OsArch);
        Check("OsVersion 非空", Platform.OsVersion != null, Platform.OsVersion == null ? "null" : Platform.OsVersion.ToString());

        var sep = Platform.PathSeparator;
        Check("LongPath 仅在 Windows 加前缀",
            Platform.IsWindows || Platform.LongPath("/tmp/x") == "/tmp/x", Platform.LongPath("/tmp/x"));
        Check("FromManifestPath 使用本机分隔符",
            Platform.FromManifestPath("a/b/c") == "a" + sep + "b" + sep + "c", Platform.FromManifestPath("a/b/c"));
        Check("ToManifestPath 统一为 /", Platform.ToManifestPath("a" + sep + "b") == "a/b", Platform.ToManifestPath("a" + sep + "b"));
        Check("RealPath 非空", Platform.RealPath(Path.GetTempPath()) != null, Platform.RealPath(Path.GetTempPath()));
        Check("IsUnder", Platform.IsUnder(Path.Combine("/tmp", "a", "b"), Path.Combine("/tmp", "a")) || Platform.IsWindows);

        byte[] cipher;
        var protectedOk = Platform.TryProtect(new byte[] { 1, 2, 3 }, null, out cipher);
        Check("TryProtect 仅在 Windows 成功", protectedOk == Platform.IsWindows);
        byte[] plain;
        Check("TryUnprotect 与 TryProtect 对称",
            Platform.TryUnprotect(cipher, null, out plain) == Platform.IsWindows);

        bool called = false;
        Platform.VisitRegistryValues(new[] { "SOFTWARE\\JavaSoft" }, new[] { "JavaHome" }, null, 2, delegate { called = true; });
        Check("VisitRegistryValues 仅在 Windows 回调", called == Platform.IsWindows);

        Check("QuoteArg 无空格时原样", Util.QuoteArg("/usr/bin/java") == "/usr/bin/java", Util.QuoteArg("/usr/bin/java"));
        Check("QuoteArg/SplitArgs 可往返", RoundTrip("a b"), "a b");
        Check("QuoteArg/SplitArgs 可往返(引号)", RoundTrip("a'b"), "a'b");

        var enc = PlatformEncoding.SystemDefault;
        Check("SystemDefault 编码符合平台",
            Platform.IsWindows ? enc.CodePage != 65001 || enc.WebName == "utf-8" : enc.WebName == "utf-8", enc.WebName);

        Check("OfficialMinecraftDir 指向 minecraft",
            Platform.OfficialMinecraftDir.EndsWith("minecraft", Platform.PathComparison), Platform.OfficialMinecraftDir);

        Check("Java 可执行名符合平台",
            Platform.IsWindows
                ? Platform.JavaConsoleExe == "java.exe" && Platform.JavaWindowedExe == "javaw.exe"
                : Platform.JavaConsoleExe == "java" && Platform.JavaWindowedExe == "java",
            Platform.JavaConsoleExe + "/" + Platform.JavaWindowedExe);

        int roots = 0;
        foreach (var r in Platform.CommonJavaRoots) { if (string.IsNullOrEmpty(r)) throw new Exception("空的 Java 根目录"); roots++; }
        Check("CommonJavaRoots 与平台一致", Platform.IsWindows ? roots == 0 : roots > 0, roots + " 项");

        var tmp = Path.Combine(Path.GetTempPath(), "smoke-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, "x");
        Platform.DeleteToRecycleBin(tmp);
        Check("DeleteToRecycleBin 生效", !File.Exists(tmp));

        Check("SafeFileName 不抛", Util.SafeFileName("a:b*c?.jar") != null, Util.SafeFileName("a:b*c?.jar"));

        var bmp = new Bitmap32(2, 2, new byte[16]);
        Check("Bitmap32 步长", bmp.Stride == 8);
        Check("Imaging.Decode 无解码器返回 null", Imaging.Decode(new byte[] { 1, 2, 3 }) == null);

        Check("TaskManager.OnUi 无 UI 时同步执行", RunsInline());
        Check("TaskManager.OnUiAsync 无 UI 时同步执行", RunsInlineAsync().GetAwaiter().GetResult());

        Console.WriteLine(failed == 0 ? "全部通过" : failed + " 项失败");
        return failed == 0 ? 0 : 1;
    }

    static bool RoundTrip(string value)
    {
        var parts = Util.SplitArgs(Util.QuoteArg(value));
        return parts.Count == 1 && parts[0] == value;
    }

    static bool RunsInline()
    {
        bool ran = false;
        TaskManager.OnUi(delegate { ran = true; });
        return ran;
    }

    static async System.Threading.Tasks.Task<bool> RunsInlineAsync()
    {
        bool ran = false;
        await TaskManager.OnUiAsync(delegate { ran = true; });
        return ran;
    }
}
