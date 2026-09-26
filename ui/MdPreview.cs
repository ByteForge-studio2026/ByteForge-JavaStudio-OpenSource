// MdPreview.cs —— Markdown 文件的「编辑 / 拆分 / 预览」三态
//
// md 不是代码，拿代码编辑器看它等于看源码。所以 md 打开时不直接塞进 TabItem，
// 而是套一层这个容器：左边编辑、右边渲染，中间那条线能拖；
// 上面一条 30 高的按钮条，右上角一个圆角按钮切三种状态（编辑 / 拆分 / 预览）。
//
// ⚠️⚠️ 预览**不用 WebBrowser**，是 WPF 原生渲染（FlowDocument）。两个原因：
//
//   1) 这台机器上 WPF 的 WebBrowser（Trident 内核）**根本不画东西**。
//      不是我的 HTML 写错了 —— 喂它一整块纯红的 body，屏幕上也是一个红点都没有
//      （用 BitBlt 从屏幕 DC 抓下来量的，不是 RenderTargetBitmap 那种会漏掉
//      HWND 宿主的抓法）。IE 退役之后这个控件在不少机器上是废的，不能押在它身上。
//   2) WebBrowser 是 HWND 宿主，在 WPF 里永远画在 WPF 元素**上面**（airspace），
//      ZIndex 压不住 —— 浮在上面的切换按钮会被它吃掉。
//
// 改成 FlowDocument 之后：不依赖任何浏览器组件、切主题自动跟着变、
// 按钮想放哪儿都行。代价是没有完整 HTML/CSS，但 md 就是 README 那类说明文档，
// 下面这些够用：标题三档 / 粗体 / 斜体 / 行内代码 / 代码块 / 列表 / 引用 / 链接。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // Popup / PlacementMode
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;               // TextEditor

namespace JavaStudio;

internal enum MdViewMode { Edit, Split, Preview }

/// <summary>轻量 Markdown → FlowDocument。够 README / 说明文档用，不追求 CommonMark 全兼容。</summary>
internal static class MdDoc
{
    private static readonly Regex Inline = new(
        @"\*\*(?<b>.+?)\*\*|`(?<c>[^`]+?)`|\[(?<t>[^\]]+?)\]\((?<u>[^)]+?)\)|(?<!\*)\*(?<i>[^*]+?)\*(?!\*)",
        RegexOptions.Compiled);

    public static FlowDocument Build(string md)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(24, 18, 24, 24), FontSize = 15 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Fg.Primary");

        var lines = md.Replace("\r\n", "\n").Split('\n');
        bool inCode = false; var code = new StringBuilder();
        List list = null;

        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                if (!inCode) { inCode = true; code.Clear(); }
                else
                {
                    inCode = false;
                    list = null;
                    doc.Blocks.Add(CodeBlock(code.ToString()));
                }
                continue;
            }
            if (inCode) { code.Append(raw).Append('\n'); continue; }

            if (string.IsNullOrWhiteSpace(raw)) { list = null; continue; }

            Paragraph p;
            if (raw.StartsWith("# "))       { list = null; p = Head(raw.Substring(2), 24); }
            else if (raw.StartsWith("## ")) { list = null; p = Head(raw.Substring(3), 20); }
            else if (raw.StartsWith("### ")){ list = null; p = Head(raw.Substring(4), 17); }
            else if (raw.StartsWith("> "))
            {
                list = null;
                p = Text(raw.Substring(2));
                // 引用：左边一道竖线 + 缩进 + 暗一点的字
                p.BorderThickness = new Thickness(3, 0, 0, 0);
                p.BorderBrush = Ui.B("Brush.Accent");
                p.Padding = new Thickness(14, 2, 0, 2);
                p.Margin = new Thickness(0, 6, 0, 6);
                p.SetResourceReference(Paragraph.ForegroundProperty, "Brush.Fg.Muted");
            }
            else if (raw.StartsWith("- ") || raw.StartsWith("* "))
            {
                if (list == null)
                {
                    list = new List { MarkerStyle = TextMarkerStyle.Disc,
                                      Margin = new Thickness(6, 4, 0, 8) };
                    doc.Blocks.Add(list);
                }
                // 列表项里的段落不要再带走自己的上下边距，不然每一项之间空一大截
                var itemPara = Text(raw.Substring(2));
                itemPara.Margin = new Thickness(0, 1, 0, 1);
                list.ListItems.Add(new ListItem(itemPara));
                continue;
            }
            else { list = null; p = Text(raw); }

            doc.Blocks.Add(p);
        }
        if (inCode) doc.Blocks.Add(CodeBlock(code.ToString()));
        return doc;
    }

    /// <summary>给一段纯文本当正文用（比如「不支持这个格式」的提示）。</summary>
    public static FlowDocument Plain(string text)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(24) };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.Fg.Muted");
        doc.Blocks.Add(new Paragraph(new Run(text)));
        return doc;
    }

    private static Paragraph Head(string text, double size)
    {
        var p = new Paragraph { FontSize = size, FontWeight = FontWeights.SemiBold,
                                Margin = new Thickness(0, 18, 0, 6), LineHeight = size * 1.35 };
        p.SetResourceReference(Paragraph.ForegroundProperty, "Brush.Fg.Primary");
        Fill(p.Inlines, text);
        return p;
    }

    private static Paragraph Text(string s)
    {
        var p = new Paragraph { Margin = new Thickness(0, 3, 0, 7), LineHeight = 24 };
        p.SetResourceReference(Paragraph.ForegroundProperty, "Brush.Fg.Primary");
        Fill(p.Inlines, s);
        return p;
    }

    private static Block CodeBlock(string src)
    {
        var p = new Paragraph(new Run(src.TrimEnd('\n')))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5,
            // ⚠️ Paragraph 没有 TextWrapping 这个属性（那是 TextBlock/TextBox 的）。
            // FlowDocument 里的段落本来就是按页宽自动折行的，不用设也别设。
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 8, 0, 10),
        };
        p.SetResourceReference(Paragraph.ForegroundProperty, "Brush.Fg.Primary");
        p.SetResourceReference(Paragraph.BackgroundProperty, "Brush.Bg.Raised");
        p.SetResourceReference(Paragraph.BorderBrushProperty, "Brush.Border");
        p.BorderThickness = new Thickness(1);
        return p;
    }

    /// <summary>把一行里的 `code` / **粗** / *斜* / [链接]() 拆成 Inline。</summary>
    private static void Fill(InlineCollection into, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        int at = 0;
        foreach (Match m in Inline.Matches(s))
        {
            if (m.Index > at) into.Add(new Run(s.Substring(at, m.Index - at)));
            at = m.Index + m.Length;

            if (m.Groups["c"].Success)
            {
                var r = new Run(m.Groups["c"].Value)
                {
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13
                };
                r.SetResourceReference(Run.ForegroundProperty, "Brush.Accent");
                into.Add(r);
            }
            else if (m.Groups["b"].Success)
            {
                into.Add(new Run(m.Groups["b"].Value) { FontWeight = FontWeights.SemiBold });
            }
            else if (m.Groups["i"].Success)
            {
                into.Add(new Run(m.Groups["i"].Value) { FontStyle = FontStyles.Italic });
            }
            else if (m.Groups["t"].Success)
            {
                var h = new Hyperlink(new Run(m.Groups["t"].Value))
                {
                    // 点链接开浏览器。⚠️ 必须 Handle，否则 FlowDocument 会往上抛
                    // 一个导航请求，没人接就是一次未处理的异常。
                    NavigateUri = new Uri(m.Groups["u"].Value)
                };
                h.SetResourceReference(Hyperlink.ForegroundProperty, "Brush.Accent");
                h.TextDecorations = null;
                h.RequestNavigate += (_, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
                    }
                    catch { /* 打不开就算了，别把编辑器带走 */ }
                    e.Handled = true;
                };
                into.Add(h);
            }
        }
        if (at < s.Length) into.Add(new Run(s.Substring(at)));
    }
}

/// <summary>
/// md 文件的容器：左编辑 + 右渲染，中间一条能拖的分割线，上面一条按钮条。
///
/// 它是 TabItem 的 Content，所以外面那些按 `t.Content is TextEditor` 找编辑器的
/// 地方要改成先问这个容器要（见 MainWindow.CurrentEditor）。
/// </summary>
internal sealed class MdPane : Grid
{
    /// <summary>
    /// 拆分时**编辑区**占的比例（不是「左栏」—— 交换之后编辑区在右边）。
    /// **整个会话共用**：拖过一次分界线，之后新开的 md 也按这个来。
    /// </summary>
    private static double _splitRatio = 0.5;

    /// <summary>上次选的模式，同样整个会话共用。</summary>
    private static MdViewMode _lastMode = MdViewMode.Split;

    /// <summary>
    /// 编辑区是否在**右**（预览在左）。点一次「交换左右」就翻过来，
    /// 之后所有 md 都跟着走 —— 并且写进 Configuration.json，关掉软件再开还是那样。
    /// </summary>
    private static bool _swapped = Config.MdSwap;

    public TextEditor Editor { get; }

    private readonly FlowDocumentScrollViewer _view = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        IsToolBarVisible = false,
    };
    private readonly ColumnDefinition _edCol, _splitCol, _viewCol;
    private readonly GridSplitter _split;
    private readonly Button _btn;
    private readonly Button _btnSwap;
    private readonly TextBlock _btnText = new() { FontSize = 12 };
    private readonly ContentControl _btnIcon = new() { Width = 14, Height = 14 };
    /// <summary>图标和它后面的字之间留多少。太小的话「▦拆分」会糊成一坨。</summary>
    private const double IconGap = 6;
    private readonly DispatcherTimer _refresh;
    private Popup _pop;
    private MdViewMode _mode = _lastMode;
    private bool _dirty = true;      // 预览内容已经不是当前文本了

    public MdPane(TextEditor ed)
    {
        Editor = ed;

        // ---- 两行：上面一条 30 高的按钮条，下面才是「编辑 | 分割线 | 预览」
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- 三列：编辑 | 分割线 | 预览
        _edCol = new ColumnDefinition { Width = new GridLength(_splitRatio, GridUnitType.Star) };
        _splitCol = new ColumnDefinition { Width = new GridLength(5) };
        _viewCol = new ColumnDefinition { Width = new GridLength(1 - _splitRatio, GridUnitType.Star) };
        ColumnDefinitions.Add(_edCol);
        ColumnDefinitions.Add(_splitCol);
        ColumnDefinitions.Add(_viewCol);

        // 编辑器默认 0 列、预览 2 列；交换之后是 2 / 0（见 ApplySwap）
        Grid.SetRow(ed, 1);
        Children.Add(ed);

        // 分割线：ResizeBehavior 用 PreviousAndNext，两栏一起让位。
        // ⚠️ 不要留 Background 为空：透明的话点在它身上会穿透到编辑区，
        // 出现「按住那条线拖动，光标在编辑器里刷出一片选区」。
        _split = new GridSplitter
        {
            Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Cursor = System.Windows.Input.Cursors.SizeWE,
        };
        _split.SetResourceReference(GridSplitter.BackgroundProperty, "Brush.Border");
        Grid.SetColumn(_split, 1);
        Grid.SetRow(_split, 1);
        Children.Add(_split);
        // 拖完把新比例记下来，下次开 md 接着用。
        // ⚠️ 记的是**编辑区**占多少，不是「左栏」占多少 —— 交换之后编辑区在右列，
        // 照左栏存的话，翻一次再拖一次，宽度就反着跳了。
        _split.DragCompleted += (_, _) =>
        {
            double total = _edCol.ActualWidth + _viewCol.ActualWidth;
            if (total <= 1) return;
            double ed = _swapped ? _viewCol.ActualWidth : _edCol.ActualWidth;
            _splitRatio = Math.Clamp(ed / total, 0.15, 0.85);
        };

        Grid.SetColumn(_view, 2);
        Grid.SetRow(_view, 1);
        Children.Add(_view);

        // ---- 右上角那一对按钮：[交换左右] [视图模式]，整组待在 30 高那行里靠右
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        Grid.SetColumnSpan(bar, 3);
        Children.Add(bar);

        // 交换左右：只有两栏同时在才有意义，编辑 / 预览这两种模式整个按钮藏起来
        _btnSwap = new Button
        {
            Style = (Style)Application.Current.FindResource("MdSwapBtn"),
            ToolTip = Lang.T("md.swap.tip"),
        };
        _btnSwap.SetResourceReference(Button.BackgroundProperty, "Brush.Bg.Panel");
        _btnSwap.Content = Icons.Visual("shuffle", 13, "Brush.Fg.Primary");
        _btnSwap.Click += (_, _) => Swap();
        bar.Children.Add(_btnSwap);

        _btn = new Button
        {
            Style = (Style)Application.Current.FindResource("MdModeBtn"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = Lang.T("md.mode.tip"),
        };
        _btn.SetResourceReference(Button.BackgroundProperty, "Brush.Bg.Panel");
        var box = new StackPanel { Orientation = Orientation.Horizontal,
                                   VerticalAlignment = VerticalAlignment.Center };
        // ⚠️ 图标必须先把 Margin 清掉再上：_btnIcon 这个 ContentControl 的模板
        // 自带一圈内边距，不清的话图标左右各多出几个像素，
        // 再叠加下面那个 IconGap 就变成「图标和字快贴上了」。
        // 这三个元素全程只有这一处创建，所以清一次就够。
        box.Children.Add(_btnIcon);
        box.Children.Add(_btnText);
        _btnIcon.Margin = new Thickness(0);
        _btnText.Margin = new Thickness(IconGap, 0, 0, 0);
        var arrow = Icons.Visual("chevron-down", 12, "Brush.Fg.Dim");
        if (arrow != null) { arrow.Margin = new Thickness(8, 1, 0, 0); box.Children.Add(arrow); }
        _btn.Content = box;
        _btn.Click += (_, _) => OpenMenu();
        bar.Children.Add(_btn);

        // ---- 跟着打字刷新（停手 350ms）
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refresh.Tick += (_, _) => { _refresh.Stop(); Render(); };
        ed.Document.TextChanged += (_, _) =>
        {
            _dirty = true;
            if (_mode != MdViewMode.Edit) { _refresh.Stop(); _refresh.Start(); }
        };

        ApplyMode();
        Loaded += (_, _) => Render();
    }

    // ---------------------------------------------------------------- 模式

    private void ApplyMode()
    {
        bool edit = _mode != MdViewMode.Preview;
        bool view = _mode != MdViewMode.Edit;
        bool split = _mode == MdViewMode.Split;

        // 编辑区 / 预览区各占多少。非拆分时占满的那一个拿 1，另一个 0。
        double edShare = split ? _splitRatio : 1;
        double viewShare = split ? 1 - _splitRatio : 1;

        // ⚠️ 用「列宽归零」而不是 Visibility.Collapsed：编辑器藏掉再显示会丢焦点、
        // 丢光标位置；列宽归零它一直是活的，切回来光标还在原处。
        // 交换过的话编辑区在右列，所以两栏分到的份额要对调。
        _edCol.Width = new GridLength(edit ? (_swapped ? viewShare : edShare) : 0, GridUnitType.Star);
        _viewCol.Width = new GridLength(view ? (_swapped ? edShare : viewShare) : 0, GridUnitType.Star);
        _splitCol.Width = new GridLength(split ? 5 : 0);
        _split.Visibility = split ? Visibility.Visible : Visibility.Collapsed;

        ApplySwap();
        // 只有拆分才有「左右」可言，另两种模式下这个按钮没有意义
        _btnSwap.Visibility = split ? Visibility.Visible : Visibility.Collapsed;

        _btnIcon.Content = Icons.Visual(IconOf(_mode), 14, "Brush.Fg.Primary");
        _btnText.Text = Lang.T(KeyOf(_mode));

        _lastMode = _mode;
        if (view) { _dirty = true; Render(); }
        else Editor.Focus();
    }

    /// <summary>把编辑器和预览放进正确的列里（交换 = 编辑器去右列、预览去左列）。</summary>
    private void ApplySwap()
    {
        Grid.SetColumn(Editor, _swapped ? 2 : 0);
        Grid.SetColumn(_view, _swapped ? 0 : 2);
    }

    /// <summary>交换左右两栏。改的是共享的静态字段，所以之后新开的 md 也按新的来。</summary>
    public void Swap()
    {
        _swapped = !_swapped;
        Config.MdSwap = _swapped;
        Config.Save();          // 写进 Configuration.json，下次启动还记得
        ApplyMode();
    }

    public void SetMode(MdViewMode m)
    {
        if (_mode == m) return;
        _mode = m;
        ApplyMode();
    }

    private static string IconOf(MdViewMode m) => m switch
    {
        MdViewMode.Edit => "edit",
        MdViewMode.Split => "grid",
        _ => "eye",
    };

    private static string KeyOf(MdViewMode m) => m switch
    {
        MdViewMode.Edit => "md.mode.edit",
        MdViewMode.Split => "md.mode.split",
        _ => "md.mode.preview",
    };

    // ---------------------------------------------------------------- 菜单

    private void OpenMenu()
    {
        if (_pop != null) { _pop.IsOpen = false; _pop = null; return; }

        var box = new StackPanel { Margin = new Thickness(4) };
        box.Children.Add(Item(MdViewMode.Edit));
        box.Children.Add(Item(MdViewMode.Split));
        box.Children.Add(Item(MdViewMode.Preview));

        var card = new Border { Child = box, CornerRadius = new CornerRadius(10),
                                SnapsToDevicePixels = true };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        card.BorderThickness = new Thickness(1);
        // 菜单浮在内容上，没有阴影会跟底下的字糊在一起
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 2, Opacity = 0.22, Direction = 270
        };

        _pop = new Popup
        {
            Child = card, PlacementTarget = _btn, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, IsOpen = true
        };
        _pop.Closed += (_, _) => _pop = null;
    }

    private Button Item(MdViewMode m)
    {
        var b = new Button { Style = (Style)Application.Current.FindResource("MdMenuItemBtn"),
                             HorizontalAlignment = HorizontalAlignment.Stretch };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var ic = Icons.Visual(IconOf(m), 14, m == _mode ? "Brush.Accent" : "Brush.Fg.Muted");
        // 图标左边留出菜单项的 Padding（9）那么多，免得选中那一项的字被顶出去
        if (ic != null) { ic.Width = 14; ic.Height = 14;
                          ic.Margin = new Thickness(9, 0, 0, 0); row.Children.Add(ic); }
        // 当前那一项后面打个圆点：不然看不出「现在是哪一种」
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
                               Margin = new Thickness(IconGap, 0, 0, 0),
                               Visibility = m == _mode ? Visibility.Visible : Visibility.Collapsed };
        dot.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        row.Children.Add(dot);
        var t = new TextBlock { Text = Lang.T(KeyOf(m)), FontSize = 12.5,
                                Margin = new Thickness(IconGap, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty,
                               m == _mode ? "Brush.Fg.Primary" : "Brush.Fg.Muted");
        if (m == _mode) t.FontWeight = FontWeights.SemiBold;
        row.Children.Add(t);
        b.Content = row;
        b.Click += (_, _) =>
        {
            _pop.IsOpen = false;
            _pop = null;
            SetMode(m);
        };
        return b;
    }

    // ---------------------------------------------------------------- 渲染

    public void Render()
    {
        // 文本没动过就别重来：切模式、切主题、窗口缩放都会走到这儿，
        // 每次都重建文档的话，长文档会明显闪一下、滚动位置也丢。
        if (_mode == MdViewMode.Edit || !_dirty) return;
        try
        {
            _view.Document = MdDoc.Build(Editor.Document.Text);
            _dirty = false;
        }
        catch { /* 渲染挂了不该把编辑器也带走 */ }
    }

    /// <summary>
    /// 切主题时重新渲染一次。
    /// 其实颜色都走的 DynamicResource，理论上会自己变 —— 但代码块/标题那些
    /// 画刷是在构造时取好的，留个入口以防万一，也让调用处意图明确。
    /// </summary>
    public void ReTheme()
    {
        _dirty = true;
        Render();
    }
}
