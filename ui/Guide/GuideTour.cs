// GuideTour.cs —— 阶段二：界面高亮巡礼
//
// 做法：一层透明的跟随窗口盖在主窗口上面，把要讲的控件「挖」出来，
// 旁边浮一张讲解卡片。每一步_highlight 一个区域，下一步推进。
//
// 为什么是独立窗口而不是塞一层 Panel 到主窗口里：
//   主窗口根部是 DockPanel，工具栏和状态栏都在中间那个 Grid 外面，
//   往 Grid 里加遮罩照不到它们 —— 而这两处恰恰是最该讲的。
//   独立窗口按主窗口的位置尺好，铺满整窗口，谁都盖得住。
//
// 坑记在这儿：
//   · 这层窗口本身是透明的，坐标要跟着主窗口走 ——
//     LocationChanged / SizeChanged / StateChanged 都得重新铺。
//   · 目标控件可能被收起来了（比如没开项目时项目树是空的），
//     铺之前先看一眼 Visible 和实际宽度，这种步骤直接跳过，别对着空气讲。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace JavaStudio;

internal sealed class GuideTour : Window
{
    public sealed record Step(FrameworkElement Target, string TitleKey, string DescKey);

    private readonly Window _owner;
    private readonly List<Step> _steps;
    private readonly Canvas _canvas = new();
    private readonly Border _card = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _desc = new();
    private readonly TextBlock _counter = new();
    private readonly Button _prev;
    private readonly Button _next;

    private int _index;
    private bool _completed;

    private GuideTour(Window owner, List<Step> steps)
    {
        _owner = owner;
        _steps = steps;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Owner = owner;

        _title.FontSize = 14;
        _title.FontWeight = FontWeights.SemiBold;
        _title.TextWrapping = TextWrapping.Wrap;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");

        _desc.FontSize = 12;
        _desc.TextWrapping = TextWrapping.Wrap;
        _desc.Margin = new Thickness(0, 5, 0, 0);
        _desc.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");

        _counter.FontSize = 10.5;
        _counter.Margin = new Thickness(0, 8, 0, 0);
        _counter.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");

        _prev = Ui.BtnKey("guide.prev");
        _prev.Click += (_, _) => { if (_index > 0) { _index--; Layout(); } };

        _next = Ui.BtnKey("guide.next", true);
        _next.Click += (_, _) => Advance();

        var skip = Ui.BtnKey("guide.skip");
        skip.Click += (_, _) => Stop(finished: false);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        btns.Children.Add(skip);
        btns.Children.Add(_prev);
        btns.Children.Add(_next);

        var box = new StackPanel();
        box.Children.Add(_title);
        box.Children.Add(_desc);
        box.Children.Add(_counter);
        box.Children.Add(btns);

        _card = new Border
        {
            Width = 330, Child = box, CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14), BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed, SnapsToDevicePixels = true,
            // ⚠️ 卡片放在 Grid 里、靠 Margin 定位，不能用 Canvas.Left/Top ——
            // Grid 是会给子元素重新摆位的，Canvas 那对附加属性在它这儿完全不生效，
            // 表现是卡片永远居中，看着像「位置算错了」。
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Base");
        _card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 22, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_card);
        Content = root;

        // 自检开关：JS_GUIDE_TSTEP=n 直接停在第 n 步（0 起）。
        if (int.TryParse(Environment.GetEnvironmentVariable("JS_GUIDE_TSTEP"), out int t)
            && t >= 0 && t < _steps.Count)
            _index = t;

        // 注意：这里不要挂 Loaded -> Layout。Loaded 早于主窗口排版完成，
        // 那时候量出来的尺寸是 0，会把没问题的步骤当成「看不见」跳过去。
        // 首次铺图统一走 OnContentRendered 里的延迟调用。
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Stop(finished: false); e.Handled = true; }
            else if (e.Key == Key.Enter) { Advance(); e.Handled = true; }
        };
    }

    /// <summary>跑一轮巡礼。返回 true = 走完了，false = 半路跳过。</summary>
    public static bool Run(Window owner, List<Step> steps)
    {
        if (steps == null || steps.Count == 0) return true;
        var t = new GuideTour(owner, steps);
        try { t.ShowDialog(); }
        catch (InvalidOperationException) { return false; }   // Owner 没显示过会炸，别让它拖累启动
        return t._completed;
    }

    // ---------------------------------------------------------------- 流程

    private void Advance()
    {
        if (_index >= _steps.Count - 1) { Stop(finished: true); return; }
        _index++;
        Layout();
    }

    private void Stop(bool finished)
    {
        _completed = finished;
        try { DialogResult = finished; } catch (InvalidOperationException) { Close(); }
    }

    // ---------------------------------------------------------------- 铺图

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        SnapToOwner();

        // 主窗口一动或者一变，这层就得跟着挪，不然「挖」出来的位置指错了地方
        _owner.LocationChanged += OnOwnerMoved;
        _owner.SizeChanged += OnOwnerMoved;
        _owner.StateChanged += OnOwnerMoved;

        // 等布局落定再量。这层窗口刚 Show 出来时主窗口那边的排版可能还没跑完，
        // 控件 ActualWidth 还是 0 —— 那时候判定「看不见」会把正常步骤误跳过
        // （踩过：项目树那一步就是这么消失的）。
        Dispatcher.BeginInvoke(new Action(Layout), DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        _owner.LocationChanged -= OnOwnerMoved;
        _owner.SizeChanged -= OnOwnerMoved;
        _owner.StateChanged -= OnOwnerMoved;
        base.OnClosed(e);
    }

    private void OnOwnerMoved(object sender, EventArgs e)
    {
        SnapToOwner();
        Layout();
    }

    private void SnapToOwner()
    {
        try
        {
            var root = _owner.Content as Visual;
            if (root == null) return;
            var dpi = VisualTreeHelper.GetDpi(_owner);
            Point p = _owner.PointToScreen(new Point(0, 0));
            Left = p.X / dpi.DpiScaleX;
            Top = p.Y / dpi.DpiScaleY;
            Width = _owner.ActualWidth;
            Height = _owner.ActualHeight;
        }
        catch { /* 拿不到就先保持原位 */ }
    }

    /// <summary>目标此刻是不是真的看得见。看不见的步骤直接过，别对着空气讲。</summary>
    private static bool IsTargetVisible(FrameworkElement el)
    {
        if (el == null || el.Visibility != Visibility.Visible) return false;
        return el.ActualWidth > 2 && el.ActualHeight > 2;
    }

    private void Layout()
    {
        _canvas.Children.Clear();
        if (!IsLoaded || _index >= _steps.Count) return;

        // 判定「看不见」之前先把主窗口的排版催一次 —— ActualWidth 为 0
        // 可能只是还没量，不是真的收起来了。量完再判，误跳的概率就小得多。
        try { _owner.UpdateLayout(); } catch { }

        // 跳掉确实不可见的步骤
        while (_index < _steps.Count && !IsTargetVisible(_steps[_index].Target)) _index++;
        if (_index >= _steps.Count) { Stop(finished: true); return; }

        var step = _steps[_index];
        Rect r = RectOf(step.Target);
        if (r.Width < 2 || r.Height < 2) { Advance(); return; }

        double W = ActualWidth, H = ActualHeight;
        const double pad = 6;

        double x = Math.Max(0, r.Left - pad);
        double y = Math.Max(0, r.Top - pad);
        double w = Math.Min(W - x, r.Width + pad * 2);
        double h = Math.Min(H - y, r.Height + pad * 2);

        // 四块遮罩拼出中间的洞 —— 比走 Geometry 组合直观，也不会因为
        // target 尺寸为 0 时 CombinedGeometry 退化成一整块黑而盖死全屏
        AddVeil(0, 0, W, y);                                  // 上
        AddVeil(0, y + h, W, Math.Max(0, H - y - h));         // 下
        AddVeil(0, y, x, h);                                  // 左
        AddVeil(x + w, y, Math.Max(0, W - x - w), h);         // 右

        // 洞边上描一圈强调色，让视线有落点
        var ring = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2), Background = System.Windows.Media.Brushes.Transparent,
            SnapsToDevicePixels = true
        };
        ring.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        Canvas.SetLeft(ring, x);
        Canvas.SetTop(ring, y);
        _canvas.Children.Add(ring);

        _title.Text = Lang.T(step.TitleKey);
        _title.Tag = step.TitleKey;
        _desc.Text = Lang.T(step.DescKey);
        _desc.Tag = step.DescKey;
        _counter.Text = string.Format(Lang.T("guide.stepOf"), _index + 1, _steps.Count);
        _prev.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        _next.Content = Lang.T(_index >= _steps.Count - 1 ? "guide.t.finish" : "guide.next");
        _next.Tag = _index >= _steps.Count - 1 ? "guide.t.finish" : "guide.next";

        _card.Visibility = Visibility.Visible;
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cw = _card.DesiredSize.Width, ch = _card.DesiredSize.Height;

        double cx, cy;
        // 优先放到洞的下面；下面放不下就翻到上面；再不行就在洞里居中
        if (y + h + 14 + ch <= H - 8) { cx = x; cy = y + h + 14; }
        else if (y - 14 - ch >= 8) { cx = x; cy = y - 14 - ch; }
        else { cx = x + w + 14; cy = y; }
        cx = Math.Min(Math.Max(8, cx), Math.Max(8, W - cw - 8));
        cy = Math.Min(Math.Max(8, cy), Math.Max(8, H - ch - 8));

        _card.Margin = new Thickness(cx, cy, 0, 0);
    }

    private void AddVeil(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        var rect = new System.Windows.Shapes.Rectangle
        {
            Width = w, Height = h, Fill = new SolidColorBrush(Colors.Black) { Opacity = 0.62 }
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _canvas.Children.Add(rect);
    }

    /// <summary>目标相对主窗口内容区的矩形 —— 铺洞和摆卡片都用它。</summary>
    private Rect RectOf(FrameworkElement el)
    {
        var root = _owner.Content as Visual;
        if (root == null) return Rect.Empty;
        try
        {
            Point p = el.TransformToVisual(root).Transform(new Point(0, 0));
            return new Rect(p.X, p.Y, el.ActualWidth, el.ActualHeight);
        }
        catch
        {
            return Rect.Empty;   // 元素已经从树上摘掉了（比如切了主页），当看不见处理
        }
    }
}
