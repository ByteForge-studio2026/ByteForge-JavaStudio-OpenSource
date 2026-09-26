// Chrome.cs —— 自定义标题栏 + 圆角对话框外壳
//
// 以前所有窗口都用 Windows 自带的标题栏，和 IDE 的深色风格格格不入，
// 而且对话框按钮还是系统直角。这里统一：
//   - 主窗口：WindowStyle=None + WindowChrome（保留原生缩放/吸附），
//     顶部自己画一条标题栏（拖拽 + 最小化/最大化/关闭）。
//   - 对话框：WindowStyle=None + AllowsTransparency（真圆角），
//     外面包一层圆角 Border，顶部同样一条标题栏（只有关闭）。
// 标题栏和按钮的颜色全走 DynamicResource，切主题自动跟着变。

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace JavaStudio;

internal static class Chrome
{
    // ---------------------------------------------------------------- 对话框

    /// <summary>给一个对话框套上自定义标题栏 + 圆角外壳。标题取窗口的 Title。</summary>
    public static void ApplyDialog(Window w, UIElement body)
    {
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Background = Brushes.Transparent;

        var border = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Base");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        // DockPanel：标题栏顶上 38，内容占满剩下的。SizeToContent 时内容自然多高
        // 窗口就多高；显式 Height 时内容拉伸填满，都不会留透明缝。
        var dock = new DockPanel();
        var bar = MakeTitleBar(w, w.Title, dialog: true);
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(body);
        border.Child = dock;

        w.Content = border;

        // 所有对话框都从这儿过一道，所以「切语言」这件事统一在这里兜住：
        // 每次真正显示前按当前语言刷一遍带戳的文案。
        // 放在这里而不是各个弹窗构造函数里，是因为弹窗可能是切语言之前就建好的，
        // 而显示动作一定发生在切语言之后。
        w.Loaded += (_, _) => Lang.Translate(w);
    }

    // ---------------------------------------------------------------- 主窗口

    /// <summary>主窗口的标题栏：插到 DockPanel 最顶上，再挂 WindowChrome（原生缩放）。</summary>
    public static void ApplyMainWindow(MainWindow w)
    {
        w.WindowStyle = WindowStyle.None;

        // 先挂自己的「最大化尺寸」hook，再设 WindowChrome：SourceInitialized 的
        // 订阅者是先订阅先跑，所以这行必须在 SetWindowChrome 之前，否则会被
        // WindowChrome 那套（按整个显示器算）抢先处理掉。
        w.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(w) is HwndSource src)
                src.AddHook(MaxSizeHook);
        };

        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = 0,                       // 标题栏自己画，不给系统留可拖区域
            CornerRadius = new CornerRadius(9),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(7),
            NonClientFrameEdges = NonClientFrameEdges.None,
        });
        var bar = MakeTitleBar(w, "Java Studio", dialog: false);
        DockPanel.SetDock(bar, Dock.Top);
        if (w.Content is DockPanel dp)
        {
            dp.Children.Insert(0, bar);
            // 圆角：把整个内容裁成 9px 圆角矩形，和 WindowChrome.CornerRadius(9) 对齐。
            // 否则标题栏（及状态栏）的方角被圆角窗口裁掉后，方角抗锯齿边缘会跟非客户区
            // 的白色混出「白角/白边」。最大化时窗口铺满、本就没有圆角，半径置 0，
            // 避免角落被裁出透明、透出后面的桌面。
            const double R = 9;
            void ClipContent()
            {
                if (w.ActualWidth <= 0 || w.ActualHeight <= 0) return; // 布局未就绪，先不裁（保持可见）
                double r = w.WindowState == WindowState.Maximized ? 0 : R;
                dp.Clip = new RectangleGeometry(new Rect(0, 0, w.ActualWidth, w.ActualHeight), r, r);
            }
            w.SizeChanged += (_, _) => ClipContent();
            w.StateChanged += (_, _) =>
            {
                ClipContent();
                UpdateMaxGlyphLater(w, bar);
            };
        }
        else
        {
            w.StateChanged += (_, _) => UpdateMaxGlyphLater(w, bar);
        }
    }

    /// <summary>
    /// 最大化按钮的图标要在「□ / 还原」之间换，但**不能**在 StateChanged 里当场换。
    ///
    /// 踩过的坑：StateChanged 是在 WPF 处理完 WM_SIZE 之后、真正把新尺寸交给布局之前
    /// 触发的。这个回调里往标题栏插/换一个 Button，等于在窗口状态切换的半途动视觉树，
    /// WPF 会当场按「旧尺寸」跑一次布局，紧接着那次本该把根布局撑大的更新就被吞掉了。
    /// 结果就是：窗口 HWND 已经最大化成 1920x1032，内容却还停在还原时的 1040x700，
    /// 右下多出来的那一大片全是黑屏。
    /// 延后一个 dispatcher 周期再换图标，让 WPF 先把尺寸和布局走完，就没事了。
    /// </summary>
    private static void UpdateMaxGlyphLater(Window w, Border bar)
        => w.Dispatcher.BeginInvoke(new Action(() => UpdateMaxGlyph(w, bar)),
                                    System.Windows.Threading.DispatcherPriority.Loaded);

    // ---------------------------------------------------------------- 最大化尺寸
    //
    // WindowStyle=None 的窗口，系统默认按「整个显示器」给它最大化尺寸：带任务栏的
    // 显示器上会盖住任务栏，缩放 / 多屏时还可能按错显示器的尺寸，表现就是「最大化
    // 后没铺满，或铺过头」。这里在 WM_GETMINMAXINFO 里把最大化尺寸钉死成当前显示器
    // 的工作区（rcWork，已经扣掉任务栏），铺满但不越界。

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static IntPtr MaxSizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return IntPtr.Zero;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        RECT work = mi.rcWork, scr = mi.rcMonitor;
        mmi.ptMaxPosition.X = work.Left - scr.Left;   // 相对显示器左上角
        mmi.ptMaxPosition.Y = work.Top - scr.Top;
        mmi.ptMaxSize.X = work.Right - work.Left;
        mmi.ptMaxSize.Y = work.Bottom - work.Top;
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- 标题栏

    private static Border MakeTitleBar(Window w, string title, bool dialog)
    {
        var bar = new Border { Height = 38 };
        bar.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        bar.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        bar.BorderThickness = new Thickness(0, 0, 0, 1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 左侧：图标 + 标题
        var left = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        // 应用图标用 icon.ico（和 exe 资源同一份）。加载失败就退回 coffee，标题栏不会开天窗。
        UIElement logo;
        try
        {
            var iconUri = new System.Uri(
                System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "icon.ico"),
                System.UriKind.Absolute);
            var iconBmp = new System.Windows.Media.Imaging.BitmapImage(iconUri);
            logo = new Image
            {
                Source = iconBmp, Width = 18, Height = 18,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };
        }
        catch
        {
            logo = Icons.Visual("coffee", 17, "Brush.Fg.Primary");
        }
        left.Children.Add(logo);
        var titleTb = new TextBlock { Text = title, FontSize = 13,
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleTb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        left.Children.Add(titleTb);
        // 自绘标题栏上这段字是构造时从 w.Title 拷过来的，不在窗口的常规内容树上，
        // Lang.Translate 摸不到它。所以这里给它也打上同一个戳：
        // Translate 会把 w.Title 和这段文字设成同一个值，切语言时两边一起变。
        if (dialog && w.Tag is string tkey && !string.IsNullOrEmpty(tkey))
            titleTb.Tag = tkey;
        grid.Children.Add(left);

        // 右侧：窗口控制按钮。mac 的红绿灯：黄=最小化、绿=最大化、红=关闭。
        // 顺序沿用 Windows 的位置（关闭永远在最右那个角），只把外形换成圆点。
        var right = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0) };
        if (!dialog)
        {
            right.Children.Add(TrafficBtn(w, "min", (_, _) => w.WindowState = WindowState.Minimized));
            right.Children.Add(TrafficBtn(w, "max", (_, _) => ToggleMax(w)));
        }
        right.Children.Add(TrafficBtn(w, "close", (_, _) => w.Close()));
        grid.Children.Add(right);
        Grid.SetColumn(right, 1);

        bar.Child = grid;

        // 拖动：左键拖标题栏；主窗口双击最大化/还原
        bar.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2 && !dialog) { ToggleMax(w); return; }
            // 最大化时 DragMove 会直接抛 InvalidOperationException（WPF 不让拖最大化窗口），
            // 先还原再拖，跟系统「拖拽已最大化窗口」的手感一致。
            if (w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal;
            try { w.DragMove(); } catch { }
        };
        return bar;
    }

    private static void ToggleMax(Window w)
        => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>主窗口状态变了，把最大化按钮的图标在「□ / 还原」之间换一下。</summary>
    private static void UpdateMaxGlyph(Window w, Border bar)
    {
        if (bar.Child is not Grid g) return;
        var right = g.Children.OfType<StackPanel>().LastOrDefault();
        if (right == null || right.Children.Count < 2) return;
        string kind = w.WindowState == WindowState.Maximized ? "maxrestored" : "max";

        // ⚠️ 这里**不能**写成 right.Children[1] = 新按钮。
        // UIElementCollection 的索引器 setter 会当场抛
        //   ArgumentException: 指定的索引已经在使用。请先在指定的索引处断开 Visual 子级。
        // 而 Dispatcher 会把回调里的异常悄悄吞掉，表面上什么都没发生 ——
        // 症状就是「窗口最大化了，最大化按钮那个图标却始终不换」。
        // 老老实实先 RemoveAt 断开旧子级、再 Insert，异常消息里的"断开"就是这个意思。
        right.Children.RemoveAt(1);
        right.Children.Insert(1, TrafficBtn(w, kind, (_, _) => ToggleMax(w)));
    }

    /// <summary>
    /// 一个红绿灯圆点。
    ///
    /// 三个颜色是 mac 的原值，不进主题表：这套样式的全部意义就是「靠颜色认按钮」，
    /// 让深色调色板把它们洗成别的色就等于白画。符号一律深色半透明，
    /// 所以在浅标题栏和深标题栏上对比度都一样。
    /// </summary>
    private static Button TrafficBtn(Window w, string kind, RoutedEventHandler click)
    {
        var dot = new SolidColorBrush(kind switch
        {
            "min"         => Color.FromRgb(0xFE, 0xBC, 0x2E),   // 黄
            "max"         => Color.FromRgb(0x28, 0xC8, 0x40),   // 绿
            "maxrestored" => Color.FromRgb(0x28, 0xC8, 0x40),
            _             => Color.FromRgb(0xFF, 0x5F, 0x57),   // 红
        });

        // 符号统一画在 8×8 的设计框里，框的正中心是 (4,4)。
        // 三件事必须同时成立，否则圆点里的符号就会歪（这几个都实测踩过）：
        //   1) 每个符号的 bounds 中心都要落在 (4,4)。bounds 不居中的话，
        //      8×8 的布局框居中了，符号本身还是偏的。
        //   2) 模板里 Path 要写死 Width/Height=8。Stretch=None 时 WPF 不平移 geometry，
        //      不给死尺寸的话布局框只有 bounds 那么大，坐标大于它的部分直接画到框外 ——
        //      最小化那条横线就是这么偏下去 2px 的。
        //   3) 框取偶数 8。24 宽的按钮减 8 得 16，除以 2 是整数，不会落在半像素上发糊。
        Geometry geo = Geometry.Parse(kind switch
        {
            "min"         => "M1.2,4 L6.8,4",
            "max"         => "M1.6,1.6 L6.4,1.6 L6.4,6.4 L1.6,6.4 Z",
            // 还原 = 前后两个错开的方框。
            //
            // 两个坑：
            //   1) 错开量必须够大。一开始只错开 1.4 单位、描边 1.5 宽，
            //      两层线条基本重叠，渲染出来就是一个"描边偏粗的方框"，
            //      压根看不出是两层。这里错开 2 单位（= 框边长的一半）。
            //   2) 后面那个框的线在重叠处要**手动断开**，不然两个框的线交叉成井字。
            //      mac 是靠后框被前框遮挡实现的，这里只有一个 Path，
            //      就把该被盖住的那几段不画。
            //   bbox 仍是 (1,1)-(7,7)，中心正好 (4,4)，和前两个符号对齐。
            "maxrestored" => "M3,3 L7,3 L7,7 L3,7 Z " +
                             "M1,1 L1,5 L3,5 " +
                             "M1,1 L5,1 L5,3",
            _             => "M2.1,2.1 L5.9,5.9 M5.9,2.1 L2.1,5.9",
        });

        var b = new Button
        {
            // 24 宽是留给鼠标的命中区（和 mac 一样：圆点细，命中区比它宽得多）。
            // 圆点本身 16，圆心间距 24，净间隙 8。
            Width = 24, Height = 38,
            Background = dot, Content = geo,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Style = (Style)Application.Current.FindResource("TrafficBtn")
        };
        b.Click += click;
        return b;
    }
}
