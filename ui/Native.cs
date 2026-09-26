// Native.cs —— 到 javastudio_core.dll 的 P/Invoke 封装
//
// 三条约定，改这个文件之前先看一眼：
//
//   1) 字符串一律按 UTF-8 传。.NET 的宽字符和 C++ 的 char* 不是一回事，
//      用 Marshal.StringToCoTaskMemUTF8 手动转，别指望默认封送。
//   2) DLL 返回的 char* 必须 js_free。这里用 SafeHandle 不合适（不是句柄），
//      所以统一包进 Using(() => ...) 里，出了作用域就释放。
//   3) 所有调用都不要在 UI 线程上跑长时间任务 —— 编译、下载会卡住界面。
//      Core.Run 是给这些用的（见 CoreService.cs）。

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JavaStudio;

internal static class Native
{
    private const string Dll = "javastudio_core";

    // ---------------------------------------------------------------- 内存

    [DllImport(Dll, EntryPoint = "js_free")]
    internal static extern void Free(IntPtr s);

    /// <summary>
    /// 拿到 DLL 返回的字符串，转成托管 string 之后立刻释放。
    /// 不做成属性是因为必须成对调用，写成方法才不容易漏。
    /// </summary>
    internal static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return string.Empty;
        try
        {
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            var buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }
        finally
        {
            Free(p);
        }
    }

    private sealed class Utf8 : IDisposable
    {
        public IntPtr Ptr;
        public Utf8(string s)
        {
            Ptr = string.IsNullOrEmpty(s)
                ? Marshal.StringToCoTaskMemUTF8("")
                : Marshal.StringToCoTaskMemUTF8(s);
        }
        public void Dispose()
        {
            if (Ptr != IntPtr.Zero) { Marshal.ZeroFreeCoTaskMemUTF8(Ptr); Ptr = IntPtr.Zero; }
        }
        public static implicit operator IntPtr(Utf8 u) => u.Ptr;
    }

    private static IntPtr U(string s)
    {
        return string.IsNullOrEmpty(s)
            ? Marshal.StringToCoTaskMemUTF8("")
            : Marshal.StringToCoTaskMemUTF8(s);
    }

    private static void Release(IntPtr p) => Marshal.ZeroFreeCoTaskMemUTF8(p);

    // ---------------------------------------------------------------- 生命周期

    [DllImport(Dll, EntryPoint = "js_init")]
    private static extern void Init(IntPtr appDataDir, IntPtr exeDir);

    internal static void Init(string appDataDir, string exeDir)
    {
        var a = U(appDataDir); var e = U(exeDir);
        try { Init(a, e); } finally { Release(a); Release(e); }
    }

    [DllImport(Dll, EntryPoint = "js_shutdown")]
    internal static extern void Shutdown();

    // 日志回调。C++ 侧每写一条日志就调一次。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogSink(IntPtr level, IntPtr msg, IntPtr ud);

    [DllImport(Dll, EntryPoint = "js_set_log_sink")]
    internal static extern void SetLogSink(LogSink cb, IntPtr ud);

    // ---------------------------------------------------------------- JDK

    [DllImport(Dll, EntryPoint = "js_jdk_list")]
    private static extern IntPtr JdkListRaw();
    internal static string JdkList() => Take(JdkListRaw());

    [DllImport(Dll, EntryPoint = "js_jdk_current")]
    private static extern IntPtr JdkCurrentRaw();
    internal static string JdkCurrent() => Take(JdkCurrentRaw());

    [DllImport(Dll, EntryPoint = "js_jdk_select")]
    internal static extern int JdkSelect(int major);

    [DllImport(Dll, EntryPoint = "js_jdk_rescan")]
    internal static extern void JdkRescan();

    // ---------------------------------------------------------------- 项目

    [DllImport(Dll, EntryPoint = "js_open_project")]
    private static extern IntPtr OpenProjectRaw(IntPtr root);
    internal static string OpenProject(string root)
    {
        var p = U(root);
        try { return Take(OpenProjectRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_dir_tree")]
    private static extern IntPtr DirTreeRaw(IntPtr root);
    internal static string DirTree(string root)
    {
        var p = U(root);
        try { return Take(DirTreeRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_validate_project")]
    private static extern IntPtr ValidateProjectRaw(IntPtr name, IntPtr group, IntPtr artifact);
    internal static string ValidateProject(string name, string group, string artifact)
    {
        var a = U(name); var b = U(group); var c = U(artifact);
        try { return Take(ValidateProjectRaw(a, b, c)); } finally { Release(a); Release(b); Release(c); }
    }

    [DllImport(Dll, EntryPoint = "js_create_project")]
    private static extern IntPtr CreateProjectRaw(IntPtr parentDir, IntPtr name, IntPtr group,
        IntPtr artifact, IntPtr version, IntPtr buildSystem, IntPtr jdkRelease,
        int addSampleCode, int createGitRepo);

    internal static string CreateProject(string parentDir, string name, string group,
        string artifact, string version, string buildSystem, string jdkRelease,
        bool addSampleCode, bool createGitRepo)
    {
        var a = U(parentDir); var b = U(name); var c = U(group); var d = U(artifact);
        var e = U(version); var f = U(buildSystem); var g = U(jdkRelease);
        try
        {
            return Take(CreateProjectRaw(a, b, c, d, e, f, g,
                addSampleCode ? 1 : 0, createGitRepo ? 1 : 0));
        }
        finally { Release(a); Release(b); Release(c); Release(d); Release(e); Release(f); Release(g); }
    }

    [DllImport(Dll, EntryPoint = "js_default_projects_dir")]
    private static extern IntPtr DefaultProjectsDirRaw();
    internal static string DefaultProjectsDir() => Take(DefaultProjectsDirRaw());

    [DllImport(Dll, EntryPoint = "js_guess_main")]
    private static extern IntPtr GuessMainRaw(IntPtr root);
    internal static string GuessMain(string root)
    {
        var p = U(root);
        try { return Take(GuessMainRaw(p)); } finally { Release(p); }
    }

    // ---------------------------------------------------------------- 文件

    [DllImport(Dll, EntryPoint = "js_read_file")]
    private static extern IntPtr ReadFileRaw(IntPtr path);
    internal static string ReadFile(string path)
    {
        var p = U(path);
        try { return Take(ReadFileRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_write_file")]
    private static extern IntPtr WriteFileRaw(IntPtr path, IntPtr text);
    internal static string WriteFile(string path, string text)
    {
        var p = U(path); var t = U(text);
        try { return Take(WriteFileRaw(p, t)); } finally { Release(p); Release(t); }
    }

    [DllImport(Dll, EntryPoint = "js_list_dir")]
    private static extern IntPtr ListDirRaw(IntPtr path);
    internal static string ListDir(string path)
    {
        var p = U(path);
        try { return Take(ListDirRaw(p)); } finally { Release(p); }
    }

    // ---------------------------------------------------------------- 构建

    [DllImport(Dll, EntryPoint = "js_compile")]
    private static extern IntPtr CompileRaw(IntPtr root, IntPtr jdkHome, IntPtr classpath, int verbose);
    internal static string Compile(string root, string jdkHome, string classpath, bool verbose)
    {
        var a = U(root); var b = U(jdkHome); var c = U(classpath);
        try { return Take(CompileRaw(a, b, c, verbose ? 1 : 0)); }
        finally { Release(a); Release(b); Release(c); }
    }

    [DllImport(Dll, EntryPoint = "js_run")]
    private static extern IntPtr RunRaw(IntPtr root, IntPtr jdkHome, IntPtr mainClass, IntPtr classpath);
    internal static string RunProject(string root, string jdkHome, string mainClass, string classpath)
    {
        var a = U(root); var b = U(jdkHome); var c = U(mainClass); var d = U(classpath);
        try { return Take(RunRaw(a, b, c, d)); } finally { Release(a); Release(b); Release(c); Release(d); }
    }

    [DllImport(Dll, EntryPoint = "js_check")]
    private static extern IntPtr CheckRaw(IntPtr root);
    internal static string Check(string root)
    {
        var p = U(root);
        try { return Take(CheckRaw(p)); } finally { Release(p); }
    }

    // ---------------------------------------------------------------- 第三方库

    [DllImport(Dll, EntryPoint = "js_libs_catalog")]
    private static extern IntPtr LibsCatalogRaw();
    internal static string LibsCatalog() => Take(LibsCatalogRaw());

    [DllImport(Dll, EntryPoint = "js_libs_selection")]
    private static extern IntPtr LibsSelectionRaw(IntPtr root);
    internal static string LibsSelection(string root)
    {
        var p = U(root);
        try { return Take(LibsSelectionRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_save")]
    private static extern int LibsSaveRaw(IntPtr root, IntPtr ids);
    internal static bool LibsSave(string root, string ids)
    {
        var a = U(root); var b = U(ids);
        try { return LibsSaveRaw(a, b) != 0; } finally { Release(a); Release(b); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_resolve")]
    private static extern IntPtr LibsResolveRaw(IntPtr root, IntPtr ids);
    internal static string LibsResolve(string root, string ids)
    {
        var a = U(root); var b = U(ids);
        try { return Take(LibsResolveRaw(a, b)); } finally { Release(a); Release(b); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_download")]
    private static extern IntPtr LibsDownloadRaw(IntPtr id);
    internal static string LibsDownload(string id)
    {
        var p = U(id);
        try { return Take(LibsDownloadRaw(p)); } finally { Release(p); }
    }

    /// <summary>
    /// 下载进度回调（对应 C 侧的 js_download_progress）。返回 0 表示中止。
    /// done = 已写入磁盘的字节，total = Content-Length（0 = 服务器没给）。
    /// 注意它是在**下载线程**上被调用的，不是 UI 线程。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DownloadProgressCb(ulong done, ulong total, IntPtr user);

    [DllImport(Dll, EntryPoint = "js_libs_download_ex")]
    private static extern IntPtr LibsDownloadProgressRaw(IntPtr id,
        DownloadProgressCb cb, IntPtr user);
    internal static string LibsDownloadProgress(string id, DownloadProgressCb cb)
    {
        var p = U(id);
        try { return Take(LibsDownloadProgressRaw(p, cb, IntPtr.Zero)); }
        finally { Release(p); }
    }

    // ---- 仓库查询 / 缓存 / pom：第三方库页面重写后新增的那一批

    [DllImport(Dll, EntryPoint = "js_libs_search")]
    private static extern IntPtr LibsSearchRaw(IntPtr q);
    internal static string LibsSearch(string q)
    {
        var p = U(q);
        try { return Take(LibsSearchRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_versions")]
    private static extern IntPtr LibsVersionsRaw(IntPtr group, IntPtr artifact);
    internal static string LibsVersions(string group, string artifact)
    {
        var a = U(group); var b = U(artifact);
        try { return Take(LibsVersionsRaw(a, b)); } finally { Release(a); Release(b); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_cache_list")]
    private static extern IntPtr LibsCacheListRaw();
    internal static string LibsCacheList() => Take(LibsCacheListRaw());

    [DllImport(Dll, EntryPoint = "js_libs_cache_remove")]
    private static extern int LibsCacheRemoveRaw(IntPtr coord);
    internal static bool LibsCacheRemove(string coord)
    {
        var p = U(coord);
        try { return LibsCacheRemoveRaw(p) != 0; } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_pom_coords")]
    private static extern IntPtr LibsPomCoordsRaw(IntPtr root);
    internal static string LibsPomCoords(string root)
    {
        var p = U(root);
        try { return Take(LibsPomCoordsRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_pom_exists")]
    private static extern int LibsPomExistsRaw(IntPtr root);
    internal static bool LibsPomExists(string root)
    {
        var p = U(root);
        try { return LibsPomExistsRaw(p) != 0; } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_pom_add")]
    private static extern int LibsPomAddRaw(IntPtr root, IntPtr coord);
    internal static bool LibsPomAdd(string root, string coord)
    {
        var a = U(root); var b = U(coord);
        try { return LibsPomAddRaw(a, b) != 0; } finally { Release(a); Release(b); }
    }

    [DllImport(Dll, EntryPoint = "js_libs_pom_remove")]
    private static extern int LibsPomRemoveRaw(IntPtr root, IntPtr g, IntPtr a);
    internal static bool LibsPomRemove(string root, string g, string a)
    {
        var p = U(root); var pg = U(g); var pa = U(a);
        try { return LibsPomRemoveRaw(p, pg, pa) != 0; }
        finally { Release(p); Release(pg); Release(pa); }
    }

    // ---------------------------------------------------------------- 图标

    [DllImport(Dll, EntryPoint = "js_icons_all")]
    private static extern IntPtr IconsAllRaw(IntPtr dir);
    internal static string IconsAll(string dir)
    {
        var p = U(dir);
        try { return Take(IconsAllRaw(p)); } finally { Release(p); }
    }

    // ---------------------------------------------------------------- 设置 / 最近

    [DllImport(Dll, EntryPoint = "js_settings_get")]
    private static extern IntPtr SettingsGetRaw(IntPtr key);
    internal static string SettingsGet(string key)
    {
        var p = U(key);
        try { return Take(SettingsGetRaw(p)); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_settings_set")]
    private static extern void SettingsSetRaw(IntPtr key, IntPtr value);
    internal static void SettingsSet(string key, string value)
    {
        var a = U(key); var b = U(value);
        try { SettingsSetRaw(a, b); } finally { Release(a); Release(b); }
    }

    // 构建输出面板里我们自己拼的提示行说哪国话（javac/mvn 原样透传的不受影响）
    [DllImport(Dll, EntryPoint = "js_build_set_lang")]
    private static extern void BuildSetLangRaw(IntPtr lang);
    internal static void BuildSetLang(string lang)
    {
        var p = U(lang);
        try { BuildSetLangRaw(p); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_recent")]
    private static extern IntPtr RecentRaw();
    internal static string Recent() => Take(RecentRaw());

    [DllImport(Dll, EntryPoint = "js_recent_add")]
    private static extern void RecentAddRaw(IntPtr root);
    internal static void RecentAdd(string root)
    {
        var p = U(root);
        try { RecentAddRaw(p); } finally { Release(p); }
    }

    [DllImport(Dll, EntryPoint = "js_recent_clear")]
    internal static extern void RecentClear();

    [DllImport(Dll, EntryPoint = "js_recent_remove")]
    private static extern void RecentRemoveRaw(IntPtr root);
    internal static void RecentRemove(string root)
    {
        var p = U(root);
        try { RecentRemoveRaw(p); } finally { Release(p); }
    }

    // ---------------------------------------------------------------- 杂项

    // 内核自报的版本号（{"version":"0.1.0-Preview.1"}）。界面不自己记一份，
    // 都是从产物里读：内核的这个串是 mk.py 构建时编进去的，exe 的那个是
    // csproj 写进程序集特性的，谁都不会跟 VERSION 文件对不上。
    [DllImport(Dll, EntryPoint = "js_version")]
    private static extern IntPtr VersionRaw();
    internal static string Version() => Take(VersionRaw());

    [DllImport(Dll, EntryPoint = "js_free_space")]
    private static extern long FreeSpaceRaw(IntPtr path);
    internal static long FreeSpace(string path)
    {
        var p = U(path);
        try { return FreeSpaceRaw(p); } finally { Release(p); }
    }
}
