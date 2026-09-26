// DlgHost.cs —— 把对话框做成「主窗口里的弹窗」
//
// 背景：设置、关于、新建项目、第三方库这些原来都是独立的小窗口
// （`Window` + `ShowDialog()`）。独立窗口有两个毛病：
//   1. 拖动/缩放主窗口时它不跟着动，跑着跑着就飘到主窗口外面去了；
//   2. 用户想让内容跟着主窗口大小走，没地方说这个话。
//
// 所以改成：对话框不再是自己的窗口，而是**主窗口里的一个弹窗卡片**——工作区上
// 压一层半透明遮罩，中间一张圆角卡片，有关闭按钮（见 MainWindow.xaml 的
// DlgHostLayer）。
//
// ⚠️ 试过一版「铺满工作区、只留一条顶栏」的整页形态，用户否掉了：那看着是全屏
// 换了个界面，不像弹窗，一眼看不出「这是个临时开的东西，关掉就回去了」。
// 卡片尺寸跟着主窗口缩放（主窗口小它就小），但不会无脑铺满。
//
// 怎么做到「调用点一行都不用改」：
//   `DlgPanel` 这个基类**仍然是 Window**。调用点写的
//       new SettingsDialog { Owner = this }.ShowDialog()
//   一个字都不用动。区别在内部：
//   - 真正弹窗时，照旧走 `Chrome.ApplyDialog` 那套（自己一个窗口）；
//   - 内嵌时，`ShowDialog()` 不再真的开窗口，而是把自己的内容交给
//     `DlgHost.Show(...)`，然后**阻塞等**这个弹窗被关掉（关闭 / 确定 / 取消），
//     最后回一个和真 `ShowDialog()` 一样的 `bool?`。
//
// ⚠️ 两个必须守住的点：
//
//  1. **阻塞不能在 UI 线程上忙等。** `ShowDialog()` 是同步调用，而内容要在
//     UI 线程上显示。用 `Dispatcher.PushFrame` 才是「在这儿停住、但消息循环
//     照跑」的唯一正确姿势 —— 换成 `Thread.Sleep`/`SpinWait` 会直接死锁，
//     界面一动不动。这是 WPF 里实现同步对话框的标准手段。
//
//  2. **`DialogResult` 不是随便能设的。** WPF 只在「用 ShowDialog() 打开的
//     窗口」上允许设它，内嵌时我们没真的开窗口，设它会抛 InvalidOperationException。
//     所以统一走 `CloseWith(bool)`：能设就设，不能设就直接结束这一页。

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace JavaStudio;

/// <summary>
/// 内嵌对话框的宿主：管一页的显示、返回、以及把结果还给调用方。
///
/// 一次只显示一页（同时开两个设置页没有意义，而且返回该回哪儿会变成一团乱麻）。
/// </summary>
internal static class DlgHost
{
    private static MainWindow _win;
    private static DlgPanel _cur;                 // 正在显示的那一页
    private static DispatcherFrame _frame;        // 撑着 ShowDialog() 那个「停住」
    private static bool _closing;                 // 防重入：返回按钮和按钮连点

    internal static void Attach(MainWindow w) => _win = w;

    internal static bool Busy => _cur != null;

    /// <summary>
    /// 显示一个弹窗并停在这儿，直到它被关掉。
    ///
    /// 返回值跟 `Window.ShowDialog()` 一个意思：`true` = 点了确定，
    /// `false`/`null` = 取消、关闭、或中途被别的弹窗顶掉。
    /// </summary>
    internal static bool? Show(DlgPanel dlg)
    {
        if (_win == null) return null;

        // 已经有一个开着：先把旧的按「取消」收掉，不然它的 ShowDialog() 永远不返回，
        // 调用方那个栈帧就卡死在那儿了（内存里留一条断头路）。
        if (_cur != null)
        {
            if (ReferenceEquals(_cur, dlg)) return null;
            Close(_cur, null);
        }

        _cur = dlg;
        _closing = false;

        // 不给关闭按钮 = 必须做个决定才能走（比如还没配 API 时的必填模式）
        _win.DlgClose.Visibility = dlg.AllowBack ? Visibility.Visible : Visibility.Collapsed;
        SizeCard(dlg);

        _win.DlgHostTitle.Text = dlg.Title;
        if (dlg.Tag is string key && !string.IsNullOrEmpty(key))
        {
            _win.DlgHostTitle.Tag = key;      // 切语言时跟着变
            _win.DlgHostTitle.Text = Lang.T(key);
        }
        else _win.DlgHostTitle.Tag = null;

        // 内容从原来那个窗口里摘出来挂到这儿。
        // ⚠️ 必须先断开：一个 UIElement 只能有一个逻辑父级，
        // 直接赋给新的父级会抛「指定的元素已经是另一个元素的逻辑子元素」。
        var body = dlg.TakeBody();
        var slot = dlg.OwnScroll ? _win.DlgHostFill : _win.DlgHostBody;
        _win.DlgHostBody.Content = null;
        _win.DlgHostFill.Content = null;
        _win.DlgHostScroll.Visibility = dlg.OwnScroll ? Visibility.Collapsed : Visibility.Visible;
        _win.DlgHostFill.Visibility = dlg.OwnScroll ? Visibility.Visible : Visibility.Collapsed;
        slot.Content = body;

        _win.DlgHostLayer.Visibility = Visibility.Visible;
        ScrollProbeForTest();
        // 每次进来都回到顶部。不重置的话，上一个对话框滚到中间，
        // 下一个打开就莫名其妙停在半截。
        _win.DlgHostScroll.ScrollToTop();

        // ⚠️ 相当于原来 `Loaded += ...`。内嵌时这个 Window 自己不会被 Show()，
        // Loaded 永远不触发，所以「打开后自动干点什么」只能挂在这儿。
        try { dlg.OnShown(); } catch { /* 自检钩子出错不该拖垮整页 */ }

        // 焦点给到内容里第一个能输入的控件，跟真弹窗的手感一致。
        var focus = dlg.InitialFocus;
        if (focus != null) _win.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { focus.Focus(); } catch { /* 控件还没进视觉树就算了 */ }
        }), DispatcherPriority.Loaded);

        // 自检：把这一页的文字抄一份（看文案对不对，比截图好断言）。
        // ⚠️ 必须延到布局跑完之后：内容刚塞进 ContentControl 时，它的可视子节点
        // 还没建出来，这时候遍历视觉树只会拿到一条标题栏（踩过）。
        _win.Dispatcher.BeginInvoke(
            new Action(() => MainWindow.DumpTextsForTest(_win.DlgHostLayer, "dlg:" + dlg.Title)),
            DispatcherPriority.Loaded);

        // PushFrame：在这儿停住，但消息循环照跑（否则界面直接冻住）。
        // 想让这一页结束，唯一的路是 Close() 把 frame.Continue 置 false。
        _frame = new DispatcherFrame();
        Dispatcher.PushFrame(_frame);
        _frame = null;

        bool? result = dlg.EmbeddedResult;
        _cur = null;
        return result;
    }

    /// <summary>
    /// 卡片尺寸跟着工作区算：工作区小它就小，大到超过卡片自己的理想尺寸
    /// 就按理想尺寸来。
    ///
    /// ⚠️ 可用高度**必须按 DlgHostLayer 那个区域量，不能拿主窗口的尺寸减一个数**。
    /// 主窗口高度里还含菜单栏 + 工具栏 + 状态栏（一共 96px），卡片并不在那儿。
    /// 早先写成 `_win.ActualHeight - 80`，1040x700 的窗口算出「可用 620」，
    /// 可工作区实际只有 604 —— 卡片比装它的盒子还高，底下那段（正好是「应用」
    /// 「取消」那排按钮）直接被切掉，用户看到的就是「小窗口下弹窗不适配」。
    ///
    /// ⚠️ 高度有三种情况，别写混：
    ///   - `WantHeight` 有值 → 钉死（第三方库那种左右两栏，不钉就会一边高一边矮）；
    ///   - `WantHeight` 是 NaN → **不设 Height**，让 Grid 里那个 `*` 行按内容退化，
    ///     卡片贴着内容长（设置、关于这种，不然下面空一大块）；
    ///   - 不管哪种，**MaxHeight 都要设**：内容比工作区高的时候靠它压住卡片，
    ///     Grid 第二行拿到有限高度，里面那层 ScrollViewer 才会真的滚起来。
    ///
    /// ⚠️ `OwnScroll` 的弹窗必须拿到一个**确定的**高度。它自己内部就有滚动区，
    /// 内容被直接塞进卡片第二行（不套 ScrollViewer），那一行有多高它就多高 ——
    /// Height 留 NaN 的话第二行跟着内容长，里层的列表又回到「无限高、永不滚」。
    /// </summary>
    private static void SizeCard(DlgPanel dlg)
    {
        double hostW = HostWidth(), hostH = HostHeight();

        // 留白**按比例**给，不能固定减一个数（固定减 80 的话窗口一小边距还占 80，
        // 比例上反而显得更小）。宽度和高度分开给，因为两者的"不够看"程度不一样：
        //
        //   宽度 0.82：实测 1040x700 的窗口工作区是 1040x638，0.82 算出来 853 宽，
        //     左右各留 94px。再宽就又变成「几乎铺满」，用户会当成没适配。
        //
        //   高度 0.88：高度留白不能跟宽度一样狠。0.82 会把设置页压到 523 高，
        //     而它内容要 547 —— 明明窗口够大却被迫滚一下，很别扭。
        //     0.88 给到 561，设置页正好装下；再高的表单（新建项目）该滚就滚。
        double availW = Math.Max(hostW * 0.82, 200);
        double availH = Math.Max(hostH * 0.88, 140);

        _win.DlgCard.Width = Math.Min(dlg.WantWidth, availW);
        _win.DlgCard.MaxWidth = availW;
        _win.DlgCard.MaxHeight = availH;
        // 自己管滚动的：高度必须写死，不然第二行跟着内容长，里层又没得滚了
        _win.DlgCard.Height =
            double.IsNaN(dlg.WantHeight) && !dlg.OwnScroll
                ? double.NaN
                : Math.Min(double.IsNaN(dlg.WantHeight) ? availH : dlg.WantHeight, availH);

        WriteRectForTest(dlg, hostW, hostH, availW, availH);
    }

    /// <summary>
    /// 遮罩所在那块区域（= 主窗口的工作区）的宽度。
    ///
    /// 拿 Workbench 而不是 DlgHostLayer：DlgHostLayer 在没打开弹窗时是
    /// Collapsed，ActualWidth 是 0，量不到东西。两者尺寸一样
    /// （DlgHostLayer 铺满 Workbench，没设 Margin）。
    /// </summary>
    private static double HostWidth()
    {
        if (_win.Workbench.ActualWidth > 0) return _win.Workbench.ActualWidth;
        // 布局还没跑过（窗口刚建、还没显示）时的兜底估计
        return Math.Max(_win.ActualWidth - 96, 240);
    }

    private static double HostHeight()
    {
        if (_win.Workbench.ActualHeight > 0) return _win.Workbench.ActualHeight;
        return Math.Max(_win.ActualHeight - 96, 180);   // 菜单 30 + 工具栏 42 + 状态栏 24
    }

    /// <summary>
    /// 自检用：JS_DLG_RECT=路径 时把这次算出来的尺寸和卡片的实际落脚点写进文件。
    ///
    /// 为什么需要：卡片是 WPF 内部元素，从 Win32 那边量不到；光看截图又分不清
    /// 「卡片算小了」和「卡片算对了但被裁了」。把布局的原始数字打出来最省事。
    /// 正式运行不设这个环境变量，什么都不写。
    /// </summary>
    private static void WriteRectForTest(DlgPanel dlg, double hostW, double hostH,
                                         double availW, double availH)
    {
        string path = Environment.GetEnvironmentVariable("JS_DLG_RECT");
        if (string.IsNullOrEmpty(path)) return;

        // 尺寸要等布局跑完才准，所以延到 Loaded 优先级再量一遍
        _win.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var card = _win.DlgCard;
                string where = "?";
                if (card.ActualWidth > 0)
                {
                    var p = card.TransformToAncestor(_win.Workbench).Transform(new Point(0, 0));
                    where = string.Format("left={0:F0} top={1:F0} right={2:F0} bottom={3:F0}",
                                          p.X, p.Y, p.X + card.ActualWidth, p.Y + card.ActualHeight);
                }
                File.WriteAllText(path, string.Join("\n", new[]
                {
                    "title=" + dlg.Title,
                    string.Format("window={0:F0}x{1:F0}", _win.ActualWidth, _win.ActualHeight),
                    string.Format("workbench={0:F0}x{1:F0}", _win.Workbench.ActualWidth, _win.Workbench.ActualHeight),
                    string.Format("host={0:F0}x{1:F0}", hostW, hostH),
                    string.Format("avail={0:F0}x{1:F0}", availW, availH),
                    string.Format("want={0:F0}x{1}", dlg.WantWidth, dlg.WantHeight),
                    string.Format("card_set={0:F0}x{1}",
                                  double.IsNaN(card.Width) ? -1 : card.Width,
                                  double.IsNaN(card.Height) ? "NaN" : card.Height.ToString("F0")),
                    string.Format("card_actual={0:F0}x{1:F0}", card.ActualWidth, card.ActualHeight),
                    "card_at=" + where,
                }));
            }
            catch { /* 自检写不出来不该影响弹窗 */ }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 自检用：JS_DLG_SCROLL=路径 时把弹窗里每一层 ScrollViewer 的读数写进文件。
    ///
    /// 为什么需要：弹窗里套了不止一层 ScrollViewer 时，「滚轮究竟动的是哪一层」
    /// 光看代码看不出来。每一层都写着 `VerticalScrollBarVisibility=Auto`，但
    /// **真正能滚的只有高度被限制住的那几层** —— 哪一层没拿到受限高度，它就
    /// 把内容原样撑开、自己一像素都不滚，滚轮越过它落到外面那一层。
    /// 界面上的表现就是「中间那条列表怎么拨都不动，整张卡片跟着晃」。
    ///
    /// 把 view / extent / scrollable / offset 和它在屏幕上的位置打出来，
    /// 再配 WS_MOUSEWHEEL 真滚一下看哪个 offset 变了，比对着布局推演快得多。
    /// 正式运行不设这个环境变量，什么都不写。
    /// </summary>
    private static bool _scrollProbeHooked;

    private static void ScrollProbeForTest()
    {
        string path = Environment.GetEnvironmentVariable("JS_DLG_SCROLL");
        if (string.IsNullOrEmpty(path)) return;
        try { File.WriteAllText(path, ""); } catch { return; }

        // PreviewMouseWheel 是隧道路由，比真正的处理更早；直接在这儿读 offset
        // 读到的是「还没滚」的旧值，所以延到 Loaded 优先级再读一遍。
        if (!_scrollProbeHooked)
        {
            _scrollProbeHooked = true;
            _win.DlgCard.PreviewMouseWheel += (_, _) =>
                _win.Dispatcher.BeginInvoke(
                    new Action(() => DumpScrollers(path, "after-wheel")),
                    DispatcherPriority.Loaded);
        }
        _win.Dispatcher.BeginInvoke(
            new Action(() => DumpScrollers(path, "opened")),
            DispatcherPriority.Loaded);
    }

    private static void DumpScrollers(string path, string tag)
    {
        try
        {
            var lines = new List<string>
            {
                "=== " + tag,
                string.Format("card={0:F0}x{1:F0}",
                              _win.DlgCard.ActualWidth, _win.DlgCard.ActualHeight),
            };
            try
            {
                var c = _win.DlgCard.PointToScreen(
                    new Point(_win.DlgCard.ActualWidth / 2, _win.DlgCard.ActualHeight / 2));
                lines.Add(string.Format("card_screen_center=({0:F0},{1:F0})", c.X, c.Y));
            }
            catch { /* 还没进视觉树 */ }
            WalkScrollers(_win.DlgCard, lines);
            File.AppendAllText(path, string.Join("\n", lines) + "\n");
        }
        catch { /* 自检写不出来不该影响弹窗 */ }
    }

    private static void WalkScrollers(DependencyObject node, List<string> lines)
    {
        if (node is ScrollViewer sv)
        {
            // view：这一层实际能看见多高；extent：内容多高。
            // 两者相等 = 这一层拿到的高度是无限的、它自己根本不会滚。
            string who = ReferenceEquals(sv, _win.DlgHostScroll)
                ? "OUTER(整弹窗)"
                : "inner<" + (sv.Content?.GetType().Name ?? "-") + ">";
            string at = "?";
            try
            {
                var p = sv.PointToScreen(new Point(sv.ActualWidth / 2, sv.ActualHeight / 2));
                at = string.Format("screen_center=({0:F0},{1:F0})", p.X, p.Y);
            }
            catch { /* 还没进视觉树 */ }
            lines.Add(string.Format("  {0} {1} size={2:F0}x{3:F0} view={4:F0} " +
                                    "extent={5:F0} scrollable={6:F0} offset={7:F0}",
                                    who, at, sv.ActualWidth, sv.ActualHeight,
                                    sv.ViewportHeight, sv.ExtentHeight,
                                    sv.ScrollableHeight, sv.VerticalOffset));
        }

        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            WalkScrollers(VisualTreeHelper.GetChild(node, i), lines);
    }

    /// <summary>结束当前这个弹窗。<paramref name="result"/> 为 null 表示「关闭/取消」。</summary>
    internal static void Close(DlgPanel dlg, bool? result)
    {
        if (!ReferenceEquals(_cur, dlg)) return;
        if (_closing) return;              // 连点两下关闭会走到这儿
        _closing = true;

        dlg.EmbeddedResult = result;

        _win.DlgHostBody.Content = null;
        _win.DlgHostFill.Content = null;
        _win.DlgHostLayer.Visibility = Visibility.Collapsed;

        // ⚠️ 顺序不能反：先让 PushFrame 那个循环退出，再回到调用方。
        // Frame 没停就往下走的话，Show() 里读 EmbeddedResult 读到的是旧值。
        var f = _frame;
        if (f != null) f.Continue = false;
    }

    /// <summary>主窗口尺寸变了，卡片要跟着重算一遍。</summary>
    internal static void OnResized()
    {
        if (_cur != null) SizeCard(_cur);
    }

    /// <summary>关闭按钮 / 点遮罩：把当前这个弹窗按「取消」收掉。没开着就什么都不做。</summary>
    internal static void CloseNothing()
    {
        if (_cur != null) Close(_cur, false);
    }
}

/// <summary>
/// 所有对话框的基类。
///
/// 它**仍然是 Window**（调用点不用改），但有两种工作模式：
///   - 内嵌（默认）：内容交给 <see cref="DlgHost"/>，显示在主窗口工作区里；
///   - 独立窗口：`Popout = true` 时退回原来那套，自己开一个窗口。
/// </summary>
internal abstract class DlgPanel : Window
{
    private UIElement _body;

    /// <summary>内嵌时这一页的结果（true=确定，false/null=取消）。</summary>
    internal bool? EmbeddedResult;

    /// <summary>
    /// 现在是「嵌在主窗口里」而不是「自己一个窗口」。
    /// 自检代码要读它来决定截谁：内嵌页的内容在主窗口上，截那个空壳 Window
    /// 只能拍到一张空白图。
    /// </summary>
    internal bool IsEmbedded => !Popout;

    /// <summary>
    /// 左上角关闭按钮（和点遮罩）要不要给。
    /// 默认给（关闭 = 取消）。置 false 用于「必须选一个才能走」的场合 ——
    /// 比如还没配 API 时弹出来的必填模式，给个关闭就能绕过去，等于没拦住。
    /// </summary>
    internal bool AllowBack { get; set; } = true;

    /// <summary>
    /// 这个弹窗「最舒服」的宽度。卡片实际宽度是它和主窗口可用宽度里取小的那个，
    /// 所以主窗口一缩小卡片会跟着缩，主窗口放大到一定程度卡片就不再长了。
    /// </summary>
    internal double WantWidth { get; set; } = 560;

    /// <summary>
    /// 理想高度。留 NaN = 「内容有多高就多高（但不许超出主窗口）」，
    /// 适合设置、关于这种跟着内容走的弹窗；第三方库那种两栏的要写死一个数，
    /// 不然两栏会被内容撑得一边高一边矮。
    /// </summary>
    internal double WantHeight { get; set; } = double.NaN;

    /// <summary>
    /// 这一页自己内部就有滚动区，外层别再套 ScrollViewer。
    ///
    /// 默认值 false（外面那层 ScrollViewer 兜着整个页面滚动，绝大多数弹窗这样最省事）。
    /// 置 true 的情况：页面里有「只有这一块该滚」的区域 ——
    /// 比如第三方库那个双栏，要滚的是中间两条列表，标题栏、左侧导航、
    /// 下面的「应用 / 取消」都得钉在那儿。
    ///
    /// ⚠️ 留 false 会让整页滚起来：ScrollViewer 给子元素的是**无限高**，
    /// 里面那条列表就被撑到内容的自然高度（3948px），它自己的 ScrollViewer
    /// view == extent、永远不滚，滚轮越过它落到外层的 ScrollViewer 上。
    /// 结果是「拨列表没反应，整张卡片跟着晃」，而且光看布局推演不出来，
    /// 得把每层的 extent 打出来才看得见（见 DlgHost.WalkScrollers）。
    /// </summary>
    internal bool OwnScroll { get; set; }

    /// <summary>进来时该把焦点给谁。没有就算了。</summary>
    internal virtual IInputElement InitialFocus => null;

    /// <summary>
    /// 内容挂上去之后调一次，等价于原来写的 `this.Loaded += ...`。
    ///
    /// ⚠️ 内嵌模式下这个 Window 不会被 Show()，`Loaded` 根本不触发。原来挂在
    /// Loaded 上的「打开后自动搜索 / 自动刷新」会静悄悄地一次都不跑 —— 排查时
    /// 很容易误以为是对话框坏了。要自动干活的请 override 这个。
    /// </summary>
    internal virtual void OnShown() { }

    /// <summary>
    /// 想让这个对话框回到「独立小窗口」的老样子，就在构造函数里置 true。
    /// 目前没有谁用，留着是因为万一内嵌在某处不合适，改一行就能退回去。
    /// </summary>
    protected bool Popout { get; set; }

    /// <summary>
    /// 对话框内容里那个大标题。
    ///
    /// 内嵌时直接藏掉：主窗口那条标题栏（DlgHostTitle）已经把标题写了一遍，
    /// 内容里再来一个 18pt 加粗，同一个词上下出现两次，看着像排版漏了。
    /// 独立窗口模式（Popout）没有那条标题栏，照旧显示。
    /// </summary>
    protected UIElement PageTitle(string key, double size)
    {
        var t = Ui.TitleKey(key, size);
        if (!Popout) t.Visibility = Visibility.Collapsed;
        return t;
    }

    /// <summary>
    /// 构造函数最后一步调它，把搭好的内容交出去。
    /// 替代原来的 `Chrome.ApplyDialog(this, root)`。
    /// </summary>
    protected void Install(UIElement body)
    {
        _body = body;

        if (Popout)
        {
            // 老路：自己一个窗口，带标题栏和关闭按钮
            Width = double.IsNaN(Width) ? 560 : Width;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Ui.B("Brush.Bg.Base");
            if (FontFamily == null || FontFamily.Source.Length == 0)
                FontFamily = new FontFamily("Microsoft YaHei UI");
            Chrome.ApplyDialog(this, body);
            return;
        }

        // 内嵌模式：**不设 Content**。等 ShowDialog() 被调用时，DlgHost 会把
        // _body 摘到主窗口那一页上去。这里设了 Content 的话，body 就有了两个
        // 父级，摘的时候会抛异常。
    }

    /// <summary>把内容交出去（只能交一次，交完这边就不再持有）。</summary>
    internal UIElement TakeBody()
    {
        var b = _body;
        _body = null;
        return b;
    }

    /// <summary>
    /// 代替原来的 `ShowDialog()`。
    ///
    /// 内嵌模式下它**不开窗口**，而是把内容挂到主窗口、停在这儿等结果 —— 对调用方
    /// 来说和真 `ShowDialog()` 完全一样（同步、返回 bool?）。
    /// </summary>
    public new bool? ShowDialog()
    {
        if (Popout) return base.ShowDialog();
        return DlgHost.Show(this);
    }

    /// <summary>
    /// 结束这一页。跟 `DialogResult = x` 等价，两种模式都能用。
    ///
    /// ⚠️ 不要直接写 `DialogResult = x`：内嵌时没真的开窗口，WPF 会抛
    /// InvalidOperationException（"只能在创建 Window 并作为对话框显示之后才能设置"）。
    /// </summary>
    internal void CloseWith(bool? result)
    {
        if (Popout)
        {
            try { DialogResult = result; }
            catch (InvalidOperationException) { Close(); }
            return;
        }
        DlgHost.Close(this, result);
    }
}
