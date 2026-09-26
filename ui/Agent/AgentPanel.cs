#nullable enable

// AgentPanel.cs —— To-code Studio Pro 的界面
//
// 竖排、停靠在右侧、宽度跟项目树一个量级；只有打开项目时才出现。
// 头部左侧是「收回」按钮（点它把面板收成右侧那个图标），旁边是设置和新会话。
// 欢迎页默认写「我们该构建什么」，输入框描边跑流光。
//
// 线程：模型的流式回调和工具执行都在后台线程上，凡是碰控件的地方一律
// Dispatcher 回去 —— 这是之前「调用线程无法访问此对象」那个坑的直接教训。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JavaStudio;

public sealed class AgentPanel : UserControl
{
    /// <summary>点「收回」时通知外层把面板收起来。</summary>
    public event Action? RequestCollapse;

    private readonly ScrollViewer _scroll;
    /// <summary>是否跟随到底部。用户往上翻时置 false，滑回底部再置 true。</summary>
    private bool _pinBottom = true;
    private readonly Button _jumpBottom;   // 「回到最新」，只在没贴底时出现
    // 在声明处就初始化：下面注册 Click 的 lambda 会引用 _list，
    // 而字段在构造函数里「后赋值」的话，编译器会认为那一刻它可能还是 null（CS8602）。
    private readonly StackPanel _list = new StackPanel { Margin = new Thickness(10, 10, 10, 4) };
    private readonly Border _welcome;
    private readonly TextBox _input;
    private readonly Button _send;
    private Button _attach;      // 「附上文件」按钮（回形针）
    private readonly TextBlock _hint;
    // 头部三个图标按钮留成字段：切语言时要重新给它们换 ToolTip，
    // 否则「切了英文界面但悬停还是中文」。
    private readonly Button _collapse;
    private readonly Button _newChat;
    private readonly Button _gear;

    private string _root = "";
    private AgentSession? _session;
    private AgentTools? _tools;
    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <summary>
    /// 当前挂起的那张授权卡片的「结案」动作。
    ///
    /// 存在的唯一理由：ConfirmOnUi 是拿 ManualResetEventSlim 阻塞等结果的，
    /// 万一卡片在用户点之前从列表里消失了（比如点了「新对话」清空了列表），
    /// 那个事件就永远没人 Set —— 后台线程会一直挂着，整个面板跟着假死。
    /// 所以清列表之前必须先把它按「拒绝」结掉。
    /// </summary>
    private Action<bool>? _pendingConfirm;

    public AgentPanel()
    {
        // ---- 头部：左边收回，中间标题，右边设置 / 新会话
        _collapse = IconButton("chevron-right", Lang.T("agent.collapse"));
        _collapse.Click += (_, _) => RequestCollapse?.Invoke();

        var title = new TextBlock
        {
            Text = "To-code Studio Pro", FontSize = 12, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");

        var logo = Icons.Visual("sparkles", 15, "Brush.Fg.Muted");

        _newChat = IconButton("refresh-cw", Lang.T("agent.newChat"));
        // 清空列表之前先把挂起的授权卡按「拒绝」结掉，
        // 否则等在那儿的那次工具调用会永远拿不到结果（见 _pendingConfirm 注释）。
        _newChat.Click += (_, _) =>
        {
            _pendingConfirm?.Invoke(false);
            _session?.Clear();
            _list.Children.Clear();
            RefreshWelcome();
        };

        _gear = IconButton("settings", Lang.T("agent.gear"));
        _gear.Click += (_, _) => OpenSettings();

        var head = new Grid { Height = 34 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_collapse, 0);
        Grid.SetColumn(logo, 1);
        Grid.SetColumn(title, 2);
        Grid.SetColumn(_newChat, 3);
        Grid.SetColumn(_gear, 4);
        head.Children.Add(_collapse);
        if (logo != null) head.Children.Add(logo);
        head.Children.Add(title);
        head.Children.Add(_newChat);
        head.Children.Add(_gear);

        var headBorder = new Border
        {
            Child = head,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(6, 0, 6, 0),
        };
        headBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        headBorder.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        // ---- 消息区（_list 已在字段声明处 new 好，这里只把它塞进滚动容器）
        _scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // ⚠️ 自动滚动必须看用户在哪儿。
        // 原来不管三七二十一每来一段就 ScrollToEnd()，结果模型一边输出一边把
        // 视图往下拉 —— 用户想往上翻看思考过程，刚滑上去就被拽回来，等于滑不动。
        // 现在只有「本来就贴在底部」才跟随；用户一往上翻就停止跟随，
        // 滑回底部再自动恢复。这是所有聊天界面的通行做法。
        _scroll.ScrollChanged += (_, _) =>
        {
            double gap = _scroll.ExtentHeight - _scroll.ViewportHeight - _scroll.VerticalOffset;
            _pinBottom = gap < 24;
            if (_jumpBottom != null)
                _jumpBottom.Visibility = _pinBottom ? Visibility.Collapsed : Visibility.Visible;
        };

        // 往上翻时右下角浮出「回到最新」，一键贴回底部
        _jumpBottom = new Button
        {
            Content = Lang.T("agent.jumpBottom"), FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4), Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.Hand,
        };
        _jumpBottom.Click += (_, _) => { _pinBottom = true; _scroll.ScrollToEnd(); };

        // ---- 欢迎页
        _welcome = BuildWelcome();

        var body = new Grid();
        body.Children.Add(_scroll);
        body.Children.Add(_welcome);
        body.Children.Add(_jumpBottom);

        // ---- 输入区（描边跑流光）
        _input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120, MinHeight = 34, Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13,
        };
        _input.SetResourceReference(TextBox.ForegroundProperty, "Brush.Fg.Primary");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "Brush.Fg.Primary");
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter &&
                !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
            {
                e.Handled = true;
                _ = SendAsync();
            }
        };
        // 发送用强调色：Brush.Fg.Muted 在深色模式下太淡，几乎看不见
        _send = IconButton("send", Lang.T("agent.send"), "Brush.Accent");
        _send.IsEnabled = false;
        // 必须先给 _send 赋值再挂事件：lambda 里读字段时编译器不追踪字段的
        // 空值状态（它可能还没赋上），顺序反了就报 CS8602。
        _input.TextChanged += (_, _) => _send.IsEnabled = _input.Text.Trim().Length > 0 && !_busy;
        _send.Click += (_, _) => _ = SendAsync();

        _attach = IconButton("paperclip", Lang.T("agent.attach"));
        _attach.Click += (_, _) => AttachFiles();

        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.Children.Add(_input);
        Grid.SetColumn(_attach, 1);
        Grid.SetColumn(_send, 2);
        inputGrid.Children.Add(_attach);
        inputGrid.Children.Add(_send);

        var inputBorder = new Border
        {
            Child = inputGrid, CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5), Padding = new Thickness(6, 2, 4, 2),
            Margin = new Thickness(10, 4, 10, 10), SnapsToDevicePixels = true,
        };
        inputBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        // 流光：一层渐变描边在框上跑，下面再叠一层普通描边兜底（渐变没覆盖到的地方有边）
        inputBorder.BorderBrush = MakeFlowBrush();

        _hint = new TextBlock
        {
            Text = Lang.T("agent.hint"),
            FontSize = 10, Margin = new Thickness(12, 0, 12, 8), TextWrapping = TextWrapping.Wrap,
        };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(headBorder, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(inputBorder, 2);
        Grid.SetRow(_hint, 3);
        root.Children.Add(headBorder);
        root.Children.Add(body);
        root.Children.Add(inputBorder);
        root.Children.Add(_hint);

        var outer = new Border
        {
            Child = root,
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        outer.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Base");
        outer.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        Content = outer;

        // 自检：JS_CONFIRM_TEST=1 直接摆一张授权卡片出来，看排版对不对。
        // 正常这张卡是模型要求跑高危命令时才出现的，截图验证就得手动造一个。
        //
        // ⚠️ 别写在构造函数里：Attach() 会 Children.Clear() 重建列表，
        // 构造阶段塞进去的卡片照样被抹掉（踩过一次，截图里死活看不到卡片）。
        // 所以放到 Attach() 重建完之后，见那里的调用。
    }

    // ---------------------------------------------------------------- 流光描边

    /// <summary>
    /// 一条会跑的渐变描边。做法：三个色标（透明 / 强调色 / 透明），
    /// 让中间那个的 Offset 在 0→1 之间无限往返，看起来就是一道光顺着边框走。
    /// </summary>
    private static LinearGradientBrush MakeFlowBrush()
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };

        var head = new GradientStop(Colors.Transparent, 0.0);
        var light = new GradientStop(Colors.DodgerBlue, 0.0);
        var tail = new GradientStop(Colors.Transparent, 1.0);
        brush.GradientStops.Add(head);
        brush.GradientStops.Add(light);
        brush.GradientStops.Add(tail);

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.4))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true,
        };
        light.BeginAnimation(GradientStop.OffsetProperty, anim);
        return brush;
    }

    // ---------------------------------------------------------------- 欢迎页

    private Border BuildWelcome()
    {
        // Thickness 只有 0/1/4 三种参数个数，没有两参数的重载
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 18, 0) };

        var icon = Icons.Visual("sparkles", 34, "Brush.Fg.Dim");
        if (icon != null) { icon.HorizontalAlignment = HorizontalAlignment.Center; panel.Children.Add(icon); }

        var h = new TextBlock
        {
            Text = Lang.T("agent.welcome"), FontSize = 19, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 8),
        };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");

        var sub = new TextBlock
        {
            Text = Lang.T("agent.welcomeSub"),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");

        panel.Children.Add(h);
        panel.Children.Add(sub);
        return new Border { Child = panel, Background = Brushes.Transparent };
    }

    private void RefreshWelcome()
        => _welcome.Visibility = _list.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 切语言后把面板上的文案整体换一遍。
    /// 按钮的 ToolTip 和底部提示都是构造时写死的，不重设就会「界面英文、悬停中文」；
    /// 欢迎页整块重建（_welcome 是 readonly，只能换它的 Child）。
    /// 聊天记录本身是模型说的话，不动。
    /// </summary>
    public void RefreshLanguage()
    {
        _collapse.ToolTip = Lang.T("agent.collapse");
        _newChat.ToolTip = Lang.T("agent.newChat");
        _gear.ToolTip = Lang.T("agent.gear");
        _send.ToolTip = Lang.T("agent.send");
        _attach.ToolTip = Lang.T("agent.attach");
        _hint.Text = Lang.T("agent.hint");

        // ⚠️ 换 Child 前必须先把元素从「原父」上摘下来，而且两头都要摘。
        // BuildWelcome() 返回的是个 Border，它自己已经是里面那层内容的逻辑父级；
        // 光把 _welcome.Child 置空没用，新元素的父级还是那个临时 Border，
        // 一赋值就撞「指定的元素已经是另一个元素的逻辑子元素」（InvalidOperationException）。
        // 窗口 Loaded 阶段抛出来就是整个界面起不来，所以这里必须先把内层摘出来。
        var fresh = BuildWelcome();
        var inner = fresh.Child;
        fresh.Child = null;      // 从临时 Border 上断开
        _welcome.Child = null;   // 从旧位置上断开
        _welcome.Child = inner;
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>
    /// 图标按钮。颜色一律走主题里的画刷键名（SetResourceReference），
    /// 不用静态画刷 —— 用静态的切深色后不跟着变（发布按钮之前就栽在这）。
    /// </summary>
    private static Style? _iconBtnStyle;   // 懒加载缓存（可空，首次为空）

    /// <summary>
    /// 透明图标按钮的样式。**必须自己给模板**：主题里那条隐式 Button 样式带浅色底，
    /// 深色模式下就露一个白方块出来（发送按钮一直是这样）。
    /// 这里换成纯透明底 + 悬停一层淡灰 + 禁用半透明，两套主题都不会露白。
    /// </summary>
    private static Style IconBtnStyle()
    {
        if (_iconBtnStyle != null) return _iconBtnStyle;
        _iconBtnStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            // ⚠️ 两个命名空间都要声明：只用默认 xmlns 却写 x:Name，
            // XamlReader.Parse 会抛「'x' is an undeclared prefix」——
            // 这是在 MainWindow 构造函数里触发的，等于一启动就崩，exe 直接打不开。
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "  <Setter Property='Background' Value='Transparent'/>" +
            "  <Setter Property='BorderThickness' Value='0'/>" +
            "  <Setter Property='Padding' Value='0'/>" +
            "  <Setter Property='Template'>" +
            "    <Setter.Value>" +
            "      <ControlTemplate TargetType='Button'>" +
            "        <Border x:Name='Bd' Background='Transparent' CornerRadius='6'>" +
            "          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
            "        </Border>" +
            "        <ControlTemplate.Triggers>" +
            "          <Trigger Property='IsMouseOver' Value='True'>" +
            "            <Setter TargetName='Bd' Property='Background' Value='#26808080'/>" +
            "          </Trigger>" +
            "          <Trigger Property='IsEnabled' Value='False'>" +
            "            <Setter Property='Opacity' Value='0.4'/>" +
            "          </Trigger>" +
            "        </ControlTemplate.Triggers>" +
            "      </ControlTemplate>" +
            "    </Setter.Value>" +
            "  </Setter>" +
            "</Style>");
        return _iconBtnStyle;
    }

    private Button IconButton(string icon, string tip, string brushKey = "Brush.Fg.Muted")
    {
        var b = new Button
        {
            Width = 26, Height = 26, Margin = new Thickness(2, 0, 2, 0),
            Style = IconBtnStyle(),          // 自定义透明模板，别继承主题那条带浅色底的
            ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand,
        };
        var v = Icons.Visual(icon, 15, brushKey);
        b.Content = v ?? (FrameworkElement)new TextBlock { Text = "·" };
        return b;
    }

    private Brush B(string key) => Ui.B(key);

    // ---------------------------------------------------------------- 挂到项目

    /// <summary>打开项目时把面板接到这个项目上（会话、工具沙箱都跟着项目走）。</summary>
    public void Attach(string projectRoot)
    {
        _root = projectRoot ?? "";
        _session = AgentSession.For(_root);
        _tools = new AgentTools(_root);
        // 工具跑在后台线程，确认钩子必须自己切回 UI 线程再弹窗
        _tools.Confirm = (title, detail) => ConfirmOnUi(title, detail);

        _list.Children.Clear();
        foreach (var m in _session.Messages) RenderHistory(m);
        RefreshWelcome();
        ScrollToEnd();

        // 自检钩子放最后：构造函数里加会被上面那句 Clear() 抹掉。
        if (Environment.GetEnvironmentVariable("JS_CONFIRM_TEST") == "1")
            AddConfirmCard(Lang.T("agent.confirmDanger"),
                           Lang.T("agent.cmdPrefix") + "mvn clean package",
                           _ => { });
    }

    private void RenderHistory(ChatMsg m)
    {
        if (m.Role == "user") { AddUserBubble(m.Content); return; }
        if (m.Role == "tool") { AddToolCard(m.ToolName, m.Content, true); return; }
        if (m.Role == "assistant")
        {
            // 思考过程排在正文前面，跟对话时看到的顺序一致。
            // 历史里的都是已经想完的，直接收起、不显示用时。
            if (!string.IsNullOrWhiteSpace(m.Reasoning))
                AddThinkingBlock(m.Reasoning,
                                 startOpen: Environment.GetEnvironmentVariable("JS_THINK_OPEN") == "1")
                    .MarkDone(withTiming: false);
            if (!string.IsNullOrWhiteSpace(m.Content)) AddAssistantBubble(m.Content);
        }
    }

    /// <summary>
    /// 贴底滚动。用户已经往上翻看历史时就不动 —— 否则模型一边输出一边把视图拽下去，
    /// 用户根本没法往上读（这就是「还在输出，但滑不上去」的原因）。
    /// </summary>
    private void ScrollToEnd()
    {
        if (!_pinBottom) return;
        Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- 气泡

    private void AddUserBubble(string text)
    {
        // 线程闸门：谁都可能调到这里（工具回调、定时器…），
        // 不在 UI 线程就先切回去，免得抛「调用线程无法访问此对象」。
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddUserBubble(text)); return; }

        var tb = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        var card = new Border
        {
            Child = tb, CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(28, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        card.BorderThickness = new Thickness(1);
        _list.Children.Add(card);
        RefreshWelcome();
    }

    /// <summary>助手气泡。返回的 TextBlock 可以边流式边 Append。</summary>
    private TextBlock AddAssistantBubble(string initial = "")
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => AddAssistantBubble(initial));

        var tb = new TextBlock { Text = initial, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        _list.Children.Add(tb);
        RefreshWelcome();
        ScrollToEnd();
        return tb;
    }

    /// <summary>
    /// 思考过程的可折叠区块。默认收起 —— 思考往往比正文长好几倍，
    /// 全摊开会把它后面真正要说的话挤到看不见的地方。
    ///
    /// 返回的 TextBlock 是正文容器，流式阶段可以一边收一边 Append。
    /// </summary>
    /// <summary>
    /// 思考过程那一块，仿 DeepSeek 的「深度思考」。
    ///
    /// 标题行是状态条而不是死文字：思考中显示「正在深度思考…」，
    /// 结束后换成「已深度思考（用时 3.2 秒）」。
    /// 内容区左侧一条竖线、灰字小号，跟正文明确分开。
    ///
    /// startOpen：思考**中**默认展开（能看到它在动，这是 DeepSeek 的做法）；
    /// 结束后自动收起 —— 但用户手动点过就尊重用户的决定，不再自动收。
    /// </summary>
    private ThinkingBlock AddThinkingBlock(string initial = "", bool startOpen = true)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => AddThinkingBlock(initial, startOpen));

        // 自检开关：JS_THINK_OPEN=1 强制展开（截展开态的图看排版）
        bool forceOpen = Environment.GetEnvironmentVariable("JS_THINK_OPEN") == "1";
        if (forceOpen) startOpen = true;

        var b = new ThinkingBlock(_list, initial, startOpen) { KeepOpen = forceOpen };
        RefreshWelcome();
        ScrollToEnd();
        return b;
    }

    /// <summary>思考过程卡片。见 AddThinkingBlock 的说明。</summary>
    private sealed class ThinkingBlock
    {
        private readonly StackPanel _row;      // 竖线 + 正文那一行
        private readonly TextBlock _label;
        private readonly ContentControl _arrow;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _open;
        private bool _done;
        private bool _userTouched;             // 用户手动点过，就不再自动收起
        private double? _secs;                 // null = 不显示用时（历史消息）

        public Border Card { get; }
        public TextBlock Body { get; }
        /// <summary>自检用：思考结束后仍然保持展开（正常会自动收起）。</summary>
        public bool KeepOpen { get; init; }

        public ThinkingBlock(StackPanel host, string initial, bool startOpen)
        {
            Body = new TextBlock
            {
                Text = initial, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 6, 2), Opacity = 0.9,
            };
            Body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");

            _arrow = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
            _label = new TextBlock { FontSize = 11.5, Margin = new Thickness(4, 0, 0, 0) };
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(_arrow);
            head.Children.Add(_label);

            // 整行都能点：用 Button 自绘成透明底（键盘能聚焦、有按下反馈），
            // 别给 StackPanel 挂 MouseDown —— 那会吞掉子元素的点击。
            var btn = new Button
            {
                Content = head, HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Cursor = Cursors.Hand,
                ToolTip = Lang.T("agent.thinkingTip"),
            };
            btn.Click += (_, _) => { _userTouched = true; SetOpen(!_open); };

            // 内容左边那条竖线：跟 DeepSeek 的引用块一个意思，
            // 让人一眼看出「这段是过程，不是结论」。
            var line = new Border { Width = 2, Margin = new Thickness(3, 2, 8, 2) };
            line.SetResourceReference(Border.BackgroundProperty, "Brush.Border");

            _row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            _row.Children.Add(line);
            _row.Children.Add(Body);

            var box = new StackPanel();
            box.Children.Add(btn);
            box.Children.Add(_row);

            Card = new Border
            {
                Child = box, CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 2, 24, 4), BorderThickness = new Thickness(1),
            };
            Card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
            Card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

            _open = startOpen;
            ApplyOpen();
            UpdateLabel();
            host.Children.Add(Card);
        }

        private void SetOpen(bool open) { _open = open; ApplyOpen(); }

        private void ApplyOpen()
        {
            // 连整行一起收，不然竖线会孤零零留在外面
            _row.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
            // ⚠️ 换 Content 前先把旧的摘下来：Icons.Visual 返回的元素被塞进
            // 另一个父级会撞「指定的元素已经是另一个元素的逻辑子元素」。
            _arrow.Content = null;
            _arrow.Content = Icons.Visual(_open ? "chevron-down" : "chevron-right", 12, "Brush.Fg.Dim");
        }

        private void UpdateLabel()
        {
            if (!_done) { _label.Text = Lang.T("agent.thinkingNow"); return; }
            _label.Text = _secs.HasValue
                ? string.Format(Lang.T("agent.thoughtDone"), _secs.Value.ToString("0.0"))
                : Lang.T("agent.thoughtPlain");
        }

        /// <summary>
        /// 思考结束：停表、换标题，没被手动点过就自动收起。
        /// withTiming=false 用于渲染历史 —— 那时已经没有用时可算了，只显示「已深度思考」。
        /// </summary>
        public void MarkDone(bool withTiming = true)
        {
            _sw.Stop();
            _done = true;
            _secs = withTiming ? _sw.ElapsedMilliseconds / 1000.0 : (double?)null;
            if (!_userTouched && !KeepOpen) SetOpen(false);
            UpdateLabel();
        }
    }

    private void AddToolCard(string name, string detail, bool done)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddToolCard(name, detail, done)); return; }

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var ic = Icons.Visual(done ? "check-circle" : "loader", 13,
                              done ? "Brush.Ok" : "Brush.Fg.Dim");
        if (ic != null) head.Children.Add(ic);
        var t = new TextBlock
        {
            Text = "  " + (string.IsNullOrEmpty(name) ? Lang.T("agent.tool") : name),
            FontSize = 11.5
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        head.Children.Add(t);

        var body = new TextBlock { Text = Truncate(detail, 400), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(18, 3, 0, 0), Opacity = 0.85 };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");

        var box = new StackPanel { Margin = new Thickness(0, 3, 20, 3) };
        box.Children.Add(head);
        box.Children.Add(body);

        var card = new Border
        {
            Child = box, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 3, 0, 3), BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _list.Children.Add(card);
        card.Tag = body;
        RefreshWelcome();
        ScrollToEnd();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + " …");

    private void AddError(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddError(msg)); return; }

        var tb = new TextBlock { Text = "⚠ " + msg, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
        _list.Children.Add(tb);
        RefreshWelcome();
        ScrollToEnd();
    }

    // ---------------------------------------------------------------- 发送 / 主循环

    private async Task SendAsync()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0 || _busy || _session == null) return;

        _input.Clear();
        _session.Add(new ChatMsg { Role = "user", Content = text });
        AddUserBubble(text);

        await RunAsync();
    }

    private async Task RunAsync()
    {
        // 强制先填 API：不配好就把设置弹窗顶出来（必填模式），填完才继续
        if (!EnsureApi())
        {
            AddError(Lang.T("agent.noApi"));
            return;
        }
        var settings = AgentSettings.Current;

        // 抓成局部变量再用：编译器不追踪字段在 await / lambda 之后的空值状态，
        // 直接一路用 _session 会报一堆 CS8602。
        var session = _session;
        if (session == null) return;

        _busy = true; _send.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var client = new AgentClient(settings);

        try
        {
            for (int round = 0; round < Math.Max(1, settings.MaxToolRounds); round++)
            {
                var bubble = AddAssistantBubble();

                // 思考过程走单独一块，流式阶段才知道模型到底给不给，
                // 所以等第一段真的来了再建块 —— 不支持思考的模型不会白占一行。
                ThinkingBlock? thinkBox = null;
                var raw = new StringBuilder();        // 正文原始累计（可能混着 <think>）
                var fieldThink = new StringBuilder(); // 来自 reasoning_content 的
                var tagThink = new StringBuilder();   // 从正文 <think> 块里切出来的
                void ShowThink()
                {
                    if (fieldThink.Length == 0 && tagThink.Length == 0) return;
                    thinkBox ??= AddThinkingBlock();
                    string a = fieldThink.ToString(), b = tagThink.ToString();
                    thinkBox.Body.Text = a.Length > 0 && b.Length > 0 ? a + "\n" + b : a + b;
                    ScrollToEnd();
                }

                var turn = await client.ChatAsync(
                    session.Messages, _root,
                    delta => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 边收边切：气泡里只留真正要说的话，
                        // 混在正文里的 <think> 块抽出来塞进思考区。
                        raw.Append(delta);
                        var (body, thought) = Think.Split(raw.ToString());
                        bubble.Text = body;
                        if (thought.Length > 0)
                        {
                            tagThink.Clear();
                            tagThink.Append(thought);
                            ShowThink();
                        }
                        ScrollToEnd();
                    })),
                    _cts.Token,
                    piece => Dispatcher.BeginInvoke(new Action(() =>
                    {
                        fieldThink.Append(piece);
                        ShowThink();
                    })));

                // 收尾以最终结果为准（客户端那边也切过一次，两边一致）
                if (!string.IsNullOrEmpty(turn.Content))
                    // BeginInvoke 返回 DispatcherOperation（可等待），不 await 要显式丢弃，
                    // 否则编译器报 CS4014。
                    _ = Dispatcher.BeginInvoke(new Action(() => bubble.Text = turn.Content));

                // 思考结束：标题换成「已深度思考（用时 X 秒）」，没手动展开过就收起来
                if (thinkBox != null)
                    _ = Dispatcher.BeginInvoke(new Action(() => thinkBox.MarkDone()));

                session.Add(new ChatMsg
                {
                    Role = "assistant",
                    Content = turn.Content,
                    Reasoning = turn.Reasoning,
                    ToolCalls = turn.HasToolCalls ? turn.ToolCalls : null
                });

                if (!turn.HasToolCalls) break;

                // 工具在后台线程跑，免得长命令卡住界面
                foreach (var call in turn.ToolCalls)
                {
                    if (_cts.IsCancellationRequested) break;
                    AddToolCard(call.Name, call.ArgsJson, false);
                    var card = _list.Children[_list.Children.Count - 1] as Border;
                    var res = await Task.Run(() => _tools!.Invoke(call));
                    session.Add(new ChatMsg
                    {
                        Role = "tool", ToolCallId = res.ToolCallId,
                        ToolName = res.Name, Content = res.Content
                    });
                    if (card?.Tag is TextBlock body)
                        // BeginInvoke 返回 DispatcherOperation（可等待），不 await 要显式丢弃，
                        // 否则编译器报 CS4014。
                        _ = Dispatcher.BeginInvoke(new Action(() => body.Text = Truncate(res.Content, 400)));
                }
                if (_cts.IsCancellationRequested) break;
            }
        }
        catch (OperationCanceledException) { AddError(Lang.T("agent.stopped")); }
        catch (Exception ex) { AddError(ex.Message); }
        finally
        {
            _busy = false;
            _send.IsEnabled = _input.Text.Trim().Length > 0;
            _cts?.Dispose(); _cts = null;
        }
    }

    public void Stop() { try { _cts?.Cancel(); } catch { } }

    /// <summary>展开面板后把光标放进输入框，省得再点一下。</summary>
    public void FocusInput()
        => Dispatcher.BeginInvoke(new Action(() => _input.Focus()),
                                  System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>
    /// 附上文件：用自绘选择器挑文件，把内容按「文件名 + 代码块」贴进输入框，
    /// 跟用户自己打的字一起发给模型。路径尽量显示成相对项目的形式。
    /// 选文件同样不走系统弹窗（Pickers）。
    /// </summary>
    private void AttachFiles()
    {
        var files = Pickers.PickFile(Window.GetWindow(this), Lang.T("agent.attachTitle"), "*.*",
                                     multi: true,
                                     start: string.IsNullOrEmpty(_root) ? "" : _root);
        if (files.Length == 0) return;

        var sb = new StringBuilder();
        foreach (var f in files)
        {
            string rel = !string.IsNullOrEmpty(_root) &&
                         f.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
                ? f.Substring(_root.Length).TrimStart('\\', '/')
                : f;

            sb.Append(string.Format(Lang.T("agent.fileBlock"), rel));
            try
            {
                string txt = File.ReadAllText(f, Encoding.UTF8);
                // 别把整个大文件塞爆上下文
                if (txt.Length > 60_000) txt = txt.Substring(0, 60_000) + Lang.T("agent.truncated");
                sb.Append(txt);
            }
            catch (Exception ex)
            {
                sb.Append(Lang.T("agent.readFail")).Append(ex.Message).Append("）");
            }
            sb.Append("\n```");
        }

        _input.AppendText(sb.ToString());
        _input.Focus();
        _input.CaretIndex = _input.Text.Length;
    }

    // ---------------------------------------------------------------- 弹窗

    private void OpenSettings()
    {
        var d = new AgentSettingsDialog { Owner = Window.GetWindow(this) };
        d.ShowDialog();
    }

    /// <summary>
    /// 强制先填 API：没配好就把设置弹窗顶到前台（必填模式，取消按钮是禁用的），
    /// 填完并保存才返回 true。不填就返回 false —— 调用方据此拦住后续动作。
    /// 工具线程也可能调到这儿，所以自己判断线程并切回 UI 再弹窗。
    /// </summary>
    public bool EnsureApi()
    {
        if (AgentSettings.Current.Ready) return true;

        bool ok = false;
        Action show = () =>
        {
            var d = new AgentSettingsDialog(required: true) { Owner = Window.GetWindow(this) };
            ok = d.ShowDialog() == true && AgentSettings.Current.Ready;
        };
        if (Dispatcher.CheckAccess()) show();
        else Dispatcher.Invoke(show);
        return ok;
    }

    /// <summary>工具在后台线程上调这个，所以必须切回 UI 线程弹窗并等结果。</summary>
    /// <summary>
    /// 高危命令的授权。放在面板里做成一张卡片，**不再弹窗**。
    ///
    /// 为什么改：弹窗会把视线从对话里整个拽走，而且一轮里连续几次授权时
    /// 要反复开关窗；用户明确要求 Agent 相关的交互都留在 Agent 面板内。
    /// 做成卡片后，允许/拒绝就发生在消息流里，上下文还在眼前。
    ///
    /// ⚠️ 这里是阻塞等待：调用方是后台线程上的同步 Invoke()，所以能这么等。
    /// 千万别搬到 UI 线程上调，会死锁。
    /// </summary>
    private bool ConfirmOnUi(string title, string detail)
    {
        using var gate = new ManualResetEventSlim(false);
        bool ok = false;

        Dispatcher.Invoke(() => AddConfirmCard(title, detail, r =>
        {
            ok = r;
            gate.Set();
        }));

        gate.Wait();
        return ok;
    }

    /// <summary>
    /// 一张「要不要执行」的卡片，带允许/拒绝两个按钮。
    /// 点完就把按钮换掉、留下结果文字，不删卡片 ——
    /// 事后回看时得知道这一步是被放行还是被拦下的。
    /// </summary>
    private void AddConfirmCard(string title, string detail, Action<bool> done)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => AddConfirmCard(title, detail, done)); return; }

        var head = new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");

        var cmd = new TextBlock
        {
            Text = detail, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(0, 5, 0, 8),
        };
        cmd.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");

        var verdict = new TextBlock { FontSize = 11.5, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };

        // 「允许」是红色的（DlgBtnDanger），不是普通主按钮：
        // 这一步放行的是高危命令，颜色得把代价摆出来。
        var allow = Ui.BtnDanger(Lang.T("agent.allow"));
        var deny = Ui.Btn(Lang.T("agent.deny"));

        void Finish(bool result)
        {
            allow.IsEnabled = false;
            deny.IsEnabled = false;
            verdict.Text = result ? Lang.T("agent.allowed") : Lang.T("agent.deniedNow");
            verdict.SetResourceReference(TextBlock.ForegroundProperty,
                                         result ? "Brush.Ok" : "Brush.Fg.Dim");
            verdict.Visibility = Visibility.Visible;
            _pendingConfirm = null;   // 结案了，别再被「新对话」重复触发一次
            done(result);
        }

        _pendingConfirm = Finish;

        allow.Click += (_, _) => Finish(true);
        deny.Click += (_, _) => Finish(false);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        btns.Children.Add(deny);
        btns.Children.Add(allow);

        var box = new StackPanel();
        box.Children.Add(head);
        box.Children.Add(cmd);
        box.Children.Add(btns);
        box.Children.Add(verdict);

        var card = new Border
        {
            Child = box, CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 20, 4), BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Warn");

        _list.Children.Add(card);
        RefreshWelcome();
        // 授权卡片必须让用户看见，这里强制贴底一次（不管他之前有没有往上翻）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _pinBottom = true;
            _scroll.ScrollToEnd();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
}

// ================================================================ 设置弹窗

internal sealed class AgentSettingsDialog : DlgPanel
{
    private readonly TextBox _base = Ui.Input("https://api.deepseek.com", 420);
    // ⚠️ 固定宽度的控件一律显式 Left：WPF 默认 Stretch，Stretch 加显式 Width
    // 会**退化成居中**，在卡片这种宽面板里就飘到中间去了（见 Ui.Input 那段说明）。
    private readonly PasswordBox _key = new()
    {
        Width = 420, HorizontalAlignment = HorizontalAlignment.Left
    };
    // 模型让用户从下拉里选（IsEditable，也能自己敲一个不在列表里的名字）。
    // 点「拉取模型列表」会用当前填的 Base URL + Key 去问接口要完整列表。
    private readonly ComboBox _model = new()
    {
        Width = 330, IsEditable = true, FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _modelTip = new()
    {
        FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
    };
    private readonly TextBox _temp = Ui.Input("0.2", 420);
    private readonly TextBox _rounds = Ui.Input("12", 420);
    private readonly CheckBox _confirm = Ui.Check(Lang.T("agent.confirm"), true);

    /// <summary>
    /// required=true 是「必填模式」：还没配好 API 时弹出来的那种。
    /// 取消按钮会禁用（只能填完保存，或者关窗放弃），
    /// 这样才叫「强制先输入 API」——不填就用不了 Agent。
    /// </summary>
    private readonly bool _required;

    /// <summary>进来时读到的模型名。用于在 Loaded 之后再补一次赋值，见下。</summary>
    private readonly string _modelInit;

    public AgentSettingsDialog(bool required = false)
    {
        _required = required;

        var s = AgentSettings.Current;
        _base.Text = s.BaseUrl;
        _key.Password = s.ApiKey;
        _model.Text = s.Model;
        _modelInit = s.Model;
        _temp.Text = s.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _rounds.Text = s.MaxToolRounds.ToString();
        _confirm.IsChecked = s.ConfirmHighRisk;

        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        // 内嵌时主窗口那条标题栏已经写了标题，内容里这个就不必再来一遍
        if (Popout) root.Children.Add(Ui.Label(Lang.T("agent.settings"), 15));

        if (required)
        {
            var must = new TextBlock
            {
                Text = Lang.T("agent.required"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };
            must.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warn");
            root.Children.Add(must);
        }

        var tip = new TextBlock
        {
            Text = Lang.T("agent.tip"),
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        root.Children.Add(tip);

        root.Children.Add(Ui.Label("Base URL")); root.Children.Add(_base);
        root.Children.Add(Ui.Label("API Key")); root.Children.Add(_key);
        // 预设 DeepSeek 在用的两个；点「拉取模型列表」会用当前填的地址和密钥去问接口。
        foreach (var m in new[] { "deepseek-v4-pro", "deepseek-flash" })
            if (!_model.Items.Contains(m)) _model.Items.Add(m);

        root.Children.Add(Ui.Label(Lang.T("agent.model")));
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal };
        modelRow.Children.Add(_model);
        var fetch = Ui.Btn(Lang.T("agent.fetchModels"));
        fetch.Click += async (_, _) => await FetchModels(fetch);
        modelRow.Children.Add(fetch);
        root.Children.Add(modelRow);
        _modelTip.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        root.Children.Add(_modelTip);
        root.Children.Add(Ui.Label(Lang.T("agent.temp"))); root.Children.Add(_temp);
        root.Children.Add(Ui.Label(Lang.T("agent.rounds"))); root.Children.Add(_rounds);
        root.Children.Add(_confirm);

        var where = new TextBlock
        {
            Text = Lang.T("agent.savedAt") + System.IO.Path.Combine(Core.AppDataDir, "agent.json"),
            FontSize = 10.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        where.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        root.Children.Add(where);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = Ui.Btn(Lang.T("msg.cancel"));
        cancel.IsEnabled = !_required;      // 必填模式下不给「取消」这条退路
        cancel.Click += (_, _) => CloseWith(false);
        var ok = Ui.Btn(Lang.T("agent.save"), true); ok.Click += (_, _) => { if (Save()) CloseWith(true); };
        row.Children.Add(cancel); row.Children.Add(ok);
        root.Children.Add(row);

        WantWidth = 640;                      // Agent 设置
        Install(root);
        Title = _required ? Lang.T("agent.cfgTitle") : Lang.T("agent.gear");
        // 必填模式下不给退路：左上角那个「返回」也一起收掉，否则点一下就绕过去了
        AllowBack = !_required;

        // ⚠️ 可编辑 ComboBox 的 Text 是存在模板里的 PART_EditableTextBox 上的，
        // 而构造函数执行时模板还没应用（那个 TextBox 还不存在），
        // 所以上面 `_model.Text = s.Model` 这一次会被丢掉 —— 界面打开是空的。
        // 等窗口真正加载、模板应用完之后再补一次，模型名才显示得出来。
        // （保存侧那边同理：如果模板里没有 PART_EditableTextBox，
        //   _model.Text 读出来永远是空串，模型就存不下去。两处都依赖模板修好。）
        //
        // ⚠️ 挂载点从 `Loaded +=` 换成了 override OnShown()：内嵌时这个 Window 不会
        // 被 Show()，Loaded 永远不触发，模型名就一直是空的（看起来像设置没存上）。
    }

    internal override void OnShown() => _model.Text = _modelInit;

    /// <summary>
    /// 自检用：模拟「把模型改成 model 再点保存」，验证 Text 读得到、存得下。
    /// 可编辑下拉框曾经因为模板缺 PART_EditableTextBox，Text 读出来永远是空串，
    /// 保存等于把模型清空 —— 这个入口就是用来盯住那个回归的。
    /// </summary>
    internal bool SaveWithModelForTest(string model)
    {
        _model.Text = model;
        return Save();
    }

    private bool Save()
    {
        var s = AgentSettings.Current;
        s.BaseUrl = _base.Text.Trim();
        s.ApiKey = _key.Password.Trim();
        s.Model = _model.Text.Trim();
        s.ConfirmHighRisk = _confirm.IsChecked == true;

        if (double.TryParse(_temp.Text.Trim(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double tv))
            s.Temperature = Math.Clamp(tv, 0, 2);
        if (int.TryParse(_rounds.Text.Trim(), out int rv)) s.MaxToolRounds = Math.Clamp(rv, 1, 50);

        if (!s.Ready)
        {
            AppMsg.Show(this, Lang.T("agent.gear"), Lang.T("agent.needBoth"));
            return false;
        }
        s.Save();
        return true;
    }

    /// <summary>
    /// 用对话框里「当前填的」Base URL + Key 去 /v1/models 拉一次可用模型，
    /// 让用户从真实列表里挑，而不是对着一个空输入框猜模型名。
    /// 用的是界面上的现值（不是已保存的设置），所以改完地址立刻能拉。
    /// </summary>
    private async Task FetchModels(Button btn)
    {
        string baseUrl = (_base.Text ?? "").Trim().TrimEnd('/');
        string key = _key.Password.Trim();
        if (baseUrl.Length == 0 || key.Length == 0)
        {
            _modelTip.Text = Lang.T("agent.fetchNeedBoth");
            return;
        }
        if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl += "/v1";

        btn.IsEnabled = false;
        _modelTip.Text = Lang.T("agent.fetching");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + key);
            string json = await http.GetStringAsync(baseUrl + "/models");

            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                    if (it.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String)
                    {
                        string? id = idv.GetString();
                        if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    }
            }
            ids = ids.Distinct().OrderBy(x => x).ToList();

            if (ids.Count == 0)
            {
                _modelTip.Text = Lang.T("agent.fetchEmpty");
                return;
            }

            string keep = _model.Text ?? "";
            _model.Items.Clear();
            foreach (var id in ids) _model.Items.Add(id);
            _model.Text = ids.Contains(keep) ? keep : ids[0];
            _modelTip.Text = string.Format(Lang.T("agent.fetchOk"), ids.Count);
        }
        catch (Exception ex)
        {
            _modelTip.Text = Lang.T("agent.fetchFail") + ex.Message + Lang.T("agent.fetchFailHint");
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }
}

// 这里原本有个 AgentConfirmDialog：模态弹窗问「要不要跑这条高危命令」。
// 现在改成面板内的授权卡片了（AddConfirmCard）—— 弹窗会把整块界面挡住、
// 焦点也跳走，而授权本来就是对话的一步，应该在对话流里。
// 整个类没有任何引用了，删掉，免得以后有人又把它接回去。
