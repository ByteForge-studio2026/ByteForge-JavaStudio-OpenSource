// Core.cs —— 对 Native 的薄封装：JSON 解析 + 日志事件
//
// 这一层的存在意义是让界面代码里看不到 IntPtr 和 JSON 字符串。
// 界面要的是「一个项目树」「一组诊断」，不是一串需要自己 Parse 的文本。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace JavaStudio;

internal static class Core
{
    public static string ExeDir { get; private set; } = "";
    public static string AppDataDir { get; private set; } = "";
    public static string IconsDir => Path.Combine(ExeDir, "assets", "icons");

    /// <summary>C++ 侧每写一条日志就触发。参数是 (级别, 内容)。</summary>
    public static event Action<string, string> OnLog;

    // 回调委托必须自己存一份，否则 GC 把它回收了，
    // C++ 再调进来就是访问已释放的内存 —— 表现为随机崩溃。
    private static Native.LogSink _sink;
    private static bool _ready;

    public static void Init()
    {
        if (_ready) return;

        ExeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        AppDataDir = ResolveDataDir();
        Directory.CreateDirectory(AppDataDir);

        _sink = (levelPtr, msgPtr, _) =>
        {
            string level = Marshal2(levelPtr);
            string msg = Marshal2(msgPtr);
            try { OnLog?.Invoke(level, msg); } catch { /* 界面没起来就算了 */ }
        };

        Native.Init(AppDataDir, ExeDir);
        Native.SetLogSink(_sink, IntPtr.Zero);
        _ready = true;
    }

    /// <summary>
    /// 数据目录：配置、设置、最近项目、JDK 扫描、项目日志、依赖缓存、崩溃日志全在这下面。
    /// 这个目录还会传给 C++ 内核（js_init 第一个参数），改一处整条链路跟着走。
    ///
    /// 解析顺序：
    ///   1. 环境变量 JS_DATA_DIR —— 想放哪儿都行（开发机上就是靠它指到 D 盘的）
    ///   2. 安装目录下的 datadir.txt —— 装机时写一次就能固定位置，改起来比改环境变量省事
    ///   3. **安装目录下的 .javastudio** —— 默认。配置跟着程序走，
    ///      整个目录拷到别的机器上，主题 / 语言 / 最近项目 / JDK 选择全都还在。
    ///      原来默认写 %LOCALAPPDATA%\JavaStudio（也就是 C 盘），
    ///      程序和它的数据分家，拷走程序等于把配置丢了。
    ///   4. 上面那个写不进去（装到 Program Files 这类只读的地方）才退到用户目录
    ///
    /// ⚠️ 这里曾经硬编码成 D:\S++\.javastudio。开发机上没问题，
    /// 可一旦拿去装机，别人的机器上要么没有 D 盘、要么那个路径压根不该动 ——
    /// 分发版绝不能写死开发机的路径。开发机想继续用 D 盘，设 JS_DATA_DIR 就行。
    /// </summary>
    private static string ResolveDataDir()
    {
        string env = Environment.GetEnvironmentVariable("JS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        // 安装目录旁边放了 datadir.txt 就以它为准（安装程序可以写，默认不写）
        try
        {
            string hint = Path.Combine(ExeDir, "datadir.txt");
            if (File.Exists(hint))
            {
                string s = File.ReadAllText(hint).Trim();
                if (s.Length > 0) return s;
            }
        }
        catch { /* 读不了就用默认 */ }

        // 放在 .javastudio 子目录里，不跟安装目录自己的 jdk/ assets/ item/ 混在根上
        string want = Path.Combine(ExeDir, ".javastudio");
        if (Writable(want)) return want;

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();   // 兜底，别让它空着
        return Path.Combine(local, "JavaStudio");
    }

    /// <summary>
    /// 这个目录能不能真的写东西。
    /// ⚠️ 光看目录在不在不算 —— 建目录成功不代表有写权限
    /// （装到 Program Files 下时目录照样建得出来，一写文件就报警）。
    /// 所以建完还要真写一个字节试试。
    /// </summary>
    private static bool Writable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writetest");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string Marshal2(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        int len = 0;
        while (System.Runtime.InteropServices.Marshal.ReadByte(p, len) != 0) len++;
        var buf = new byte[len];
        System.Runtime.InteropServices.Marshal.Copy(p, buf, 0, len);
        return System.Text.Encoding.UTF8.GetString(buf);
    }

    public static void Shutdown() => Native.Shutdown();

    // ---------------------------------------------------------------- 小工具

    private static JsonDocument J(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) s = "{}";
        try { return JsonDocument.Parse(s); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static string Str(JsonElement e, string name, string def = "")
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : def;

    private static bool Bool(JsonElement e, string name, bool def = false)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : def;

    private static int Int(JsonElement e, string name, int def = 0)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private static List<string> Arr(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString());
        }
        return list;
    }

    // ---------------------------------------------------------------- 模型

    /// <summary>
    /// Builtin = 随软件一起发布的那套（exe 旁边的 jdk\ 下），由内核扫描时打上标记。
    /// 它的 Home 是安装目录里的路径，界面上不该原样显示 —— 显示「内置」就行。
    /// 别在 C# 这边靠路径特征去猜（比如找 "\jdk\jdk-"）：
    /// 用户自己装一套到同名目录里就会误判，而内核是当场知道自己从哪儿扫出来的。
    /// </summary>
    public sealed record JdkInfo(int Major, string Version, string Vendor, string Home,
                                 string Label, bool Ok, bool Builtin = false)
    {
        public override string ToString() => Label;

        /// <summary>「位置」一栏显示什么。内置的显示「内置」，外部的还是给路径。</summary>
        public string LocationText => Builtin ? Lang.T("jdk.builtin") : Home;
    }

    public sealed record TreeNode(string Name, string Path, bool IsDir, List<TreeNode> Children);

    public sealed record ProjectInfo(bool Ok, string Path, string Name, string Tool, string MainClass,
        List<string> Jars, List<string> Ready, List<string> Missing, List<TreeNode> Tree, string Error);

    public sealed record Diagnostic(string Level, string File, int Line, int Col, string Message);

    public sealed record BuildResult(bool Ok, int ExitCode, string Tool, string Output,
        int ErrorCount, int WarnCount, double Seconds, List<Diagnostic> Diags);

    public sealed record LibEntry(string Id, string Name, string Group, string Artifact,
        string Version, string Desc, string Category, bool Cached)
    {
        public string Coord => $"{Group}:{Artifact}:{Version}";
    }

    // ---------------------------------------------------------------- JDK

    public static List<JdkInfo> Jdks()
    {
        using var d = J(Native.JdkList());
        var list = new List<JdkInfo>();
        if (d.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in d.RootElement.EnumerateArray())
                list.Add(new JdkInfo(Int(x, "major"), Str(x, "version"), Str(x, "vendor"),
                                     Str(x, "home"), Str(x, "label"), Bool(x, "ok"),
                                     Bool(x, "builtin")));
        }
        return list;
    }

    public static JdkInfo CurrentJdk()
    {
        using var d = J(Native.JdkCurrent());
        var r = d.RootElement;
        return new JdkInfo(Int(r, "major"), "", "", Str(r, "home"), Str(r, "label"),
                           Bool(r, "ok"), Bool(r, "builtin"));
    }

    public static bool SelectJdk(int major) => Native.JdkSelect(major) != 0;

    // ---------------------------------------------------------------- 项目

    private static List<TreeNode> ParseTree(JsonElement arr)
    {
        var list = new List<TreeNode>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in arr.EnumerateArray())
        {
            var kids = new List<TreeNode>();
            if (x.TryGetProperty("children", out var c)) kids = ParseTree(c);
            list.Add(new TreeNode(Str(x, "name"), Str(x, "path"), Bool(x, "dir"), kids));
        }
        return list;
    }

    public static ProjectInfo OpenProject(string root)
    {
        using var d = J(Native.OpenProject(root));
        var r = d.RootElement;
        var libs = r.TryGetProperty("libs", out var l) ? l : default;
        var tree = new List<TreeNode>();
        if (r.TryGetProperty("tree", out var t)) tree = ParseTree(t);
        return new ProjectInfo(Bool(r, "ok"), Str(r, "path"), Str(r, "name"), Str(r, "tool"),
            Str(r, "mainClass"), Arr(libs, "jars"), Arr(libs, "ready"), Arr(libs, "missing"),
            tree, Str(r, "error"));
    }

    public static List<TreeNode> DirTree(string root)
    {
        using var d = J(Native.DirTree(root));
        return ParseTree(d.RootElement);
    }

    public static string ValidateProject(string name, string group, string artifact)
    {
        using var d = J(Native.ValidateProject(name, group, artifact));
        return d.RootElement.ValueKind == JsonValueKind.String
            ? d.RootElement.GetString() : "";
    }

    public sealed record CreateResult(bool Ok, string Dir, string Error, List<string> Created);

    public static CreateResult CreateProject(string parentDir, string name, string group,
        string artifact, string version, string buildSystem, string jdkRelease,
        bool addSampleCode, bool createGitRepo)
    {
        using var d = J(Native.CreateProject(parentDir, name, group, artifact, version,
                                             buildSystem, jdkRelease, addSampleCode, createGitRepo));
        var r = d.RootElement;
        return new CreateResult(Bool(r, "ok"), Str(r, "dir"), Str(r, "error"), Arr(r, "created"));
    }

    public static string DefaultProjectsDir()
    {
        using var d = J(Native.DefaultProjectsDir());
        return d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() : "";
    }

    public static string GuessMain(string root)
    {
        using var d = J(Native.GuessMain(root));
        return d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() : "";
    }

    // ---------------------------------------------------------------- 文件

    public sealed record FileResult(bool Ok, string Text, string Error);

    public static FileResult ReadFile(string path)
    {
        using var d = J(Native.ReadFile(path));
        var r = d.RootElement;
        return new FileResult(Bool(r, "ok"), Str(r, "text"), Str(r, "error"));
    }

    public static bool WriteFile(string path, string text)
    {
        using var d = J(Native.WriteFile(path, text));
        return Bool(d.RootElement, "ok");
    }

    // ---------------------------------------------------------------- 构建

    public static BuildResult Compile(string root, string jdkHome, string classpath, bool verbose = false)
        => ParseBuild(Native.Compile(root, jdkHome, classpath, verbose));

    public static BuildResult Run(string root, string jdkHome, string mainClass, string classpath)
        => ParseBuild(Native.RunProject(root, jdkHome, mainClass, classpath));

    private static BuildResult ParseBuild(string json)
    {
        using var d = J(json);
        var r = d.RootElement;
        var diags = new List<Diagnostic>();
        if (r.TryGetProperty("diags", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in arr.EnumerateArray())
                diags.Add(new Diagnostic(Str(x, "level"), Str(x, "file"), Int(x, "line"),
                                         Int(x, "col"), Str(x, "message")));
        }
        double secs = 0;
        if (r.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number)
            s.TryGetDouble(out secs);
        return new BuildResult(Bool(r, "ok"), Int(r, "exitCode"), Str(r, "tool"),
            Str(r, "output"), Int(r, "errorCount"), Int(r, "warnCount"), secs, diags);
    }

    public static List<Diagnostic> Check(string root)
    {
        using var d = J(Native.Check(root));
        var list = new List<Diagnostic>();
        if (d.RootElement.TryGetProperty("diags", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in arr.EnumerateArray())
                list.Add(new Diagnostic(Str(x, "level"), Str(x, "file"), Int(x, "line"),
                                        Int(x, "col"), Str(x, "message")));
        }
        return list;
    }

    // ---------------------------------------------------------------- 第三方库

    public static List<LibEntry> Libs()
    {
        using var d = J(Native.LibsCatalog());
        var list = new List<LibEntry>();
        if (d.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in d.RootElement.EnumerateArray())
                list.Add(new LibEntry(Str(x, "id"), Str(x, "name"), Str(x, "group"),
                                      Str(x, "artifact"), Str(x, "version"), Str(x, "desc"),
                                      Str(x, "category"), Bool(x, "cached")));
        }
        return list;
    }

    public static string LibsSelection(string root)
    {
        using var d = J(Native.LibsSelection(root));
        return d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() : "";
    }

    public static bool LibsSave(string root, string ids) => Native.LibsSave(root, ids);

    public sealed record ResolveResult(List<string> Jars, List<string> Ready, List<string> Missing);

    public static ResolveResult LibsResolve(string root, string ids)
    {
        using var d = J(Native.LibsResolve(root, ids));
        var r = d.RootElement;
        return new ResolveResult(Arr(r, "jars"), Arr(r, "ready"), Arr(r, "missing"));
    }

    public sealed record DownloadResult(bool Ok, string Path, string Error);

    public static DownloadResult LibsDownload(string id)
    {
        using var d = J(Native.LibsDownload(id));
        var r = d.RootElement;
        return new DownloadResult(Bool(r, "ok"), Str(r, "path"), Str(r, "error"));
    }

    /// <summary>
    /// 带进度的下载。
    ///
    /// 两件事必须同时做到，缺一个就有 bug：
    ///   1) 跑在后台线程上 —— 它是一次同步的 P/Invoke，几十秒不返回都很正常，
    ///      直接在按钮的 Click 里调，界面当下就冻住、标题栏挂着「无响应」。
    ///   2) 委托要活过整个调用 —— 交给 C++ 的是个函数指针，托管委托被 GC 收走后
    ///      那是一个野指针，下载会在半路炸掉（这类崩溃看着像网络问题，其实是 GC）。
    ///      GCHandle.Alloc 把它钉住，finally 里释放。
    /// onProgress 在同一个后台线程上回调，要刷 UI 自己 Dispatcher。
    /// </summary>
    public static Task<DownloadResult> LibsDownloadAsync(string id, Action<long, long> onProgress)
    {
        var handle = default(System.Runtime.InteropServices.GCHandle);
        Native.DownloadProgressCb cb = (done, total, _) =>
        {
            try { onProgress?.Invoke((long)done, (long)total); }
            catch { /* 界面那边的异常不该把下载线程带崩 */ }
            return 1;
        };
        handle = System.Runtime.InteropServices.GCHandle.Alloc(cb);

        return Task.Run(() =>
        {
            try
            {
                using var d = J(Native.LibsDownloadProgress(id, cb));
                var r = d.RootElement;
                return new DownloadResult(Bool(r, "ok"), Str(r, "path"), Str(r, "error"));
            }
            catch (Exception ex)
            {
                // EntryPointNotFoundException 之类的都要在这层消化掉，
                // 否则 await 抛在 UI 线程上，按钮就永远停在「正在下载」。
                return new DownloadResult(false, "", ex.Message);
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        });
    }

    // ---- 仓库查询 / 缓存 / pom
    //
    // 这几个都是「要等网络」或者「要扫盘」的，界面那边一律扔后台线程再调，
    // 别在主线程上直接来。

    public sealed record SearchHit(string Group, string Artifact, string Version)
    {
        public string Coord => Group + ":" + Artifact + ":" + Version;
    }

    public sealed record SearchResult(bool Ok, string Error, List<SearchHit> Hits);

    public static SearchResult LibsRepoSearch(string q)
    {
        using var d = J(Native.LibsSearch(q));
        var r = d.RootElement;
        var hits = new List<SearchHit>();
        if (r.TryGetProperty("hits", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in arr.EnumerateArray())
                hits.Add(new SearchHit(Str(x, "group"), Str(x, "artifact"), Str(x, "version")));
        }
        return new SearchResult(Bool(r, "ok"), Str(r, "error"), hits);
    }

    public sealed record VersionsResult(bool Ok, string Error, List<string> Versions);

    public static VersionsResult LibsRepoVersions(string group, string artifact)
    {
        using var d = J(Native.LibsVersions(group, artifact));
        var r = d.RootElement;
        return new VersionsResult(Bool(r, "ok"), Str(r, "error"), Arr(r, "versions"));
    }

    public sealed record CacheItem(string Coord, string Path, long Size);

    public static List<CacheItem> LibsCacheList()
    {
        using var d = J(Native.LibsCacheList());
        var list = new List<CacheItem>();
        var r = d.RootElement;
        if (r.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in arr.EnumerateArray())
            {
                // size 是 JSON 数字，可能是整数也可能被写成小数，按 double 兜住
                long size = 0;
                if (x.TryGetProperty("size", out var sv))
                {
                    if (sv.ValueKind == JsonValueKind.Number)
                    {
                        if (sv.TryGetInt64(out long iv)) size = iv;
                        else size = (long)sv.GetDouble();
                    }
                }
                list.Add(new CacheItem(Str(x, "coord"), Str(x, "path"), size));
            }
        }
        return list;
    }

    public static bool LibsCacheRemove(string coord) => Native.LibsCacheRemove(coord);

    public static List<string> LibsPomCoords(string root)
    {
        using var d = J(Native.LibsPomCoords(root));
        var list = new List<string>();
        if (d.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var x in d.RootElement.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString());
        return list;
    }

    public static bool LibsPomExists(string root) => Native.LibsPomExists(root);
    public static bool LibsPomAdd(string root, string coord) => Native.LibsPomAdd(root, coord);
    public static bool LibsPomRemove(string root, string g, string a) => Native.LibsPomRemove(root, g, a);

    /// <summary>
    /// 把「目录里的 id」或「完整坐标」统一成坐标。认不出来返回 null。
    /// 老配置里存的是 id（"gson"），新加的自定义依赖只能存坐标，
    /// 两边要能在同一张表里比较，就得先归一。
    /// </summary>
    public static string ToCoord(string idOrCoord)
    {
        if (string.IsNullOrWhiteSpace(idOrCoord)) return null;
        string s = idOrCoord.Trim();
        if (s.Contains(':'))
        {
            var p = SplitCoord(s);
            return p == null ? null : string.Join(":", p);
        }
        var e = Libs().FirstOrDefault(x => x.Id == s);
        return e?.Coord;
    }

    /// <summary>
    /// 这个项目真正生效的依赖（坐标形式，按 group:artifact 去重）。
    ///
    /// 两个来源，pom 说了算：
    ///   pom.xml           —— Maven 工程，依赖本来就该写在这儿
    ///   .javastudio/libs.txt —— 没有 pom 的项目（纯 javac）靠它记
    /// 两个都记是为了「本机没装 mvn、构建退回 javac」那条路也能拿到 jar；
    /// 真跑 mvn 的时候它只认 pom，libs.txt 不参与，所以不会重复。
    /// </summary>
    public static List<string> LibsEffective(string root)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        void Take(string idOrCoord)
        {
            string coord = ToCoord(idOrCoord);
            if (coord == null) return;
            var p = SplitCoord(coord);
            if (p == null) return;
            byKey[p[0] + ":" + p[1]] = coord;
        }

        // 先 libs.txt 再 pom：pom 后写，同名的以 pom 的版本为准。
        foreach (var id in (LibsSelection(root) ?? "")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
            Take(id);
        foreach (var c in LibsPomCoords(root)) Take(c);

        return byKey.Values.ToList();
    }

    /// <summary>"group:artifact:version" 三段。写不出来（空段）返回 null。</summary>
    public static string MakeCoord(string g, string a, string v)
    {
        if (string.IsNullOrWhiteSpace(g) || string.IsNullOrWhiteSpace(a) ||
            string.IsNullOrWhiteSpace(v)) return null;
        return g.Trim() + ":" + a.Trim() + ":" + v.Trim();
    }

    /// <summary>从一个坐标里拆出三段。不是合法坐标返回 null。</summary>
    public static string[] SplitCoord(string coord)
    {
        if (string.IsNullOrWhiteSpace(coord)) return null;
        var p = coord.Trim().Split(':');
        if (p.Length != 3) return null;
        for (int i = 0; i < 3; i++)
        {
            p[i] = p[i].Trim();
            if (p[i].Length == 0) return null;
        }
        return p;
    }

    // ---------------------------------------------------------------- 设置 / 最近

    public static string Setting(string key)
    {
        using var d = J(Native.SettingsGet(key));
        return d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() : "";
    }

    public static void SetSetting(string key, string value) => Native.SettingsSet(key, value);

    public static List<string> Recent()
    {
        using var d = J(Native.Recent());
        var list = new List<string>();
        if (d.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var x in d.RootElement.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString());
        return list;
    }

    public static void RecentAdd(string root) => Native.RecentAdd(root);
    public static void RecentClear() => Native.RecentClear();

    /// <summary>把一条从「最近项目」里划掉（项目被删掉时用）。
    /// 吞掉异常：最近列表只是顺手维护，删文件才是大事；
    /// 之前 js_recent_remove 没导出导致这里抛 EntryPointNotFoundException，
    /// 把后面的 ShowHome() 一起带崩，主页删完不刷新。这里兜底，刷新永远发生。</summary>
    public static void RecentRemove(string root)
    {
        try { Native.RecentRemove(root); } catch { }
    }

    // ---------------------------------------------------------------- 版本

    /// <summary>
    /// 内核 DLL 自报的版本号。空串表示没问到（老内核没编进 js_version）。
    ///
    /// 不在这里写死任何数字：值是构建时 -DJS_VERSION 编进 DLL 的，
    /// 换了个内核文件，问回来的就是那个文件的版本 —— 显示的永远是真的。
    /// </summary>
    public static string CoreVersion()
    {
        try
        {
            using var d = J(Native.Version());
            var v = Str(d.RootElement, "version");
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        catch { /* 老内核没有这个导出：EntryPointNotFoundException，退回文件信息 */ }

        // 兜底：读磁盘上那个 DLL 的文件版本。它今天没有版本资源（clang 直接
        // -shared 出来的，没编 .rc），所以八成也是空的 —— 那就如实说「未标注」，
        // 总比在界面里编一个数字强。
        try
        {
            string dll = Path.Combine(ExeDir, "javastudio_core.dll");
            if (File.Exists(dll))
            {
                var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll);
                if (!string.IsNullOrWhiteSpace(fv.FileVersion)) return fv.FileVersion;
            }
        }
        catch { }
        return "";
    }

    // ---------------------------------------------------------------- 异步
    //
    // 编译、下载会跑好几秒。直接在 UI 线程调，界面就冻在那儿了。

    public static Task<T> OnWorker<T>(Func<T> f)
        => Task.Run(() => { try { return f(); } catch (Exception ex) { Log("ERROR", ex.ToString()); return default; } });

    public static void Log(string level, string msg) => OnLog?.Invoke(level, msg);
}
