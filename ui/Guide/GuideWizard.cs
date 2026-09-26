// GuideWizard.cs —— 阶段一：首次启动向导
//
// 一个多步小窗口：欢迎 → 选外观 → 选 JDK → 建项目 → 收尾。
// 只有最前面三步可以「跳过」直接跳出（等于整段引导都不再看），
// 从「建项目」那步起就没有跳过了 —— 见 Render() 里的说明。
//
// 三个刻意的选择：
//   1. 主题改成即时生效（点一下整界面就变），而不是最后才应用 ——
//      让人先看到效果再决定，比对着「浅色/深色」两个词想象强得多。
//   2. 项目是强制的：不给「不用了」，否则新手走完引导还是面对一个空
//      首页，不知道下一步该干嘛。这步必做，跳不出。
//   3. 项目本身放到最后一步、点了「开始使用」才真去建。中途退出的话
//      磁盘上不会留半个项目。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JavaStudio;

internal sealed class GuideWizard : Window
{
    private int _step;
    private bool _dark;
    private readonly int _darkFrom = Theme.IsDark ? 1 : 0;
    private Core.JdkInfo _jdk;
    private bool _makeSample = true;

    private readonly StackPanel _body = new() { Margin = new Thickness(28, 20, 28, 8) };
    private readonly StackPanel _nav = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(28, 0, 28, 20)
    };
    private readonly TextBlock _progress;

    /// <summary>true = 走完了（可能要建示例项目）；false = 跳过了。</summary>
    public bool Finished { get; private set; }

    /// <summary>走完之后要干的事（建示例项目）。由调用方执行，对话框自己不动磁盘。</summary>
    public bool WantSampleProject => Finished && _makeSample;

    /// <summary>
    /// 「选 JDK」那一步挑中的版本号（17 / 21 / 25 …），拿去给示例项目用。
    /// 建项目不能写死版本 —— 用户刚在这一步选了 25，示例项目就必须是 25，
    /// 否则 pom 里写着 17、编译却拿 25 的 javac，两头对不上。
    /// 一个 JDK 都没检测到时是 0，调用方自己兜底。
    /// </summary>
    public int ChosenJdkMajor => _jdk.Major;

    public GuideWizard()
    {
        Title = Lang.T("guide.title");
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.B("Brush.Bg.Base");

        _dark = Theme.IsDark;
        var jdks = SafeJdks();
        if (jdks.Count > 0) _jdk = jdks[0];

        // 自检开关：JS_GUIDE_STEP=n 直接停在某一页，截图看排版用。
        // （向导是模态窗口，主窗口那套 JS_SHOT 抓不到它。）
        string jump = Environment.GetEnvironmentVariable("JS_GUIDE_STEP");
        if (int.TryParse(jump, out int start) && start >= 0 && start <= LastStep)
            _step = start;

        // JS_GUIDE_JDK=n 预设挑中某个版本。跟用户真点那一行走同一条路
        // （_jdk 变了 + Core.SelectJdk 立刻生效），用来验证示例项目的 JDK 跟着选的走。
        string pickJdk = Environment.GetEnvironmentVariable("JS_GUIDE_JDK");
        if (int.TryParse(pickJdk, out int pick) && pick > 0)
        {
            var hit = jdks.FirstOrDefault(j => j.Major == pick);
            if (hit != null) { _jdk = hit; Core.SelectJdk(pick); }
        }

        // JS_GUIDE_AUTO=1 直接跳到最后一页并点「开始使用」。
        // 这是唯一能把「走完向导 → 建出示例项目」整条链路跑起来的办法 ——
        // 光靠截图点不到按钮，也就验不了项目里的 JDK 版本到底是多少。
        if (Environment.GetEnvironmentVariable("JS_GUIDE_AUTO") == "1")
        {
            _step = LastStep;
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                Finished = true;
                DialogResult = true;
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        _progress = new TextBlock { FontSize = 11, Margin = new Thickness(28, 14, 28, 0) };
        _progress.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");

        var root = new StackPanel();
        root.Children.Add(_progress);
        root.Children.Add(_body);
        root.Children.Add(_nav);

        Chrome.ApplyDialog(this, root);
        Lang.Tag(this, "guide.title");
        Render();
    }

    private static List<Core.JdkInfo> SafeJdks()
    {
        try { return Core.Jdks().Where(j => j.Ok).ToList(); }
        catch { return new List<Core.JdkInfo>(); }
    }

    // ---------------------------------------------------------------- 步骤内容

    private const int LastStep = 4;
    private const int ProjectStep = 3;   // 必做：建项目。这步没有「跳过」。

    private void Render()
    {
        _body.Children.Clear();
        _nav.Children.Clear();

        _progress.Text = string.Format(Lang.T("guide.stepOf"), _step + 1, LastStep + 1);

        switch (_step)
        {
            case 0: WelcomeStep(); break;
            case 1: ThemeStep(); break;
            case 2: JdkStep(); break;
            case 3: SampleStep(); break;
            default: ReadyStep(); break;
        }

        if (_step > 0)
        {
            var back = Ui.BtnKey("guide.prev");
            back.Click += (_, _) => { _step--; Render(); };
            _nav.Children.Add(back);
        }

        if (_step < LastStep)
        {
            var next = Ui.BtnKey("guide.next", true);
            next.Click += (_, _) => { _step++; Render(); };
            _nav.Children.Add(next);
        }
        else
        {
            // 用向导自己的键，别借巡礼的 guide.t.finish ——
            // 那会显示成「完成导览」，跟上一页写的「点开始使用」对不上。
            var go = Ui.BtnKey("guide.w.start", true);
            go.Click += (_, _) => { Finished = true; DialogResult = true; };
            _nav.Children.Add(go);
        }

        // 「跳过」只留给前几步（欢迎 / 外观 / JDK）—— 那几步是锦上添花，跳了不亏。
        // 从「建项目」开始就不给了：那步跳了整套引导等于白走，用户最后还是
        // 落在空荡荡的首页上；而收尾页要是还能跳，Finished 就成了 false，
        // 前面同意建的示例项目照样不会建 —— 等于「强制」被最后一步绕过去。
        // 也不至于把人困住：上一步能退回到有跳过的那些页。
        // （巡礼那层另有自己的「跳过」，它纯粹是讲解，跳掉没有任何副作用。）
        if (_step < ProjectStep)
        {
            var skip = Ui.BtnKey("guide.skip");
            skip.Click += (_, _) =>
            {
                Finished = false;
                if (_darkFrom == 0 && Theme.IsDark) Theme.Toggle();   // 跳过就把外观还原回去
                DialogResult = false;
            };
            _nav.Children.Add(skip);
        }
    }

    /// <summary>标题 + 说明。lead 给了就先摆它（欢迎页那张大图标走这条）。</summary>
    private void Head(string titleKey, string descKey, UIElement lead = null)
    {
        if (lead != null) _body.Children.Add(lead);
        _body.Children.Add(Ui.TitleKey(titleKey, 20));
        var d = new TextBlock
        {
            Text = Lang.T(descKey), FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 18), Tag = descKey
        };
        d.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        _body.Children.Add(d);
    }

    private void WelcomeStep()
    {
        var logo = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(20),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 18), BorderThickness = new Thickness(1)
        };
        logo.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        logo.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var mark = Icons.Visual("coffee", 38, Ui.B("Brush.Accent"));
        if (mark != null)
        {
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            logo.Child = mark;
        }
        Head("guide.w.welcome.t", "guide.w.welcome.d", logo);
    }

    // ---------------------------------------------------------------- 选外观

    private void ThemeStep()
    {
        Head("guide.w.theme.t", "guide.w.theme.d");

        var row = new StackPanel { Orientation = Orientation.Horizontal,
                                   HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(ThemeCard(false, "sun", "guide.w.theme.light"));
        row.Children.Add(ThemeCard(true, "moon", "guide.w.theme.dark"));
        _body.Children.Add(row);
    }

    private Border ThemeCard(bool dark, string icon, string key)
    {
        bool on = _dark == dark;
        var card = new Border
        {
            Width = 200, Height = 132, CornerRadius = new CornerRadius(14),
            Margin = new Thickness(10, 0, 10, 0), Padding = new Thickness(12),
            BorderThickness = new Thickness(on ? 2 : 1), Cursor = System.Windows.Input.Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Border");

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center,
                                     HorizontalAlignment = HorizontalAlignment.Center };
        var vis = Icons.Visual(icon, 30, on ? Ui.B("Brush.Accent") : Ui.B("Brush.Fg.Dim"));
        if (vis != null) { vis.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(vis); }
        var t = new TextBlock
        {
            Text = Lang.T(key), FontSize = 13, Tag = key,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0)
        };
        t.SetResourceReference(TextBlock.ForegroundProperty,
                               on ? "Brush.Fg.Primary" : "Brush.Fg.Muted");
        stack.Children.Add(t);
        card.Child = stack;

        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_dark == dark) return;
            _dark = dark;
            Theme.Apply(dark);                 // 先让人看到效果
            Core.SetSetting("theme", dark ? "dark" : "light");
            Config.Theme = dark ? "dark" : "light";
            Config.Save();
            Render();
        };
        return card;
    }

    // ---------------------------------------------------------------- 选 JDK

    private void JdkStep()
    {
        Head("guide.w.jdk.t", "guide.w.jdk.d");

        var jdks = SafeJdks();
        if (jdks.Count == 0)
        {
            var none = new TextBlock
            {
                Text = Lang.T("guide.w.jdk.none"), FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10), Tag = "guide.w.jdk.none"
            };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warn");
            _body.Children.Add(none);
            return;
        }

        foreach (var j in jdks)
        {
            bool on = _jdk.Home == j.Home;
            _body.Children.Add(JdkRow(j, on));
        }
    }

    private Border JdkRow(Core.JdkInfo j, bool on)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(on ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Border");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var vis = Icons.Visual("cpu", 18, on ? Ui.B("Brush.Accent") : Ui.B("Brush.Fg.Dim"));
        if (vis != null)
        {
            vis.VerticalAlignment = VerticalAlignment.Center;
            vis.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(vis);
        }

        var texts = new StackPanel();
        Grid.SetColumn(texts, 1);
        var t = new TextBlock { Text = j.Label, FontSize = 13 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        texts.Children.Add(t);
        // 「位置」这一行：随软件发布的那套显示「内置」——它的路径是安装目录，
        // 用户既改不了也不关心，甩出来只会让人以为自己哪里配错了。
        var h = new TextBlock { Text = j.LocationText, FontSize = 10.5 };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        texts.Children.Add(h);
        grid.Children.Add(texts);

        card.Child = grid;
        card.MouseLeftButtonUp += (_, _) =>
        {
            _jdk = j;
            // 真选出去：这一步的成果立刻生效，不是记下来等最后一起做
            if (j.Major > 0) Core.SelectJdk(j.Major);
            Render();
        };
        return card;
    }

    // ---------------------------------------------------------------- 示例项目

    /// <summary>
    /// 建项目这一步是强制的：不给「我自己来」那个选项。
    ///
    /// 空着进主界面等于把人丢在一片空白里 —— 项目树是空的、
    /// 底部输出是空的、巡礼讲到「左边是项目树」时指着一栏空气。
    /// 先有一个能跑的项目垫着，整套引导才讲得下去。
    /// </summary>
    private void SampleStep()
    {
        Head("guide.w.project.t", "guide.w.project.d");
        _makeSample = true;
        _body.Children.Add(SampleCard(true, "plus", "guide.w.project.yes"));
    }

    private Border SampleCard(bool yes, string icon, string key)
    {
        bool on = _makeSample == yes;
        var card = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(on ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");

        var row = new StackPanel { Orientation = Orientation.Horizontal,
                                   VerticalAlignment = VerticalAlignment.Center };
        var vis = Icons.Visual(icon, 18, Ui.B("Brush.Accent"));
        if (vis != null) row.Children.Add(vis);
        var t = new TextBlock { Text = Lang.T(key), FontSize = 13, Tag = key,
                                Margin = new Thickness(10, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        row.Children.Add(t);
        card.Child = row;
        return card;   // 只有一张、点了也不切换：这步没有「不选」这条路
    }

    private void ReadyStep()
    {
        Head("guide.w.ready.t", "guide.w.ready.d");

        var ok = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
        };
        ok.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        var vis = Icons.Visual("check-circle", 30, Ui.B("Brush.Ok"));
        if (vis != null)
        {
            vis.HorizontalAlignment = HorizontalAlignment.Center;
            vis.VerticalAlignment = VerticalAlignment.Center;
            ok.Child = vis;
        }
        _body.Children.Add(ok);
    }
}
