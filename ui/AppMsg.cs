#nullable enable

// AppMsg.cs —— 软件自己的消息框
//
// 全项目不许再直接用 MessageBox.Show / System.Windows.Forms.* / Microsoft.Win32.*。
// 那些是**系统**弹窗，三个毛病都绕不过去：
//   1. 不跟主题走 —— 深色模式下弹一个白框，很刺眼；
//   2. 字体、圆角、按钮样式跟界面是两套；
//   3. 文案写死在代码里，切英文时它还是中文。
// 所有用户可见的提示、确认、报错统一走这里（选文件/文件夹走 Pickers）。

using System.Windows;
using System.Windows.Controls;

namespace JavaStudio;

internal static class AppMsg
{
    /// <summary>只带一个「确定」的提示框。</summary>
    public static void Show(Window? owner, string title, string message)
    {
        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 18), MaxWidth = 460 };
        root.Children.Add(Ui.Label(title, 14));
        root.Children.Add(Body(message));

        Window? win = null;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var ok = Ui.Btn(Lang.T("msg.ok"), true);
        ok.Click += (_, _) => { if (win != null) win.DialogResult = true; };
        row.Children.Add(ok);
        root.Children.Add(row);

        win = Make(owner, title, root);
        win.ShowDialog();
    }

    /// <summary>「确定 / 取消」二选一。返回 true 表示点了确定。</summary>
    public static bool Ask(Window? owner, string title, string message,
                           string? yesText = null, string? noText = null)
    {
        var root = new StackPanel { Margin = new Thickness(24, 18, 24, 18), MaxWidth = 460 };
        root.Children.Add(Ui.Label(title, 14));
        root.Children.Add(Body(message));

        Window? win = null;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var no = Ui.Btn(noText ?? Lang.T("msg.cancel"));
        no.Click += (_, _) => { if (win != null) win.DialogResult = false; };
        var yes = Ui.Btn(yesText ?? Lang.T("msg.ok"), true);
        yes.Click += (_, _) => { if (win != null) win.DialogResult = true; };
        row.Children.Add(no); row.Children.Add(yes);
        root.Children.Add(row);

        win = Make(owner, title, root);
        return win.ShowDialog() == true;
    }

    private static TextBlock Body(string message)
    {
        var t = new TextBlock
        {
            Text = message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        return t;
    }

    private static Window Make(Window? owner, string title, UIElement body)
    {
        var w = new Window
        {
            Owner = owner,
            Title = title,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = owner == null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Brush.Bg.Base");
        Chrome.ApplyDialog(w, body);
        return w;
    }
}
