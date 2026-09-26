// LibsDialog.cs —— 第三方库（项目依赖 + 本地缓存）
//
// 取代原来那个「59 个内置库打勾」的 LibrariesDialog。旧版的问题不是样式，
// 是它把三件事混在了一个勾选列表里：依赖写在哪、能加哪些库、本机下了什么。
// 结果就是「勾了但没生效」（依赖其实得写在 pom.xml 里）和「想加个表外的
// 库无从下手」。这一版按这三件事分成两页：
//
//   项目依赖 —— 左边挑、右边看当前有哪些，双栏
//     来源跟着项目走：有 pom.xml 就写 pom（Maven 工程该有的样子），
//     没有就退回 .javastudio\libs.txt（纯 javac 工程）。两条路都记，
//     因为没装 mvn 的时候构建会退回 javac。
//     库不限于内置那 59 个：能搜 Maven 中央仓库，也能手输坐标。
//   本地缓存 —— 已经下到本机的 jar，能看大小、能删
//
// ⚠️ 两处结构上的坑，改这个文件时要守住：
//  1. 两页各只**造一次**，缓存起来反复显示。每次切页都重建的话，
//     事件会重复订阅（搜一次刷新两遍、点一下触发两次）。
//  2. 网络相关的调用（搜仓库、拉版本列表、下载）一律走后台线程。
//     它们是同步 P/Invoke，几十秒不返回很正常，在 UI 线程上调就是「未响应」。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JavaStudio;

internal sealed class LibsDialog : DlgPanel
{
    // ---------------------------------------------------------------- 状态

    private sealed class Dep
    {
        public string Group = "";
        public string Artifact = "";
        public string Version = "";
        public bool Ready;      // jar 在本机找得到
        public bool Busy;       // 正在下载

        public string Coord => Group + ":" + Artifact + ":" + Version;
        public string Key => Group + ":" + Artifact;
    }

    private readonly string _root;
    private readonly bool _hasPom;
    private readonly List<Core.LibEntry> _catalog;
    private readonly List<Dep> _deps = new();
    private readonly HashSet<string> _pomKeysAtOpen = new(StringComparer.Ordinal);

    // 左栏（候选）
    private readonly TextBox _find = Ui.Input("", 190);
    private readonly TextBox _cg = Ui.Input("", 118);
    private readonly TextBox _ca = Ui.Input("", 118);
    private readonly TextBox _cv = Ui.Input("", 92);
    private readonly StackPanel _cand = new();
    private readonly TextBlock _candInfo = Ui.Label("", 11);
    private readonly Button _backBtn = MiniBtn(Lang.T("lib.backCatalog"));
    private bool _showingRepo;                       // 左栏现在显示的是仓库结果
    private bool _searching;
    private string _failText = "";                   // 上次搜仓库失败的原因，空串=没失败
    private List<Core.SearchHit> _repoHits = new();
    private bool _autoSearch;                // 自检：进来自动搜一次

    // 右栏（已选）
    private readonly StackPanel _depList = new();
    private readonly TextBlock _depInfo = Ui.Label("", 11);

    // 缓存页
    private readonly StackPanel _cacheList = new();
    private readonly TextBlock _cacheInfo = Ui.Label("", 11);

    private readonly Grid _stage = new();
    private readonly Border[] _nav = new Border[2];
    private readonly TextBlock[] _navText = new TextBlock[2];
    private UIElement[] _pages = new UIElement[2];   // 各造一次，别反复重建
    private int _page;

    public LibsDialog(string root)
    {
        _root = root;
        _hasPom = Core.LibsPomExists(root);
        _catalog = Core.Libs();

        foreach (var c in Core.LibsPomCoords(root))
        {
            var p = Core.SplitCoord(c);
            if (p != null) _pomKeysAtOpen.Add(p[0] + ":" + p[1]);
        }
        foreach (var c in Core.LibsEffective(root))
        {
            var p = Core.SplitCoord(c);
            if (p == null) continue;
            _deps.Add(new Dep { Group = p[0], Artifact = p[1], Version = p[2] });
        }

        Title = Lang.T("lib.title");

        // ⚠️ 中间那一行是 Star，它必须拿到一个**有限**的高度，里面两条列表才会滚。
        // 高度从容器来：OwnScroll=true 让这一页直接顶到卡片第二行（不套 ScrollViewer），
        // 行高就是「卡片高度 - 标题栏」，永远是个定数。
        //
        // 别再给个 MinHeight 兜底了：MinHeight 一旦超过容器实际能给的高度，
        // 又没有外层 ScrollViewer 兜着，就是**硬裁**——「应用 / 取消」那排按钮
        // 被切掉，还滚不回来。以前那么写是因为外面那层 ScrollViewer 还能救一把。
        var wrap = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        wrap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        wrap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        wrap.Children.Add(PageTitle("lib.title", 18));

        var mid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        mid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mid.Children.Add(BuildNav());

        mid.Children.Add(_stage);
        Grid.SetColumn(_stage, 1);
        wrap.Children.Add(mid);
        Grid.SetRow(mid, 1);

        var foot = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0) };
        var cancel = Ui.BtnKey("msg.cancel");
        cancel.Click += (_, _) => CloseWith(false);
        var ok = Ui.BtnKey("lib.apply", true);
        ok.Click += (_, _) => { if (Apply()) CloseWith(true); };
        foot.Children.Add(cancel); foot.Children.Add(ok);
        wrap.Children.Add(foot);
        Grid.SetRow(foot, 2);

        Lang.Tag(this, "lib.title");
        // 左右两栏写死才不会一边高一边矮；OwnScroll：中间两条列表自己滚，外面不许滚
        WantWidth = 950; WantHeight = 650;
        OwnScroll = true;
        Install(wrap);

        // 自检用：JS_LIBS_PAGE=cache 直接落在「本地缓存」页，省得脚本去点导航；
        // JS_LIBS_SEARCH=xxx 替脚本把「搜 Maven 仓库」点一次（这条链路要发网络请求，
        // 靠脚本去模拟点击不稳，直接在构造末尾触发一次）。
        if (string.Equals(Environment.GetEnvironmentVariable("JS_LIBS_PAGE"),
                          "cache", StringComparison.OrdinalIgnoreCase)) _page = 1;
        PaintNav();

        string pre = Environment.GetEnvironmentVariable("JS_LIBS_SEARCH");
        if (!string.IsNullOrWhiteSpace(pre) && _page == 0)
        {
            _find.Text = pre;
            _autoSearch = true;
        }
    }

    /// <summary>自检钩子：进来就替脚本点一次「搜 Maven 仓库」。</summary>
    internal override void OnShown()
    {
        if (_autoSearch) _ = SearchRepoAsync();
    }

    // ---------------------------------------------------------------- 导航

    private Border BuildNav()
    {
        var host = new StackPanel { Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 12, 0) };
        string[] keys = { "lib.nav.deps", "lib.nav.cache" };
        for (int i = 0; i < 2; i++)
        {
            int idx = i;
            var text = new TextBlock { Text = Lang.T(keys[i]), FontSize = 13,
                Margin = new Thickness(12, 8, 8, 8), Tag = keys[i] };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");

            var b = new Border { Child = text, CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 4), Cursor = System.Windows.Input.Cursors.Hand };
            b.MouseLeftButtonUp += (_, _) => { _page = idx; PaintNav(); };
            _nav[i] = b;
            _navText[i] = text;
            host.Children.Add(b);
        }
        return new Border { Child = host };
    }

    private void PaintNav()
    {
        for (int i = 0; i < _nav.Length; i++)
        {
            if (_nav[i] == null) continue;
            bool on = i == _page;
            _nav[i].SetResourceReference(Border.BackgroundProperty,
                on ? "Brush.Bg.Raised" : "Brush.Bg.Base");
            if (_navText[i] != null)
                _navText[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        _stage.Children.Clear();
        if (_pages[_page] == null)
            _pages[_page] = _page == 0 ? BuildDepsPage() : BuildCachePage();
        _stage.Children.Add(_pages[_page]);

        if (_page == 0)
        {
            RefreshDeps();
            RefreshCandidates();
        }
        else _ = RefreshCacheAsync();
    }

    // ---------------------------------------------------------------- 第 1 页：项目依赖（双栏）

    private Grid BuildDepsPage()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        g.Children.Add(BuildCandidates());

        // 左右栏之间的那条竖线。用 Border 拉到满高：放在 Grid 里它会跟着
        // 最高的那一栏撑开，看着就是「从顶到底」的一条分界。
        var split = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 0) };
        split.SetResourceReference(Border.BackgroundProperty, "Brush.Border");
        g.Children.Add(split);
        Grid.SetColumn(split, 1);

        var sel = BuildSelected();
        sel.Margin = new Thickness(14, 0, 0, 0);
        g.Children.Add(sel);
        Grid.SetColumn(sel, 2);
        return g;
    }

    // ---- 左栏：挑库

    private DockPanel BuildCandidates()
    {
        var p = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.LabelKey("lib.cand", 13));

        // ⚠️ 这一行用 WrapPanel，不用 Grid（Star + Auto），也不能用横向 StackPanel。
        //
        //   横向 StackPanel：不给子元素宽度约束，东西加起来超宽时多出来的部分
        //     直接被裁掉，连报错都没有 —— 「搜索 Maven 仓库」会少半个字。
        //   Grid（Star + Auto）：不裁了，改成把 Star 那列**压扁**。窗口大时还好，
        //     主窗口缩到 720x560 时实测搜索框只剩 19px，跟没有一样。
        //   WrapPanel：装不下就整体换行，谁都不会被压扁，也不会被裁。
        //     代价是够宽时右边可能留几十像素空 —— 比输入框被压没划算。
        var findRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };

        _find.Margin = new Thickness(0, 0, 6, 0);
        // WrapPanel 里没有 Star 可拉，得自己给个下限：够宽时它是 140，
        // 不够宽就让按钮换到下一行，而不是把自己压成一条缝。
        _find.Width = double.NaN;
        _find.MinWidth = 140;
        _find.HorizontalAlignment = HorizontalAlignment.Stretch;
        _find.TextChanged += (_, _) => { if (!_showingRepo) RefreshCandidates(); };
        findRow.Children.Add(_find);

        var goRepo = Ui.BtnKey("lib.searchRepo");
        goRepo.Click += (_, _) => _ = SearchRepoAsync();
        findRow.Children.Add(goRepo);

        _backBtn.Visibility = Visibility.Collapsed;
        _backBtn.Click += (_, _) =>
        {
            _showingRepo = false; _repoHits.Clear(); _failText = "";
            RefreshCandidates();
        };
        findRow.Children.Add(_backBtn);
        head.Children.Add(findRow);

        // 手输坐标：表外的库唯一的入口。
        // 三个框上不带标签的话就是三个空盒子，用户不知道该往里填什么 ——
        // 放句浅色的提示文字当占位符，一眼知道是 groupId / artifactId / version。
        var custom = new StackPanel { Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 4) };

        // 同上理：WrapPanel。原来用 Grid 三个 Star + 一个 Auto，
        // 窗口一窄三个框就被压到 20 出头（实测 22px），根本没法填内容。
        // 换成 WrapPanel：够宽时一行四个，不够就换行，每个都保住可读宽度。
        var customRow = new WrapPanel();

        var hints = new[] { "lib.group", "lib.artifact", "lib.version" };
        var boxes = new[] { _cg, _ca, _cv };
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i].ToolTip = Lang.T(hints[i]);
            boxes[i].FontSize = 12;
            boxes[i].Margin = new Thickness(0, 0, 6, 0);
            boxes[i].Width = double.NaN;
            boxes[i].MinWidth = 78;                          // 再窄就换行，别压扁
            boxes[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Placeholder(boxes[i], hints[i]);
            customRow.Children.Add(boxes[i]);
        }
        var addCustom = MiniBtn(Lang.T("lib.add"));
        addCustom.Click += (_, _) => AddCustomCoord();
        customRow.Children.Add(addCustom);
        custom.Children.Add(customRow);
        head.Children.Add(custom);
        head.Children.Add(_candInfo);

        p.Children.Add(head);
        DockPanel.SetDock(head, Dock.Top);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroller.Content = _cand;
        p.Children.Add(scroller);
        return p;
    }

    private void AddCustomCoord()
    {
        // 走 RealText 而不是 .Text：占位提示也是 TextBox.Text，
        // 直接读会把 "groupId" 这种提示词当成用户填的内容。
        string coord = Core.MakeCoord(RealText(_cg), RealText(_ca), RealText(_cv));
        if (coord == null)
        {
            Core.Log("WARN", Lang.T("lib.coordBad"));
            _candInfo.Text = Lang.T("lib.coordBad");
            return;
        }
        AddDep(coord);
        foreach (var b in new[] { _cg, _ca, _cv })
        {
            if (b.Tag is string h) { b.Text = h; b.SetResourceReference(TextBox.ForegroundProperty, "Brush.Fg.Dim"); }
            else b.Clear();
        }
    }

    private void AddDep(string coord)
    {
        var p = Core.SplitCoord(coord);
        if (p == null) { Core.Log("WARN", Lang.T("lib.coordBad")); return; }

        var hit = _deps.FirstOrDefault(d => d.Key == p[0] + ":" + p[1]);
        if (hit != null)
        {
            // 同一个库再添加一次 = 换版本，别加出两条来
            hit.Version = p[2];
            hit.Ready = false;
        }
        else
        {
            _deps.Add(new Dep { Group = p[0], Artifact = p[1], Version = p[2] });
        }
        RefreshDeps();
        RefreshCandidates();
    }

    private void RefreshCandidates()
    {
        _cand.Children.Clear();
        _backBtn.Visibility = _showingRepo ? Visibility.Visible : Visibility.Collapsed;

        if (_showingRepo)
        {
            _candInfo.Text = _searching ? Lang.T("lib.searching")
                           : _failText.Length > 0 ? ""
                           : string.Format(Lang.T("lib.repoCount"), _repoHits.Count);
            if (_repoHits.Count == 0 && !_searching)
            {
                // 失败和「真没搜到」要分开说：前者要用户去查网络/代理，
                // 后者只要换个关键词。混成一句「没搜到」等于把线索藏起来。
                _cand.Children.Add(Empty(_failText.Length > 0 ? _failText
                                                              : Lang.T("lib.repoEmpty")));
                return;
            }
            foreach (var h in _repoHits)
                _cand.Children.Add(CandidateRow(h.Coord, h.Artifact, h.Coord));
            return;
        }

        string q = _find.Text.Trim().ToLowerInvariant();
        var list = _catalog.Where(l =>
            q.Length == 0 ||
            (l.Name ?? "").ToLowerInvariant().Contains(q) ||
            (l.Coord ?? "").ToLowerInvariant().Contains(q) ||
            (l.Desc ?? "").ToLowerInvariant().Contains(q)).ToList();

        _candInfo.Text = string.Format(Lang.T("lib.candCount"), list.Count, _catalog.Count);
        if (list.Count == 0)
        {
            _cand.Children.Add(Empty(Lang.T("lib.candEmpty")));
            return;
        }

        // 按分类分组：59 个库平铺成一列根本没法找
        string cur = null;
        foreach (var l in list)
        {
            if (l.Category != cur)
            {
                cur = l.Category;
                var h = new TextBlock { Text = cur, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(4, 10, 0, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
                _cand.Children.Add(h);
            }
            _cand.Children.Add(CandidateRow(l.Coord, l.Name, l.Coord, l.Desc));
        }
    }

    private Border CandidateRow(string coord, string name, string sub, string desc = null)
    {
        bool added = _deps.Any(d => d.Key == KeyOf(coord));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };

        // ⚠️ 这几行必须显式给 LineHeight（≈字号 ×1.35），不能靠默认值。
        // WPF 默认行高是字体自己的行距，叠在一起时上一行的降部会压进下一行
        // （放大看就是坐标的 j 尾巴扎进了路径的 C:\ 里）。
        var t1 = new TextBlock { Text = name, FontSize = 13,
            LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis };
        t1.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        texts.Children.Add(t1);
        var t2 = new TextBlock { Text = sub, FontSize = 11,
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis };
        t2.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        texts.Children.Add(t2);
        if (!string.IsNullOrEmpty(desc))
        {
            var t3 = new TextBlock { Text = desc, FontSize = 11, Opacity = 0.8,
                LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextTrimming = TextTrimming.CharacterEllipsis };
            t3.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
            texts.Children.Add(t3);
        }
        grid.Children.Add(texts);

        UIElement right;
        if (added)
        {
            var t = new TextBlock { Text = Lang.T("lib.added"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                Tag = "lib.added" };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ok");
            right = t;
        }
        else
        {
            var b = MiniBtn(Lang.T("lib.add"));
            b.Margin = new Thickness(0, 0, 8, 0);
            b.Click += (_, _) => AddDep(coord);
            right = b;
        }
        grid.Children.Add(right);
        Grid.SetColumn(right, 1);

        var box = new Border { Child = grid, CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 1, 0, 1) };
        box.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        return box;
    }

    private static string KeyOf(string coord)
    {
        var p = Core.SplitCoord(coord);
        return p == null ? "" : p[0] + ":" + p[1];
    }

    // ---- 右栏：当前依赖

    private DockPanel BuildSelected()
    {
        var p = new DockPanel();

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.LabelKey("lib.deps", 13));
        string srcKey = _hasPom ? "lib.src.pom" : "lib.src.txt";
        var src = new TextBlock { Text = Lang.T(srcKey), FontSize = 11, Tag = srcKey,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        src.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        head.Children.Add(src);
        head.Children.Add(_depInfo);

        p.Children.Add(head);
        DockPanel.SetDock(head, Dock.Top);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroller.Content = _depList;
        p.Children.Add(scroller);
        return p;
    }

    private void RefreshDeps()
    {
        RebuildDepList();
        _depInfo.Text = _deps.Count == 0
            ? ""
            : string.Format(Lang.T("lib.depCount"), _deps.Count);
        _ = RefreshReadyStateAsync();
    }

    private void RebuildDepList()
    {
        _depList.Children.Clear();
        if (_deps.Count == 0)
        {
            _depList.Children.Add(Empty(Lang.T("lib.depsEmpty")));
            return;
        }
        foreach (var d in _deps) _depList.Children.Add(DepRow(d));
    }

    private Border DepRow(Dep d)
    {
        // 两行：坐标单独占满一行，版本/按钮在下面一行。
        //
        // ⚠️ 别改回「坐标 | 版本下拉 | 按钮」的单行三栏：右侧动作区是 Auto、
        // 中间是 Star，右半栏宽度本来就只有 400 出头，130 的版本下拉加两个按钮
        // 一挤，Star 列会被压到几乎为零 —— 坐标全变成 "com.google.c..."。
        var grid = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new TextBlock { Text = d.Ready ? "●" : "○", FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        dot.SetResourceReference(TextBlock.ForegroundProperty, d.Ready ? "Brush.Ok" : "Brush.Warn");
        dot.ToolTip = Lang.T(d.Ready ? "lib.ready" : "lib.missing");
        top.Children.Add(dot);

        // ⚠️ 别给坐标挂 ToolTip。坐标在下面 state 那行已经原样显示一次了，
        // 再挂一个气泡，鼠标一停就弹出黑框盖住下面那行（这个 bug 报过两次）。
        // 内容没被截断就不该有 ToolTip —— 它挡住的东西比它补充的多。
        var coord = new TextBlock { Text = d.Coord, FontSize = 12.5,
            LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        coord.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        top.Children.Add(coord);
        grid.Children.Add(top);

        var state = new TextBlock { FontSize = 11, Margin = new Thickness(20, 2, 0, 0),
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Tag = d.Ready ? "lib.ready" : "lib.missing" };
        state.Text = Lang.T(d.Ready ? "lib.ready" : "lib.missing");
        state.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        grid.Children.Add(state);
        Grid.SetRow(state, 1);

        var acts = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };

        // 版本下拉：展开时才去仓库拉列表。一进来就把所有依赖的版本都拉一遍
        // 会变成几十个 HTTP 请求，开个对话框得等半天。
        var ver = new ComboBox { Width = 130, Height = 26, FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0) };
        ver.Items.Add(d.Version);
        ver.SelectedIndex = 0;
        bool loaded = false;
        ver.DropDownOpened += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            var r = await Core.OnWorker(() => Core.LibsRepoVersions(d.Group, d.Artifact));
            if (r == null || !r.Ok || r.Versions.Count == 0)
            {
                loaded = false;    // 这次没拿到，下次展开再试一次
                return;
            }
            ver.Items.Clear();
            foreach (var v in r.Versions.Take(60)) ver.Items.Add(v);
            ver.SelectedItem = d.Version;
        };
        ver.SelectionChanged += (_, _) =>
        {
            if (ver.SelectedItem is string s && !string.IsNullOrEmpty(s) && s != d.Version)
            {
                d.Version = s;
                d.Ready = false;
                RefreshDeps();
            }
        };
        acts.Children.Add(ver);

        if (!d.Ready)
        {
            var dl = MiniBtn(Lang.T("lib.download"));
            dl.IsEnabled = !d.Busy;
            dl.Click += (_, _) => _ = DownloadAsync(d, state);
            acts.Children.Add(dl);
        }

        var rm = MiniBtn(Lang.T("lib.remove"));
        rm.Margin = new Thickness(0);
        rm.Click += (_, _) => { _deps.Remove(d); RefreshDeps(); RefreshCandidates(); };
        acts.Children.Add(rm);

        grid.Children.Add(acts);
        Grid.SetRow(acts, 1);

        var box = new Border { Child = grid, CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 1, 0, 1) };
        box.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        return box;
    }

    /// <summary>问一次内核：这些依赖里哪些在本机已经有 jar 了。</summary>
    private async Task RefreshReadyStateAsync()
    {
        string ids = string.Join(";", _deps.Select(d => d.Coord));
        if (ids.Length == 0) return;

        // 这个只读本地磁盘，正常是毫秒级；但它是同步 P/Invoke，
        // 网络/磁盘卡住时一样能把这一页永远晾着，所以也给它一个上限。
        var task = Core.OnWorker(() => Core.LibsResolve(_root, ids));
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))) != task) return;
        var res = task.Result;
        if (res == null) return;

        var ready = new HashSet<string>(res.Ready.Where(x => !string.IsNullOrEmpty(x)),
                                        StringComparer.Ordinal);
        foreach (var d in _deps) d.Ready = ready.Contains(d.Coord);
        RebuildDepList();
    }

    private async Task DownloadAsync(Dep d, TextBlock state)
    {
        if (d.Busy) return;
        d.Busy = true;
        state.Text = Lang.T("lib.downloading");

        var r = await Core.LibsDownloadAsync(d.Coord, (done, total) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                state.Text = total > 0
                    ? string.Format(Lang.T("lib.progress"),
                                    (int)Math.Min(100, done * 100.0 / total))
                    : string.Format(Lang.T("lib.progressBytes"), Human(done));
            }));
        });

        d.Busy = false;
        if (r == null || !r.Ok)
        {
            Core.Log("ERROR", Lang.T("log.downloadFail") + (r?.Error ?? ""));
            state.Text = string.IsNullOrEmpty(r?.Error) ? Lang.T("lib.downloadFail") : r.Error;
            state.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Error");
            return;
        }

        d.Ready = true;
        Core.Log("OK", string.Format(Lang.T("log.downLoaded"), d.Artifact));
        RebuildDepList();
    }

    private async Task SearchRepoAsync()
    {
        string q = _find.Text.Trim();
        if (q.Length == 0 || _searching) return;

        _searching = true;
        _showingRepo = true;
        _repoHits.Clear();
        _failText = "";
        RefreshCandidates();

        // 这一趟可能要等很久：走的那个代理（HTTP(S)_PROXY）冷启要二三十秒，
        // 全都耗在 CONNECT 握手上。所以界面上必须写明在干什么 ——
        // 只挂一句「正在搜索…」，用户等十秒就会以为卡死了。
        var task = Core.OnWorker(() => Core.LibsRepoSearch(q));

        // 等不等得到都得给个交代。
        //
        // 内核那次同步的 WinHTTP 调用在某些机器上会长时间不返回（代理握手、
        // 网络半死、线程池被占住…），这时候如果只有一句 await，界面就永远
        // 停在「正在搜索…」——用户既不知道发生了什么，也没法重试。
        // 到点了就先把界面放开，慢的那次请求让它自己在后台跑完。
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(45)));
        if (done != task)
        {
            _searching = false;
            _failText = string.Format(Lang.T("lib.searchFail"), Lang.T("lib.searchTimeout"));
            RefreshCandidates();
            return;
        }
        var r = task.Result;
        _searching = false;

        if (r == null || !r.Ok)
        {
            Core.Log("WARN", Lang.T("log.searchFail") + (r?.Error ?? ""));
            _repoHits.Clear();
            _failText = string.Format(Lang.T("lib.searchFail"), r?.Error ?? "");
            RefreshCandidates();
            return;
        }
        _repoHits = r.Hits;
        RefreshCandidates();
    }

    // ---------------------------------------------------------------- 第 2 页：本地缓存

    private DockPanel BuildCachePage()
    {
        var p = new DockPanel();

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        row.Children.Add(Ui.LabelKey("lib.cache", 13));
        var open = MiniBtn(Lang.T("lib.cache.open"));
        open.Click += (_, _) =>
        {
            string dir = Path.Combine(Core.AppDataDir, "libs");
            try { Directory.CreateDirectory(dir); Process.Start("explorer.exe", dir); }
            catch (Exception ex) { Core.Log("ERROR", ex.Message); }
        };
        row.Children.Add(open);
        head.Children.Add(row);
        head.Children.Add(_cacheInfo);
        p.Children.Add(head);
        DockPanel.SetDock(head, Dock.Top);

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroller.Content = _cacheList;
        p.Children.Add(scroller);
        return p;
    }

    private async Task RefreshCacheAsync()
    {
        var items = await Core.OnWorker(() => Core.LibsCacheList());
        _cacheList.Children.Clear();
        if (items == null || items.Count == 0)
        {
            _cacheList.Children.Add(Empty(Lang.T("lib.cache.empty")));
            _cacheInfo.Text = "";
            return;
        }

        _cacheInfo.Text = string.Format(Lang.T("lib.cache.total"),
                                        items.Count, Human(items.Sum(i => i.Size)));
        foreach (var it in items) _cacheList.Children.Add(CacheRow(it));
    }

    private Border CacheRow(Core.CacheItem it)
    {
        var grid = new Grid { Margin = new Thickness(8, 5, 8, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t1 = new TextBlock { Text = it.Coord, FontSize = 12.5,
            LineHeight = 18, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis };
        t1.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        mid.Children.Add(t1);
        var t2 = new TextBlock { Text = it.Path, FontSize = 10.5, Opacity = 0.7,
            Margin = new Thickness(0, 2, 0, 0),
            LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis };
        t2.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        mid.Children.Add(t2);
        grid.Children.Add(mid);

        var size = new TextBlock { Text = Human(it.Size), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        size.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        grid.Children.Add(size);
        Grid.SetColumn(size, 1);

        var del = MiniBtn(Lang.T("lib.remove"));
        del.Margin = new Thickness(0);
        del.Click += async (_, _) =>
        {
            bool ok = await Core.OnWorker(() => Core.LibsCacheRemove(it.Coord));
            if (ok) Core.Log("OK", string.Format(Lang.T("lib.cache.deleted"), it.Coord));
            await RefreshCacheAsync();
        };
        grid.Children.Add(del);
        Grid.SetColumn(del, 2);

        var box = new Border { Child = grid, CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 1, 0, 1) };
        box.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        return box;
    }

    // ---------------------------------------------------------------- 保存

    private bool Apply()
    {
        try
        {
            var now = new HashSet<string>(_deps.Select(d => d.Key), StringComparer.Ordinal);

            // pom：先补新的 / 改版本的，再删掉这次被移除的
            if (_hasPom)
            {
                foreach (var d in _deps)
                    Core.LibsPomAdd(_root, d.Coord);
                foreach (var k in _pomKeysAtOpen)
                {
                    if (now.Contains(k)) continue;
                    var p = k.Split(':');
                    if (p.Length == 2) Core.LibsPomRemove(_root, p[0], p[1]);
                }
            }
            // libs.txt：没 pom 时它是唯一来源；有 pom 时它是
            // 「没装 mvn、构建退回 javac」那条路的 classpath 来源。
            Core.LibsSave(_root, string.Join(";", _deps.Select(d => d.Coord)));
            return true;
        }
        catch (Exception ex)
        {
            Core.Log("ERROR", ex.Message);
            return false;
        }
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>
    /// 给 TextBox 塞一句浅色占位提示（WPF 没有原生的 Placeholder）。
    ///
    /// 做法是拿 Tag 当「占位符现在显示着」的标记：没文字时把提示写进 Text、
    /// 换成灰的、Tag 记下来；用户一敲字就先把提示清掉、颜色换回正常。
    /// 读值的地方（AddCustomCoord）得先把这种「假文字」当空的处理，
    /// 不然用户没填 version 点添加，会拿 "version" 这三个字当版本号。
    /// </summary>
    private static void Placeholder(TextBox box, string key)
    {
        string hint = Lang.T(key);
        box.SetResourceReference(TextBox.ForegroundProperty, "Brush.Fg.Dim");
        box.Text = hint;
        box.Tag = hint;

        box.GotFocus += (_, _) =>
        {
            if (!ReferenceEquals(box.Tag, hint)) return;
            box.Text = "";
            box.Tag = null;
            box.SetResourceReference(TextBox.ForegroundProperty, "Brush.Fg.Primary");
        };
        box.LostFocus += (_, _) =>
        {
            if (box.Text.Trim().Length > 0) return;
            box.Text = hint;
            box.Tag = hint;
            box.SetResourceReference(TextBox.ForegroundProperty, "Brush.Fg.Dim");
        };
    }

    /// <summary>取输入框的真实内容：占位提示还在的时候算空。</summary>
    private static string RealText(TextBox box)
        => box.Tag is string h && box.Text == h ? "" : box.Text.Trim();

    private static Button MiniBtn(string text)
    {
        return new Button
        {
            Content = text, FontSize = 12, Height = 26, MinWidth = 0,
            Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Style = (Style)Application.Current.FindResource("DlgBtn")
        };
    }

    private static TextBlock Empty(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(6, 14, 6, 6),
            TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        return t;
    }

    private static string Human(long bytes)
        => bytes >= 1024 * 1024 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", bytes / 1048576.0)
         : bytes >= 1024        ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} KB", bytes / 1024.0)
                                : bytes + " B";
}
