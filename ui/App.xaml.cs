// App.xaml.cs —— 程序入口
//
// 启动顺序很关键：先 Init 核心（JDK 扫描、日志），再建窗口。
// 顺序反了的话，窗口第一帧拿不到 JDK 信息，状态栏会先闪一下「无 JDK」。

using System;
using System.Windows;

namespace JavaStudio;

public partial class App : Application
{
    // 标记主窗口是否已经显示：未显示就说明是启动期崩溃，必须弹窗让用户看到，
    // 否则进程闷声退出/卡着，用户只剩「打不开、没报错」一头雾水。
    private static bool _mainShown = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Core.Init();
        }
        catch (Exception ex)
        {
            // 核心 DLL 不在或者缺依赖，这时候说什么都晚了，
            // 直接告诉用户，别让他对着一个空白窗口猜。
            AppMsg.Show(null, Lang.T("app.name"), Lang.T("err.dllLoadFail") + "\n\n" + ex.Message +
                "\n\n" + Lang.T("err.dllHint"));
            Shutdown(1);
            return;
        }

        // 崩溃别闷着：把异常写进日志；启动期崩溃再弹窗，不然只能看到进程无声无息地消失
        // （_mainShown 是类级字段，见文件顶部）
        DispatcherUnhandledException += (_, ex) =>
        {
            Crash(ex.Exception);
            if (!_mainShown)
            {
                // 启动阶段崩了：把错误摆出来（详情已经写进日志文件）
                try
                {
                    AppMsg.Show(null, Lang.T("err.bootTitle"),
                        Lang.T("err.bootBody") + "\n" +
                        System.IO.Path.Combine(Core.AppDataDir, "javastudio-error.txt") + "\n\n" +
                        Truncate(ex.Exception?.ToString() ?? "unknown", 1800));
                }
                catch { }
                Shutdown(1);
                return;
            }
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Crash(ex.ExceptionObject as Exception);

        Icons.Load();
        Config.Load();
        // 右键菜单不要了：启动时把以前注册过的那几项从 HKCU 里删掉（幂等，键不在就跳过）
        Shell.UnregisterContextMenu();
        Theme.Apply(Config.Theme == "dark");

        var win = new MainWindow();
        // 命令行可以带一个项目目录，双击 .java 或者从资源管理器拖过来时用
        if (e.Args.Length > 0 && System.IO.Directory.Exists(e.Args[0]))
            win.OpenProject(e.Args[0]);
        // 自检用：带着 JS_SHOT 截图时，顺手把主类文件打开，
        // 这样截出来的图能看到标签页和面包屑（正常的双击路径截不到）。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JS_OPEN_MAIN"))
            && System.IO.Directory.Exists(e.Args.Length > 0 ? e.Args[0] : ""))
            win.OpenMainSource();
        // JS_OPEN_N=n 打开前 n 个 .java，用来看标签条（正常双击路径截不到多个标签）
        string n = Environment.GetEnvironmentVariable("JS_OPEN_N");
        if (int.TryParse(n, out int count) && count > 0 && e.Args.Length > 0)
            win.OpenFirstJavaFiles(count);
        // JS_OPEN_FILE=<绝对路径> 打开指定的那一个文件。
        // 已有的 JS_OPEN_N 只挑 .java，照不到 md 那套「编辑/拆分/预览」。
        string openFile = Environment.GetEnvironmentVariable("JS_OPEN_FILE");
        if (!string.IsNullOrEmpty(openFile))
            win.OpenFileForTest(openFile, Environment.GetEnvironmentVariable("JS_MD_MODE"));
        // JS_COMPILE=1 打开后自动编译一次，用来看「输出/问题/波浪线」
        if (Environment.GetEnvironmentVariable("JS_COMPILE") == "1" && e.Args.Length > 0)
            win.TriggerCompileForTest();
        win.Show();
        _mainShown = true;   // 窗口出来了，之后再有异常就只记日志、不再弹启动失败窗
        // JS_MAX=1 启动后最大化，用来看标题栏那个「还原」图标和最大化下的布局。
        // 必须放在 Show() 之后：走的是和用户点最大化按钮同一条路径
        // （WindowState 变更 -> StateChanged -> UpdateMaxGlyph 重建按钮），
        // Show() 之前设就绕过这条路径了，等于没测。
        if (Environment.GetEnvironmentVariable("JS_MAX") == "1")
            win.WindowState = WindowState.Maximized;
        // 下面这两个要开对话框的钩子必须放在 Show() 之后：
        // Owner 还没显示过就 Show/ShowDialog 会直接炸（上次截图全 0 就栽在这）。
        // JS_NEWPROJ="名称|包名" 直接打开新建项目对话框并填好，验证实时校验
        string np = Environment.GetEnvironmentVariable("JS_NEWPROJ");
        if (!string.IsNullOrEmpty(np))
            win.ShowNewProjectForTest(np);
        // JS_NEWITEM="类型|名字" 不弹窗直接建一个条目，结果写 JS_ITEM_OUT。
        // 验证「后缀自动补 / 自定义后缀要自己写 / 示例代码跟类型对得上」。
        string nis = Environment.GetEnvironmentVariable("JS_NEWITEM");
        if (!string.IsNullOrEmpty(nis))
            win.CreateItemForTest(nis);
        // JS_CHECK=1 跑一遍「检查」并停到「问题」页，看诊断列表记没记进去
        if (Environment.GetEnvironmentVariable("JS_CHECK") == "1" && e.Args.Length > 0)
            win.TriggerCheckForTest();
        // JS_BOTTOM=0|1|2 直接切到底部某一页（截图用）
        if (int.TryParse(Environment.GetEnvironmentVariable("JS_BOTTOM"), out int bottomPage))
            win.ShowBottomForTest(bottomPage);
        // JS_AGENT=1 打开对话面板（默认是收起的竖条），验证里面的思考过程折叠块
        if (Environment.GetEnvironmentVariable("JS_AGENT") == "1")
            win.OpenAgentForTest();
        // JS_TREE_MENU=dir|file|blank 弹出项目树右键菜单（值决定菜单里的目标是谁）。
        // 菜单是独立的 Popup，主窗口那张 JS_SHOT 截图照不到它，得配整屏抓。
        string treeMenu = Environment.GetEnvironmentVariable("JS_TREE_MENU");
        if (!string.IsNullOrEmpty(treeMenu))
            win.ShowTreeMenuForTest(treeMenu);
        // JS_DARK=1 启动后强制切到深色主题（验证普通代码文字从黑变白）
        if (Environment.GetEnvironmentVariable("JS_DARK") == "1")
        {
            if (Config.Theme != "dark") Theme.Toggle();
            win.ApplyThemeToEditors();
        }
        // JS_SHOT 的处理在 MainWindow 构造函数里（见 MainWindow.ShotWhenReady）。
        // JS_DLG=settings|newproj|about|libs|agentapi 打开对应对话框并单独给它截图
        // （JS_DLG_SHOT=路径）。对话框是独立窗口，主窗口那张截图照不到它。
        // 第三方库那个对话框要一个项目目录才看得出东西，用 JS_ROOT 指定。
        string dlgKind = Environment.GetEnvironmentVariable("JS_DLG");
        if (!string.IsNullOrEmpty(dlgKind))
        {
            Window d = dlgKind switch
            {
                "settings" => new SettingsDialog(),
                "newproj"  => new NewProjectDialog(),
                "about"    => new AboutDialog(),
                "newitem"  => new NewItemDialog(Environment.GetEnvironmentVariable("JS_ROOT")
                                            ?? Environment.CurrentDirectory),
                "libs"     => new LibsDialog(Environment.GetEnvironmentVariable("JS_ROOT")
                                            ?? Environment.CurrentDirectory),
                // API 设置（Agent 的齿轮）。模型那一栏是可编辑下拉框，
                // 单独留个入口方便验证「选中的模型显示得出来、保存得住」。
                "agentapi" => new AgentSettingsDialog(),
                _          => null
            };
            if (d != null)
            {
                d.Owner = win;
                string shot = Environment.GetEnvironmentVariable("JS_DLG_SHOT") ?? "dlg.png";

                // 新建条目：JS_ITEM_KIND=n 选类型，JS_ITEM_LOCK=1 模拟「右键菜单里
                // 已经点明了类型」（下拉应该变灰），JS_ITEM_NAME=xxx 预填名字。
                if (d is NewItemDialog ni)
                {
                    if (int.TryParse(Environment.GetEnvironmentVariable("JS_ITEM_KIND"), out int kind))
                        ni.SetKind(kind, Environment.GetEnvironmentVariable("JS_ITEM_LOCK") == "1");
                    string nm = Environment.GetEnvironmentVariable("JS_ITEM_NAME");
                    if (!string.IsNullOrEmpty(nm)) ni.SetNameForTest(nm);
                }

                if (d is DlgPanel p && p.IsEmbedded)
                {
                    // ⚠️ 内嵌页不能再走 d.Show()：它的内容已经不在自己身上了
                    // （DlgHost 把它摘到主窗口那一页里），Show() 出来是个空壳，
                    // 截出来的图全白。正确做法是走 ShowDialog()（那个 new 出来的、
                    // 会把内容挂进主窗口并停住的版本），然后截**主窗口**。
                    //
                    // 顺序：先把截图那个延时任务起起来，再进 ShowDialog() ——
                    // 它虽然阻塞，但内部是 PushFrame，消息循环照跑，延时任务照样到点。
                    win.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _ = ShotAfterDelay(win, shot);   // 不等它，下面要进阻塞的 ShowDialog
                        p.ShowDialog();
                    }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
                else
                {
                    ShotDialogForTest(d, shot);
                    d.Show();
                }

                // JS_AGENT_MODEL=xxx 时，真的模拟一次「改模型 + 点保存」，
                // 用来验证可编辑下拉框的 Text 读得回来、模型存得进去。
                //
                // ⚠️ 两个坑都在这儿：
                //  1) 不能直接写在 Show() 后面 —— 那时模板还没应用，
                //     PART_EditableTextBox 还不存在，设 Text 会被丢掉；
                //  2) 也不能用 d.Loaded += —— Loaded 在 Show() 期间就已经触发了，
                //     等在 Show() 之后才订阅，永远等不到。
                // 所以：等一帧（ContextIdle 比 Loaded 更晚），那时候布局跑完了、
                // 构造里那个 `_model.Text = _modelInit` 也已经执行过，
                // 我们再覆盖成要测的值并保存。
                string am = Environment.GetEnvironmentVariable("JS_AGENT_MODEL");
                if (d is AgentSettingsDialog ad && !string.IsNullOrEmpty(am))
                    win.Dispatcher.BeginInvoke(
                        new Action(() => ad.SaveWithModelForTest(am)),
                        System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
        }
    }

    /// <summary>自检用：窗口出来之后单独给它拍一张 PNG。</summary>
    private static void ShotDialogForTest(Window d, string path)
        => d.Loaded += async (_, _) => { await ShotAfterDelay(d, path); };

    /// <summary>
    /// 等一会儿，然后把这个窗口渲染成 PNG。
    ///
    /// ⚠️ 默认只等 1.5 秒 —— 那是给「静态」对话框用的。凡是带网络请求的
    /// （第三方库那页搜 Maven 仓库），1.5 秒拍到的全是「正在加载」，后面等
    /// 多久都没用：快照就那一张。这种要用 JS_DLG_SHOT_DELAY 拉长。
    /// </summary>
    private static async System.Threading.Tasks.Task ShotAfterDelay(Window d, string path)
    {
        int wait = 1500;
        if (int.TryParse(Environment.GetEnvironmentVariable("JS_DLG_SHOT_DELAY"),
                         out int v) && v > 0) wait = v;
        await System.Threading.Tasks.Task.Delay(wait);
        try
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)d.ActualWidth, (int)d.ActualHeight, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(d);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(path);
            enc.Save(fs);
        }
        catch { }
    }

    /// <summary>把长文本截到 max 个字符，崩溃弹窗里用，避免一句话把对话框撑爆。</summary>
    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        return s.Length <= max ? s : s.Substring(0, max) + "\n" + Lang.T("err.seeLog");
    }

    /// <summary>
    /// 崩了要留下证据。之前只写 C++ 那份日志，wWinMain 里没初始化的窗口
    /// 崩在托管异常上时啥都看不到，进程就那么没了——查了半天。
    /// </summary>
    private static void Crash(Exception ex)
    {
        string text = ex?.ToString() ?? "unknown exception";
        try { Core.Log("ERROR", text); } catch { }
        try
        {
            System.IO.File.AppendAllText(
                // 崩了也要把证据留在数据目录里（默认 D:\S++\.javastudio），
                // 不能写 %TEMP% —— 那在 C 盘，违反「C 盘不留文件」。
                System.IO.Path.Combine(Core.AppDataDir, "javastudio-error.txt"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n" + text + "\n\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Core.Shutdown();
        base.OnExit(e);
    }
}
