// GuideChecklist.cs —— 阶段三：首页那块「快速上手」卡片
//
// 位置：首页三张入口卡片下面。列几件新手一定会遇到的事，
// 做完一项前面就打勾。全部做完自动收成一行，不占地方。
//
// 状态不自己记 —— 全部问 GuideState.Steps()，那儿每一步都按真实情况现算
// （JDK 是真是 Availability 过、项目是真存在）。这样清单永远不会跟实际情况打架。

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JavaStudio;

internal static class GuideChecklist
{
    /// <summary>清单被收起来了没。收起状态只在本次会话内有效，不落配置。</summary>
    private static bool _collapsed;

    /// <summary>
    /// 清单是否展开。外部想让它一定露出来（比如刚点完「重新观看引导」、
    /// 这时候清单多半已经被收起了）就置 true。
    /// </summary>
    public static bool Expanded
    {
        get => !_collapsed;
        set => _collapsed = !value;
    }

    public static UIElement Build(Action replay)
    {
        var steps = GuideState.Steps();
        int done = 0;
        foreach (var s in steps) if (s.Done) done++;

        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 18), BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true
        };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        // ---- 抬头：标题 + 「n/m 已完成」+ 收起/展开
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

        var titleBox = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icons.Visual("check-circle", 15, Ui.B("Brush.Accent"));
        if (icon != null)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 7, 0);
            titleBox.Children.Add(icon);
        }
        var t = new TextBlock
        {
            Text = Lang.T("guide.check.t"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Tag = "guide.check.t"
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        titleBox.Children.Add(t);
        head.Children.Add(titleBox);

        var toggleKey = _collapsed ? "guide.check.show" : "guide.check.hide";
        var toggle = new Button
        {
            Content = Lang.T(toggleKey), FontSize = 11, Tag = toggleKey,
            Padding = new Thickness(9, 3, 9, 3), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            // DockPanel 里被 Dock 到一边的子元素默认会填满整条，
            // 不设 Right 的话按钮就摊成一条白杠，看着像个输入框。
            HorizontalAlignment = HorizontalAlignment.Right
        };
        toggle.SetResourceReference(Button.ForegroundProperty, "Brush.Fg.Muted");
        toggle.SetResourceReference(Button.BackgroundProperty, "Brush.Bg.Raised");
        toggle.SetResourceReference(Button.BorderBrushProperty, "Brush.Border");
        DockPanel.SetDock(toggle, Dock.Right);
        toggle.Click += (_, _) => { _collapsed = !_collapsed; RefreshNeeded?.Invoke(); };
        head.Children.Add(toggle);

        card.Child = new StackPanel();
        var body = (StackPanel)card.Child;
        body.Children.Add(head);

        var subKey = "guide.check.sub";
        var sub = new TextBlock
        {
            Text = string.Format(Lang.T(subKey), done, steps.Count), FontSize = 11,
            Margin = new Thickness(0, 0, 0, _collapsed ? 0 : 10), Tag = "fmt:" + subKey + "|" + done + "|" + steps.Count
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        body.Children.Add(sub);

        if (GuideState.AllDone())
        {
            var ok = new TextBlock
            {
                Text = Lang.T("guide.check.allDone"), FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0), Tag = "guide.check.allDone"
            };
            ok.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ok");
            body.Children.Add(ok);
        }
        else if (!_collapsed)
        {
            foreach (var s in steps) body.Children.Add(Row(s));
        }

        if (!_collapsed)
        {
            var replayBtn = new Button
            {
                Content = Lang.T("guide.check.replay"), FontSize = 11, Tag = "guide.check.replay",
                Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            replayBtn.SetResourceReference(Button.ForegroundProperty, "Brush.Fg.Muted");
            replayBtn.SetResourceReference(Button.BackgroundProperty, "Brush.Bg.Raised");
            replayBtn.SetResourceReference(Button.BorderBrushProperty, "Brush.Border");
            replayBtn.Click += (_, _) => replay?.Invoke();
            body.Children.Add(replayBtn);
        }

        return card;
    }

    /// <summary>点「收起/展开」之后要重建首页，由 MainWindow 赋一个委托进来。</summary>
    public static Action RefreshNeeded;

    private static UIElement Row(GuideState.Step s)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 7),
            VerticalAlignment = VerticalAlignment.Center
        };

        // ⚠️ 声明成 FrameworkElement 而不是 UIElement：下面要设 VerticalAlignment / Margin，
        // 这两个属性属于 FrameworkElement，UIElement 上没有。
        FrameworkElement mark;
        if (s.Done)
        {
            mark = Icons.Visual("check", 14, Ui.B("Brush.Ok"))
                   ?? new TextBlock { Text = "✓", FontSize = 12, Width = 16 };
            if (mark is TextBlock tick)
                tick.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ok");
        }
        else
        {
            mark = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(3, 0, 5, 0), StrokeThickness = 1
            };
            ((System.Windows.Shapes.Ellipse)mark).SetResourceReference(
                System.Windows.Shapes.Shape.StrokeProperty, "Brush.Border");
            ((System.Windows.Shapes.Ellipse)mark).SetResourceReference(
                System.Windows.Shapes.Shape.FillProperty, "Brush.Fg.Dim");
        }
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 9, 0);
        row.Children.Add(mark);

        var tx = new TextBlock
        {
            Text = Lang.T(s.TitleKey), FontSize = 12, Tag = s.TitleKey,
            VerticalAlignment = VerticalAlignment.Center
        };
        tx.SetResourceReference(TextBlock.ForegroundProperty,
                                s.Done ? "Brush.Fg.Dim" : "Brush.Fg.Primary");
        row.Children.Add(tx);
        return row;
    }
}
