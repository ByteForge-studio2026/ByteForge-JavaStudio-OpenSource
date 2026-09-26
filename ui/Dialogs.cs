// Dialogs.cs —— 新建项目 / 新建文件 / 设置 / 图标库 / 关于
//
// 第三方库那个框不在这儿了，重写成了 LibsDialog.cs（项目依赖 + 本地缓存两页）
// —— 它比这些框大得多，塞在这个文件里会把它撑得没法看。
//
// 这些框都不大，用代码直接搭比再配一套 XAML 省事，也少一次
// 「XAML 里写的名字和后台对不上」的机会。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JavaStudio;

internal static class Ui
{
    public static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Label(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        return t;
    }

    /// <summary>打了语言戳的标签：切语言时 Lang.Translate 会把它换成 key 对应的文案。</summary>
    public static TextBlock LabelKey(string key, double size = 12)
    {
        var t = Label(Lang.T(key), size);
        t.Tag = key;
        return t;
    }

    /// <summary>打了语言戳的标题（字号大一点那种）。</summary>
    public static TextBlock TitleKey(string key, double size)
    {
        var t = new TextBlock { Text = Lang.T(key), FontSize = size,
            Foreground = B("Brush.Fg.Primary"), Tag = key };
        return t;
    }

    /// <summary>打了语言戳的按钮。</summary>
    public static Button BtnKey(string key, bool primary = false)
    {
        var b = Btn(Lang.T(key), primary);
        b.Tag = key;
        return b;
    }

    /// <summary>打了语言戳的带图标按钮。</summary>
    public static Button BtnIconKey(string key, string icon, bool primary = false)
    {
        var b = BtnIcon(Lang.T(key), icon, primary);
        b.Tag = key;
        return b;
    }

    /// <summary>打了语言戳的复选框。</summary>
    public static CheckBox CheckKey(string key, bool value)
    {
        var c = Check(Lang.T(key), value);
        c.Tag = key;
        return c;
    }

    public static TextBox Input(string text = "", int w = 260)
    {
        // ⚠️ HorizontalAlignment 必须是 Left，不能靠默认值。
        // WPF 的默认是 Stretch，而 Stretch 配上显式 Width 之后会**退化成居中** ——
        // 面板比输入框宽的时候，框就飘到中间去了，跟上面的标签各占一边。
        // 弹窗还是个独立窗口时面板宽度≈输入框宽度，看不出毛病；
        // 改成主窗口里的卡片之后面板一下子宽出来，偏移就藏不住了。
        var t = new TextBox { Text = text, Width = w, Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13, Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left };
        t.SetResourceReference(TextBox.BackgroundProperty, "Brush.Bg.Raised");
        t.SetResourceReference(TextBox.ForegroundProperty, "Brush.Fg.Primary");
        t.SetResourceReference(TextBox.BorderBrushProperty, "Brush.Border");
        return t;
    }

    /// <summary>
    /// 圆角按钮。颜色全走 DlgBtn / DlgBtnPrimary 样式里的 DynamicResource，
    /// 切主题自动跟着变——以前手写静态画刷，改了主题按钮还是旧色。
    /// </summary>
    public static Button Btn(string text, bool primary = false)
    {
        var b = new Button
        {
            Content = text, FontSize = 13, MinWidth = 88, Height = 32,
            Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Style = (Style)Application.Current.FindResource(primary ? "DlgBtnPrimary" : "DlgBtn")
        };
        return b;
    }

    /// <summary>
    /// 实心红按钮：危险操作的「同意」用它。
    ///
    /// 和 primary 的区别是语义不是轻重 —— Agent 授权卡片放行的是一条高危命令，
    /// 红色是在说「这一步有代价」，蓝色主按钮会让人以为是普通「继续」。
    /// </summary>
    public static Button BtnDanger(string text)
    {
        var b = Btn(text);
        b.Style = (Style)Application.Current.FindResource("DlgBtnDanger");
        return b;
    }

    public static CheckBox Check(string text, bool value)
    {
        var c = new CheckBox { Content = text, IsChecked = value, FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6) };
        c.SetResourceReference(CheckBox.ForegroundProperty, "Brush.Fg.Muted");
        return c;
    }

    public static StackPanel Row(params UIElement[] kids)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var k in kids) p.Children.Add(k);
        return p;
    }

    /// <summary>
    /// 带图标的按钮。图标一律来自图标库（Icons.Visual），
    /// 不自己画，也不拿 emoji 凑——那套 SVG 有 177 个，够用了。
    /// </summary>
    public static Button BtnIcon(string text, string icon, bool primary = false)
    {
        var b = Btn("", primary);
        if (Icons.Has(icon))
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            var vis = primary
                ? Icons.Visual(icon, 15, Brushes.White)
                : Icons.Visual(icon, 15, "Brush.Fg.Muted");
            if (vis != null) stack.Children.Add(vis);
            if (!string.IsNullOrEmpty(text))
                stack.Children.Add(new TextBlock
                { Text = text, FontSize = 13, Margin = new Thickness(7, 0, 0, 0) });
            b.Content = stack;
        }
        else b.Content = text;   // 库里没有这个名字就退化成纯文字，别崩
        return b;
    }
}

// ================================================================ 新建项目
//
// 字段照 IntelliJ IDEA 的新建项目向导：
//   名称 / 位置 / 语言 / 构建系统 / JDK
//   高级设置：GroupId · ArtifactId · Version
//   添加示例代码（关掉就只生成目录骨架，一个 .java 都不写）
//   创建 Git 仓库

// ================================================================ 新建项目
//
// 「项目名」和「包名（GroupId）」两个输入框带实时校验：
//   空   → 无图标 + 灰字「请输入字符」，创建置灰
//   合法 → 绿勾 + 绿字「可用」
//   非法 → 红叉 + 红字原因，创建置灰
// 图标全部复用图标库里的现成 SVG，不新画、不用文字符号凑：
//   - 勾 = assets/icons/check-circle.svg
//   - 叉 = assets/icons/x.svg

internal sealed class NewProjectDialog : DlgPanel
{
    public string CreatedDir { get; private set; }

    private readonly TextBox _name = Ui.Input("untitled");
    private readonly TextBox _dir;
    private readonly ComboBox _build;
    private readonly ComboBox _jdk;
    private readonly List<Core.JdkInfo> _jdks;
    private readonly TextBox _group = Ui.Input("com.example");
    // 这两个要塞进 250 宽的那一列里并排，默认宽 260 会顶出去 —— 收到 240
    private readonly TextBox _artifact = Ui.Input("", 240);
    private readonly TextBox _version = Ui.Input("1.0.0", 240);
    private readonly CheckBox _sample;
    private readonly CheckBox _git;
    private readonly TextBlock _err = new() { FontSize = 12, Foreground = Ui.B("Brush.Error"),
                                              TextWrapping = TextWrapping.Wrap };

    // 校验提示：左边小图标 + 右边文字
    private readonly ContentControl _nameIcon = new();
    private readonly TextBlock _nameHint = new() { FontSize = 11 };
    private readonly ContentControl _groupIcon = new();
    private readonly TextBlock _groupHint = new() { FontSize = 11 };
    private readonly Button _ok;

    public NewProjectDialog()
    {
        // 内嵌成主窗口的一页，所以不设 Width/SizeToContent/WindowStartupLocation：
        // 宽度由主窗口工作区决定，高度由内容自己撑（外面那层 ScrollViewer 会滚）。
        Title = Lang.T("dlg.newProject");
        FontFamily = new FontFamily("Microsoft YaHei UI");

        _dir = Ui.Input(Core.DefaultProjectsDir(), 380);
        // ⚠️ 这几个固定宽度的下拉框都要显式 Left。WPF 默认 Stretch，Stretch 配上
        // 显式 Width 之后会**退化成居中** —— 面板比控件宽的时候，控件就飘到中间，
        // 跟左边的标签对不上（见 Ui.Input 里那段同样的说明）。
        _build = new ComboBox { Width = 150, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 8),
                                HorizontalAlignment = HorizontalAlignment.Left };
        _build.Items.Add(Lang.T("dlg.bMaven"));
        _build.Items.Add(Lang.T("dlg.bGradle"));
        _build.Items.Add(Lang.T("dlg.bNone"));

        _jdk = new ComboBox { Width = 190, Margin = new Thickness(0, 0, 0, 8),
                              HorizontalAlignment = HorizontalAlignment.Left };
        _jdks = Core.Jdks().Where(x => x.Ok).ToList();
        foreach (var j in _jdks) _jdk.Items.Add(j.Label);
        // 预选设置里记的首选 JDK（主版本号匹配）；没设过就选列表最后一个
        int pref = string.IsNullOrEmpty(Config.PreferredJdk)
            ? -1 : _jdks.FindIndex(j => j.Major.ToString() == Config.PreferredJdk);
        _jdk.SelectedIndex = pref >= 0 ? pref : (_jdks.Count - 1);

        _sample = Ui.CheckKey("dlg.sample", true);
        _git = Ui.CheckKey("dlg.git", false);

        // ⚠️ 内嵌页必须自己限宽 + 靠左。不设 MaxWidth 的话，宽屏上一行能拉到两千像素，
        // 输入框跟着一起变长，整个表单看起来是散的。
        var form = new StackPanel { Margin = new Thickness(24, 12, 24, 4),
                                    MaxWidth = 620 };

        form.Children.Add(PageTitle("dlg.newProject", 18));

        // ---- 名称 + 实时校验
        form.Children.Add(Ui.LabelKey("dlg.name"));
        form.Children.Add(_name);
        form.Children.Add(HintRow(_nameIcon, _nameHint));

        form.Children.Add(Ui.LabelKey("dlg.location"));
        var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        dirRow.Children.Add(_dir);
        var browse = Ui.BtnKey("dlg.browse");
        browse.Click += (_, _) =>
        {
            string picked = Pickers.PickFolder(this, Lang.T("m.openProject"), _dir.Text);
            if (!string.IsNullOrEmpty(picked)) _dir.Text = picked;
        };
        dirRow.Children.Add(browse);
        form.Children.Add(dirRow);

        var two = new StackPanel { Orientation = Orientation.Horizontal };
        var left = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(Ui.LabelKey("dlg.build")); left.Children.Add(_build);
        var right = new StackPanel { Width = 250 };
        right.Children.Add(Ui.LabelKey("dlg.jdk")); right.Children.Add(_jdk);
        two.Children.Add(left); two.Children.Add(right);
        form.Children.Add(two);

        form.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 5),
            Background = Ui.B("Brush.Border") });
        form.Children.Add(Ui.LabelKey("dlg.advanced", 13));

        // ---- GroupId + 实时校验
        form.Children.Add(Ui.LabelKey("dlg.group"));
        form.Children.Add(_group);
        form.Children.Add(HintRow(_groupIcon, _groupHint));

        // ArtifactId / Version 并排。这两项都不长，各占一行的话光「标签 + 输入框」
        // 就多吃 55px，表单总高一顶出可视区，最底下的「创建」就被推到要下滑才看得到。
        var av = new StackPanel { Orientation = Orientation.Horizontal };
        var aCol = new StackPanel { Width = 250, Margin = new Thickness(0, 0, 20, 0) };
        aCol.Children.Add(Ui.LabelKey("dlg.artifact")); aCol.Children.Add(_artifact);
        var vCol = new StackPanel { Width = 250 };
        vCol.Children.Add(Ui.LabelKey("dlg.version")); vCol.Children.Add(_version);
        av.Children.Add(aCol); av.Children.Add(vCol);
        form.Children.Add(av);

        form.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 5),
            Background = Ui.B("Brush.Border") });
        // 两个勾选项也并排：竖着放又是一整行（22px），横着放 396px 宽，够。
        var opts = new StackPanel { Orientation = Orientation.Horizontal };
        opts.Children.Add(_sample);
        opts.Children.Add(new Border { Width = 24 });
        opts.Children.Add(_git);
        form.Children.Add(opts);
        form.Children.Add(_err);

        // ---- 底部按钮：钉在卡片底边，**不跟着表单滚**
        //
        // 原来按钮就是表单 StackPanel 的最后一行。表单 649 高、可视区只有 505，
        // 最下面 144px 全在可视区外 —— 用户看到的就是「创建按钮要点下滑才找得到」。
        // 拆成 Grid 两行：上面 `*` 行放可滚的表单，下面 Auto 行放按钮，
        // 按钮就永远在；表单再长也只是里面那层 ScrollViewer 滚，按钮不动。
        var btns = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.BtnKey("msg.cancel");
        cancel.Click += (_, _) => CloseWith(false);
        _ok = Ui.BtnKey("dlg.create", true);
        _ok.Click += OnCreate;
        btns.Children.Add(cancel); btns.Children.Add(_ok);

        // 底边加一条分隔线：表单滚起来的时候，按钮那块得看得出是「钉住的」，
        // 不然内容从它后面穿过去，像浮在半空。
        var foot = new Border { Child = btns, Padding = new Thickness(24, 10, 24, 14) };
        foot.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        foot.BorderThickness = new Thickness(0, 1, 0, 0);

        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            // 横向不许滚：限宽之后内容本来就不会超宽，多一条横向滚动条只会碍事
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var page = new Grid();
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(scroll);
        Grid.SetRow(foot, 1);
        page.Children.Add(foot);

        Lang.Tag(this, "dlg.newProject");   // 必须在 Install 之前：标题要读这个戳
        WantWidth = 620;                      // 新建项目
        // 自己管滚动：卡片拿到确定高度（= 可用高度），里面那层才滚得起来，
        // 外面那层 DlgHost 的 ScrollViewer 就不套了（套了就又是「整张卡片跟着晃」）。
        OwnScroll = true;
        Install(page);

        // 实时校验：两个框各管各的，一个变合法不影响另一个的状态
        _name.TextChanged += (_, _) => Validate();
        _group.TextChanged += (_, _) => Validate();
        Validate();
    }

    /// <summary>进来就把光标放在项目名里（DlgHost 负责调）。</summary>
    internal override IInputElement InitialFocus => _name;

    // ---------------------------------------------------------------- 校验

    /// <summary>项目名：只允许英文字母和数字。</summary>
    private static bool ValidName(string s)
        => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c));

    /// <summary>
    /// 包名：整体以字母开头；按点分段后每段都要以字母开头（顺带就排除了
    /// 「段以数字/下划线开头」和空段）；段内只允许字母、数字、下划线。
    /// </summary>
    private static bool ValidGroup(string s)
    {
        if (s.Length == 0 || !char.IsLetter(s[0])) return false;
        if (s.EndsWith(".")) return false;
        foreach (var seg in s.Split('.'))
        {
            if (seg.Length == 0 || !char.IsLetter(seg[0])) return false;
            if (!seg.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    private void Validate()
    {
        SetState(_name, _nameIcon, _nameHint, ValidName, Lang.T("dlg.nameBad"));
        SetState(_group, _groupIcon, _groupHint, ValidGroup, Lang.T("dlg.groupBad"));
        _ok.IsEnabled = ValidName(_name.Text.Trim()) && ValidGroup(_group.Text.Trim());
    }

    /// <summary>
    /// 三态刷新。关键点：每次都全量重设图标和文字颜色，红变绿就是瞬间的事，
    /// 不会出现红字赖着不走。
    /// </summary>
    private void SetState(TextBox box, ContentControl icon, TextBlock hint,
                          Func<string, bool> ok, string badMsg)
    {
        string t = box.Text.Trim();
        if (t.Length == 0)
        {
            icon.Content = null;                     // 空输入不放图标
            SetHint(hint, Lang.T("dlg.enterChars"), "Brush.Fg.Dim");
        }
        else if (ok(t))
        {
            icon.Content = Icons.Visual("check-circle", 14, Ui.B("Brush.Ok"));
            SetHint(hint, Lang.T("dlg.available"), "Brush.Ok");
        }
        else
        {
            icon.Content = Icons.Visual("x", 14, Ui.B("Brush.Error"));
            SetHint(hint, badMsg, "Brush.Error");
        }
    }

    private static void SetHint(TextBlock hint, string text, string brushKey)
    {
        hint.Text = text;
        hint.Foreground = Ui.B(brushKey);
    }

    /// <summary>图标 + 提示文字的一行。</summary>
    private static StackPanel HintRow(ContentControl icon, TextBlock hint)
    {
        icon.Width = 16; icon.VerticalContentAlignment = VerticalAlignment.Center;
        var p = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // 底部只留 4：这两行提示各占 25 高，表单总高差 31px 就顶出可视区，
            // 省下来这 6×2 正好是缺口的一部分
            Margin = new Thickness(2, 0, 0, 4)
        };
        p.Children.Add(icon);
        var wrap = new Border { Width = 4 };
        p.Children.Add(wrap);
        p.Children.Add(hint);
        return p;
    }

    /// <summary>自检用：直接填名称和包名（触发实时校验）。</summary>
    public void Prefill(string name, string group)
    {
        _name.Text = name;
        _group.Text = group;
        Validate();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        string name = _name.Text.Trim();
        string group = _group.Text.Trim();
        string artifact = string.IsNullOrWhiteSpace(_artifact.Text) ? name : _artifact.Text.Trim();

        var err = Core.ValidateProject(name, group, artifact);
        if (!string.IsNullOrEmpty(err)) { _err.Text = err; return; }
        if (string.IsNullOrWhiteSpace(_dir.Text)) { _err.Text = Lang.T("dlg.pickLocation"); return; }

        string bs = _build.SelectedIndex switch { 0 => "maven", 1 => "gradle", _ => "none" };
        string jdkRelease = "17";
        if (_jdk.SelectedIndex >= 0 && _jdk.SelectedIndex < _jdks.Count)
        {
            jdkRelease = _jdks[_jdk.SelectedIndex].Major.ToString();
            Config.PreferredJdk = jdkRelease;
            Config.Save();
        }

        var r = Core.CreateProject(_dir.Text.Trim(), name, group, artifact,
                                   _version.Text.Trim(), bs, jdkRelease,
                                   _sample.IsChecked == true, _git.IsChecked == true);
        if (!r.Ok) { _err.Text = r.Error; return; }

        CreatedDir = r.Dir;
        Core.Log("OK", string.Format(Lang.T("log.projectCreated"), name, bs, r.Created.Count)
                       + (_sample.IsChecked == true ? "" : Lang.T("log.noSample")));
        CloseWith(true);
    }
}

// ================================================================ 新建文件

internal sealed class NewFileDialog : DlgPanel
{
    public string CreatedPath { get; private set; }
    private readonly TextBox _name = Ui.Input("NewClass");
    private readonly TextBlock _err = new() { FontSize = 12, Foreground = Ui.B("Brush.Error") };
    private readonly string _root;

    public NewFileDialog(string root)
    {
        _root = root;
        Title = Lang.T("dlg.newJavaFile");

        var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 18),
                                     MaxWidth = 520 };
        panel.Children.Add(PageTitle("dlg.newJavaFile", 16));
        panel.Children.Add(Ui.LabelKey("dlg.className"));
        panel.Children.Add(_name);
        panel.Children.Add(_err);

        var btns = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.BtnKey("msg.cancel"); cancel.Click += (_, _) => CloseWith(false);
        var ok = Ui.BtnKey("dlg.create", true); ok.Click += OnCreate;
        btns.Children.Add(cancel); btns.Children.Add(ok);
        panel.Children.Add(btns);
        Lang.Tag(this, "dlg.newJavaFile");
        WantWidth = 520;                      // 新建 Java 文件
        Install(panel);
    }

    internal override IInputElement InitialFocus => _name;

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        string cls = _name.Text.Trim();
        if (string.IsNullOrEmpty(cls)) { _err.Text = Lang.T("dlg.writeName"); return; }
        if (cls.EndsWith(".java")) cls = cls[..^5];
        if (!char.IsLetter(cls[0])) { _err.Text = Lang.T("dlg.nameLetter"); return; }

        string pkgDir = Path.Combine(_root, "src", "main", "java");
        if (!Directory.Exists(pkgDir)) pkgDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(pkgDir);
        string path = Path.Combine(pkgDir, cls + ".java");
        if (File.Exists(path)) { _err.Text = Lang.T("dlg.exists"); return; }

        File.WriteAllText(path, $"public class {cls} {{\n    \n}}\n");
        Core.Log("OK", string.Format(Lang.T("log.fileCreated"), cls));
        CreatedPath = path;
        CloseWith(true);
    }
}

// ================================================================ 设置

// ================================================================ 最近文件
//
// Ctrl+E，IDEA 里叫 Recent Files：列出最近打开过的文件（关掉的也算），
// 上面一个过滤框，边打边筛，回车打开。

internal sealed class RecentFilesDialog : DlgPanel
{
    public string Picked { get; private set; } = "";

    private readonly List<string> _all;
    private readonly ListBox _list = new()
    {
        Height = 320,
        Background = Ui.B("Brush.Bg.Raised"),
        BorderBrush = Ui.B("Brush.Border"),
        Foreground = Ui.B("Brush.Fg.Primary")
    };
    private readonly TextBox _find = Ui.Input("", 420);

    public RecentFilesDialog(List<string> files)
    {
        _all = files.Where(f => !string.IsNullOrEmpty(f)).Distinct().ToList();
        Title = Lang.T("dlg.recent");

        var panel = new StackPanel { Margin = new Thickness(22, 16, 22, 16),
                                     MaxWidth = 620 };
        panel.Children.Add(PageTitle("dlg.recent", 17));

        _find.TextChanged += (_, _) => Fill();
        panel.Children.Add(_find);
        panel.Children.Add(_list);
        _list.MouseDoubleClick += (_, _) => Pick();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Pick(); e.Handled = true; }
        };

        Lang.Tag(this, "dlg.recent");
        WantWidth = 680; WantHeight = 470;    // 最近文件：列表有高度才滚得起来
        Install(panel);
        Fill();
    }

    internal override IInputElement InitialFocus => _find;

    private void Fill()
    {
        string q = _find.Text.Trim();
        var items = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(f => f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        _list.ItemsSource = items.Select(f => Path.GetFileName(f) + "   " + f).ToList();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Pick()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        string q = _find.Text.Trim();
        var items = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(f => f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (i < items.Count) { Picked = items[i]; CloseWith(true); }
    }
}

// ================================================================ 一行输入
//
// 重命名这种只要一个字符串的场合，专门写个框比每次 MessageBox 凑合强。

internal sealed class TextDialog : DlgPanel
{
    public string Value { get; private set; } = "";
    private readonly TextBox _input;

    internal override IInputElement InitialFocus => _input;

    public TextDialog(string title, string initial = "")
    {
        Title = title;

        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 20) };
        panel.Children.Add(Ui.Label(title));
        _input = Ui.Input(initial, 340);
        // 内嵌页是跟着主窗口走的，宽度不能写死 —— 写死了主窗口一宽就变成
        // 一条贴在左边的窄条，右边全是空的。
        _input.Width = double.NaN;
        _input.HorizontalAlignment = HorizontalAlignment.Stretch;
        _input.FontSize = 14;
        _input.Padding = new Thickness(8, 7, 8, 7);
        panel.Children.Add(_input);

        var btns = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = Ui.BtnIconKey("msg.cancel", "x", false);
        cancel.Click += (_, _) => CloseWith(false);
        var ok = Ui.BtnIconKey("msg.ok", "check-circle", true);
        ok.Click += (_, _) => { Value = _input.Text.Trim(); CloseWith(true); };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        panel.Children.Add(btns);

        // 标题是调用方运行时传进来的（"重命名…" 这种），没有固定的语言 key，
        // 就不打戳了 —— 打了反而是错的。
        WantWidth = 520;                      // 一行输入
        Install(panel);
    }
}

// ================================================================ 设置
//
// 以前这个框只有一个 JDK 列表，改完还得点「关闭」才写盘，而且主题和语言
// 根本没地方改。现在每一项都当场写进 settings.txt（js_settings_set），
// 主题和语言立刻生效——改了看不见、关了不保存的框等于没有。

internal sealed class SettingsDialog : DlgPanel
{
    private readonly ComboBox _theme;
    private readonly ComboBox _lang;
    private readonly TextBox _java;
    private readonly TextBlock _note;

    internal override IInputElement InitialFocus => _java;

    public SettingsDialog()
    {
        Title = Lang.T("dlg.settings");

        // 内容撑满卡片就行，不用再自己限宽：卡片宽度由 WantWidth 定，
        // 卡片多宽表单就多宽，不会出现「输入框横穿宽屏」那种散架的样子。
        var panel = new StackPanel { Margin = new Thickness(30, 24, 30, 24) };

        // ---- 主题
        panel.Children.Add(Ui.LabelKey("dlg.theme"));
        _theme = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 0, 12),
                                Foreground = Ui.B("Brush.Fg.Primary"),
                                HorizontalAlignment = HorizontalAlignment.Left };
        _theme.Tag = "dlg.theme";
        _theme.Items.Add(Lang.T("dlg.light")); _theme.Items.Add(Lang.T("dlg.dark"));
        _theme.SelectedIndex = Config.Theme == "dark" ? 1 : 0;
        _theme.SelectionChanged += (_, _) =>
        {
            bool dark = _theme.SelectedIndex == 1;
            Config.Theme = dark ? "dark" : "light"; Config.Save();
            Core.SetSetting("theme", Config.Theme);
            Theme.Apply(dark);
        };
        panel.Children.Add(_theme);

        // ---- 语言
        panel.Children.Add(Ui.LabelKey("dlg.language"));
        _lang = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 0, 12),
                               HorizontalAlignment = HorizontalAlignment.Left,
                               Foreground = Ui.B("Brush.Fg.Primary") };
        _lang.Items.Add("中文"); _lang.Items.Add("English");
        _lang.SelectedIndex = Lang.Current == Lang.EN ? 1 : 0;
        _lang.SelectionChanged += (_, _) =>
        {
            Lang.Set(_lang.SelectedIndex == 1 ? Lang.EN : Lang.ZH);
            if (Application.Current.MainWindow is MainWindow mw) mw.RefreshLanguage();
            RefreshNote();
        };
        panel.Children.Add(_lang);

        // ---- 设置一：默认 JDK 版本（内置，可展开）----
        panel.Children.Add(JdkSection());

        // ---- 设置二：选择本地 JDK（外部目录）----
        panel.Children.Add(Ui.Label(Lang.T("dlg.extJdk")));
        _java = Ui.Input(Config.LocalJdk, 380);
        var browse = Ui.BtnIconKey("dlg.browsePlain", "folder-open", false);
        browse.Click += (_, _) =>
        {
            string picked = Pickers.PickFolder(this, Lang.T("m.openProject"), _java.Text);
            if (!string.IsNullOrEmpty(picked)) _java.Text = picked;
        };
        panel.Children.Add(Ui.Row(_java, browse));

        _note = Ui.Label("");
        panel.Children.Add(_note);

        var btns = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = Ui.BtnIconKey("msg.cancel", "x", false);
        cancel.Click += (_, _) => CloseWith(false);
        var ok = Ui.BtnIconKey("dlg.save", "save", true);
        ok.Click += (_, _) =>
        {
            string p = _java.Text.Trim();
            if (p.Length > 0 && !File.Exists(Path.Combine(p, "bin", "java.exe")))
            {
                _note.Text = Lang.T("dlg.noJavaExe");
                _note.Foreground = Ui.B("Brush.Error");
                return;
            }
            Core.SetSetting("extJava", p);
            Core.SetSetting("theme", _theme.SelectedIndex == 1 ? "dark" : "light");
            Core.SetSetting("lang", _lang.SelectedIndex == 1 ? Lang.EN : Lang.ZH);
            CloseWith(true);
        };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        panel.Children.Add(btns);
        Lang.Tag(this, "dlg.settings");
        WantWidth = 660;                      // 设置
        Install(panel);
    }

    /// <summary>切语言后把框里「还没打戳的活文字」重算一遍（校验提示这类动态文案）。</summary>
    private void RefreshNote()
    {
        Lang.Translate(this);
    }

    // 默认 JDK 版本：可展开，里面是内置 JDK17 / JDK21 / JDK25，
    // 点一下立刻设为默认（Core.SelectJdk 当场写盘）。
    private static UIElement JdkSection()
    {
        var exp = new Expander
        {
            Margin = new Thickness(0, 4, 0, 12),
            IsExpanded = true,
            FontSize = 13,
        };
        exp.SetResourceReference(Expander.ForegroundProperty, "Brush.Fg.Primary");
        exp.Header = Lang.T("dlg.defJdk");
        exp.Tag = "dlg.defJdk";

        var body = new StackPanel { Margin = new Thickness(2, 8, 2, 0) };
        var builtins = Core.Jdks().Where(IsBuiltin).OrderBy(j => j.Major).ToList();
        int cur = Core.CurrentJdk().Major;
        var rows = new List<(Core.JdkInfo j, Border b)>();
        foreach (var j in builtins)
        {
            var b = MakeJdkRow(j, j.Major == cur);
            b.MouseLeftButtonDown += (_, _) =>
            {
                Core.SelectJdk(j.Major);
                cur = j.Major;
                foreach (var (jj, bb) in rows) Highlight(bb, jj.Major == cur);
            };
            rows.Add((j, b));
            body.Children.Add(b);
        }
        if (builtins.Count == 0)
            body.Children.Add(new TextBlock { Text = Lang.T("libs.noBuiltinJdk"), FontSize = 12,
                Foreground = Ui.B("Brush.Fg.Dim") });
        exp.Content = body;
        return exp;
    }

    /// <summary>
    /// 内置 JDK = 随软件一起发布的那套，由内核扫描时打上标记（Core.JdkInfo.Builtin）。
    /// 别再靠路径里有没有 "\jdk\jdk-" 去猜：用户自己装一套到同名目录里就会混进来，
    /// 而装在别处的内置 JDK（安装时改了目录）反而认不出来。
    /// </summary>
    private static bool IsBuiltin(Core.JdkInfo j) => j.Builtin;

    private static Border MakeJdkRow(Core.JdkInfo j, bool sel)
    {
        var b = new Border { Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8), Cursor = System.Windows.Input.Cursors.Hand };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Ellipse.FillProperty, sel ? "Brush.Accent" : "Brush.Border");
        sp.Children.Add(dot);
        var lbl = new TextBlock { Text = j.Label, FontSize = 13 };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        sp.Children.Add(lbl);
        // 内置的显示「内置」，不把安装目录下的完整路径甩出来。
        var home = new TextBlock { Text = "  " + j.LocationText, FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        home.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        sp.Children.Add(home);
        b.Child = sp;
        Highlight(b, sel);
        return b;
    }

    private static void Highlight(Border b, bool sel)
    {
        b.BorderThickness = new Thickness(1);
        b.SetResourceReference(Border.BackgroundProperty, sel ? "Brush.Bg.Active" : "Brush.Bg.Raised");
        b.SetResourceReference(Border.BorderBrushProperty, sel ? "Brush.Accent" : "Brush.Border");
    }
}

// ================================================================ 新建条目
//
// 项目树上右键出来的「新建」：Java 类 / Java 文件 / 文件夹 / XML 文件 /
// 自定义后缀。
//
// 后缀这件事踩过坑，规则现在是三条，别再改回去：
//
//   1. 选了具体类型（Java 类 / Java 文件 / XML）就**自动补后缀**，名字里只写
//      类名。原来这里写的是 `name + ".java"` 写死的，于是「新建 XML 文件、
//      输入 Foo」得到的是一个叫 Foo.java、里面装 xml 内容的文件 —— 后缀和
//      内容各算各的，对不上。现在后缀和示例代码都从同一个 ext 派生。
//   2. 「自定义后缀文件」不补，必须自己写全（`data.json`）。没写点就红字拦下，
//      而不是悄悄生成一个没后缀的空文件。
//   3. 名字里自带后缀时听名字的（`pom.xml` 就算类型是 Java 类也按 XML 处理），
//      类型下拉只是给懒得打后缀的人用的。

internal sealed class NewItemDialog : DlgPanel
{
    public string CreatedPath { get; private set; } = "";

    private readonly ComboBox _kind;
    private readonly TextBox _name;
    private readonly TextBlock _hint;
    private readonly string _dir;

    // 类型锁定时用来替掉下拉框的那个只读框（见 LockKind）
    private readonly Border _kindFixed;
    private readonly TextBlock _kindFixedText;
    private readonly Grid _kindSlot = new() { Margin = new Thickness(0, 0, 0, 12) };

    public NewItemDialog(string dir)
    {
        _dir = dir;
        Title = Lang.T("dlg.newItem");

        var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        panel.Children.Add(PageTitle("dlg.newItem", 17));

        panel.Children.Add(Ui.LabelKey("dlg.type"));
        _kind = new ComboBox { Width = 300, Margin = new Thickness(0, 0, 0, 12),
                               Foreground = Ui.B("Brush.Fg.Primary"),
                               HorizontalAlignment = HorizontalAlignment.Left };
        _kind.Items.Add(Lang.T("ni.javaClass"));
        _kind.Items.Add(Lang.T("ni.javaFile"));
        _kind.Items.Add(Lang.T("ni.folder"));
        _kind.Items.Add(Lang.T("ni.xml"));
        _kind.Items.Add(Lang.T("ni.custom"));
        _kind.SelectedIndex = 0;

        // 只读的「已选定」框：长得跟下拉框一样，就是没有右边那个小三角。
        // 类型定死了还画一个三角，等于在说「点我还能改」，点了却没反应。
        _kindFixed = new Border
        {
            Width = 300, Height = 30, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _kindFixed.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Raised");
        _kindFixed.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _kindFixed.BorderThickness = new Thickness(1);
        _kindFixedText = new TextBlock
        {
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0)
        };
        _kindFixedText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        _kindFixed.Child = _kindFixedText;

        // 类型这一行是个「槽」，里面放下拉框还是只读框，看有没有锁定
        _kindSlot.Children.Add(_kind);
        panel.Children.Add(_kindSlot);

        panel.Children.Add(Ui.LabelKey("dlg.name"));
        _name = Ui.Input("", 300);
        panel.Children.Add(_name);

        _hint = Ui.Label("");
        _hint.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_hint);

        var btns = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = Ui.BtnIconKey("msg.cancel", "x", false);
        cancel.Click += (_, _) => CloseWith(false);
        var ok = Ui.BtnKey("dlg.create", true);
        ok.Click += (_, _) => Create();
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        panel.Children.Add(btns);
        Lang.Tag(this, "dlg.newItem");
        WantWidth = 560;                      // 新建条目
        Install(panel);

        // 提示要跟着「类型 + 名字」两个东西变，所以两边都挂上
        _kind.SelectionChanged += (_, _) => RefreshHint();
        _name.TextChanged += (_, _) => RefreshHint();
        RefreshHint();
    }

    internal override IInputElement InitialFocus => _name;

    /// <summary>从外面指定新建的类型（右键菜单里点哪个就建哪个）。</summary>
    /// <param name="kind">类型下标。</param>
    /// <param name="locked">
    /// true = 定死，进到弹窗里不能再改。右键菜单里已经选过「新建 XML 文件」了，
    /// 弹窗里那个下拉还让人改来改去，用户会以为自己点错了。
    /// 顶部菜单的「新建文件 Ctrl+N」是通用入口，不锁。
    /// </param>
    public void SetKind(int kind, bool locked = false)
    {
        if (kind >= 0 && kind < _kind.Items.Count) _kind.SelectedIndex = kind;

        // 锁定了就把下拉框换成只读框。不是简单 IsEnabled=false ——
        // 灰掉的下拉框还留着右边那个小三角，看着像「还能点开」，点了却没反应。
        // ⚠️ 换之前必须 Clear：一个 UIElement 只能有一个逻辑父级，
        // 不摘下来直接塞进别的父级会抛「已经是另一个元素的逻辑子元素」。
        if (locked)
        {
            _kindFixedText.Text = _kind.SelectedItem as string ?? "";
            _kindSlot.Children.Clear();
            _kindSlot.Children.Add(_kindFixed);
        }
        else if (!_kindSlot.Children.Contains(_kind))
        {
            _kindSlot.Children.Clear();
            _kindSlot.Children.Add(_kind);
        }

        RefreshHint();
    }

    /// <summary>自检用：预填名字（走的是改文本那条路，提示会跟着刷）。</summary>
    internal void SetNameForTest(string name) => _name.Text = name;

    /// <summary>
    /// 自检用：不弹窗直接建一次，返回建出来的路径；失败返回 "ERR:原因"。
    /// 「后缀补得对不对、示例代码跟不对得上类型」就靠这个验。
    /// </summary>
    internal string CreateForTest(string name)
    {
        _name.Text = name;
        Create();
        return string.IsNullOrEmpty(CreatedPath) ? "ERR:" + _hint.Text : CreatedPath;
    }

    // ---------------------------------------------------------------- 提示

    /// <summary>
    /// 名字框下面那行字。两种：
    ///   自定义后缀 → 红字「后缀要自己写」；
    ///   其它类型   → 灰字「会自动补上 .java」。
    /// 预计要建的文件名也在这儿写出来，省得建完才发现后缀不对。
    /// </summary>
    private void RefreshHint()
    {
        if (_kind.SelectedIndex == 4)      // 自定义后缀：红字提醒
        {
            _hint.Text = Lang.T("ni.needExt");
            _hint.Foreground = Ui.B("Brush.Error");
            return;
        }

        string name = _name.Text.Trim();
        if (_kind.SelectedIndex == 2 || name.Length == 0)
        {
            _hint.Text = "";
            return;
        }

        string ext = ExtFor(name);
        _hint.Text = string.Format(Lang.T("ni.willCreate"),
                                   name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext);
        _hint.Foreground = Ui.B("Brush.Fg.Dim");
    }

    /// <summary>红字报错（建失败时用）。</summary>
    private void Fail(string msg)
    {
        _hint.Text = msg;
        _hint.Foreground = Ui.B("Brush.Error");
    }

    // ---------------------------------------------------------------- 后缀

    /// <summary>这个类型对应的后缀。文件夹和自定义返回 ""。</summary>
    private string ExtFor(string name)
    {
        string ext = Path.GetExtension(name);
        if (ext.Length > 0) return ext;      // 名字自带后缀：听名字的
        return _kind.SelectedIndex switch { 3 => ".xml", 0 => ".java", 1 => ".java", _ => "" };
    }

    // ---------------------------------------------------------------- 创建

    private void Create()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { Fail(Lang.T("ni.empty")); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { Fail(Lang.T("ni.badChars")); return; }

        if (_kind.SelectedIndex == 2)   // 文件夹：跟后缀没关系，单独走
        {
            var d = Path.Combine(_dir, name);
            if (Directory.Exists(d)) { Fail(Lang.T("ni.fileExists")); return; }
            Directory.CreateDirectory(d);
            CreatedPath = d;
            Core.Log("OK", string.Format(Lang.T("log.folderCreated"), name));
            CloseWith(true);
            return;
        }

        string ext = ExtFor(name);

        // 自定义后缀：后缀得自己写，不然不知道要建什么
        if (_kind.SelectedIndex == 4 && Path.GetExtension(name).Length == 0)
        { Fail(Lang.T("ni.needExt")); return; }

        // 自动补后缀。⚠️ 就这一处补，别在下面再拼一次 —— 以前正是补了两次
        // （一次按类型、一次写死 .java），才出现「XML 文件建出 .java」。
        if (Path.GetExtension(name).Length == 0 && ext.Length > 0) name += ext;

        var file = Path.Combine(_dir, name);
        if (File.Exists(file)) { Fail(Lang.T("ni.fileExists")); return; }

        // 示例代码按最终后缀来，跟类型下拉无关（自带后缀时听名字的）
        string cls = Path.GetFileNameWithoutExtension(file);
        File.WriteAllText(file, SampleFor(ext, cls));
        CreatedPath = file;
        Core.Log("OK", string.Format(Lang.T("log.itemCreated"), Path.GetFileName(file)));
        CloseWith(true);
    }

    /// <summary>
    /// 示例代码。只认**最终后缀**，不看类型下拉 ——
    /// 名字里自带后缀时（`pom.xml`）听名字的，下拉说什么不算。
    ///
    /// ⚠️ 两种 Java 类型给的东西是一样的（都是完整可跑的 public class + main）。
    /// 之前给「Java 文件」塞的是 `class X {}`（没有 public、没有入口），
    /// 既编译不出东西也看不出这是个什么，用户直接当成 bug 报了两次。
    /// 预设的那几种类型各自有各自的样板，但同一个后缀就是同一份样板。
    /// </summary>
    private static string SampleFor(string ext, string cls)
    {
        if (ext.Equals(".java", StringComparison.OrdinalIgnoreCase))
            return $"public class {cls} {{\n"
                 + "    public static void main(String[] args) {\n"
                 + "        System.out.println(\"hi\");\n"
                 + "    }\n"
                 + "}\n";

        if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root>\n</root>\n";

        // 自定义后缀：不知道该写什么，给个空文件，别瞎猜
        return "\n";
    }
}

// ================================================================ 图标库
//
// 这个框踩过两个坑，都跟「用了 StackPanel 装滚动内容」有关：
//
//   1) 滑不动。StackPanel 在竖直方向上不给子元素高度上限，ScrollViewer 拿到的
//      可用高度就是「内容要多少有多少」，于是它永远判定「不需要滚动条」——
//      滚轮转了也没用，滚动范围是 0。必须换成会给子元素定高的容器（Grid / DockPanel），
//      让 ScrollViewer 真正被约束在窗口里，才会出现滚动范围。
//   2) WrapPanel 不换行。WrapPanel 的换行是拿「父容器给的宽度」去折行的，
//      而它自己在 ScrollViewer 里时，量到的可用宽度是无穷大（内容想要多宽就给多宽），
//      结果 177 个格子摊成一长条横线，既不换行也看不全。
//      修法：别让 WrapPanel 自己当滚动内容，把 ScrollViewer 的宽度显式传下去
//      （绑定到 ScrollViewer 的 ViewportWidth），它才知道该在哪一列折行。
//
// 搜索这边：输入框原来也在滚动区里，跟着一起滚走；现在把标题 + 搜索固定在顶部，
// 只有网格滚动。另外加了个「没有匹配」的空态提示，免得搜不到时看着像卡死。

/// <summary>
/// 「关于」。
///
/// 要的是一张**正规的关于页**：软件身份（名字/版本/一句话）在上面，下面是
/// 「用到了哪些开源组件 + 各自什么许可」、开发团队、以及本机运行环境。
/// 不是把几行「标签：值」堆在一起就完事 —— 用户拿这一页回答的是
/// 「这是什么东西、谁做的、里面用了别人的什么代码」。
///
/// ⚠️ 组件表里每一条都得是真的。写这一版时核过：
///   - .NET 9 / WPF：Microsoft，MIT
///   - AvalonEdit 6.3.1：ICSharpCode，MIT（csproj 里的 PackageReference，版本号抄的它）
///   - 图标：assets/icons 下的 177 个 SVG，**来自 Morphicons**（用户确认的；index.md
///     那句「S-IDE 和 S++ 应用共用这一套」说的是**谁在用**，不是谁画的 —— 我据此
///     误判成「本项目自带」过一次，已订正）。
///     它的许可**没能核实**：公开检索到的 "Morphicons" 只有 2026 年那个图标变形
///     动画库（MIT，Guillermo），不是图标集，所以许可列写「见官方授权」而不是
///     随手填个 MIT —— 第三方资产的许可不能猜。等确认了只改这一个字符串。
///   - C++ 内核只用了标准库（src/** 里没有任何 vendored 依赖）。
/// </summary>
internal sealed class AboutDialog : DlgPanel
{
    /// <summary>「已复制」的临时提示，复制完亮一下。</summary>
    private readonly TextBlock _copyHint = new()
    {
        FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(10, 0, 0, 0), Visibility = Visibility.Collapsed
    };

    public AboutDialog()
    {
        Title = Lang.T("dlg.about");

        var asm = typeof(AboutDialog).Assembly;
        string ver = InfoVersion();
        // 团队名不硬编码在界面里：csproj 的 <Company> 是唯一来源，
        // 改了那处，关于页和 exe 的文件属性一起跟着变。
        string company = AttrText<AssemblyCompanyAttribute>(asm, a => a.Company);
        if (company.Length == 0) company = "Byteforge-工作室";

        var panel = new StackPanel { Margin = new Thickness(26, 18, 26, 16) };

        // ---- 头部：图标 + 名字 + 版本 + 副标题
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tile = new Border { Width = 58, Height = 58, CornerRadius = new CornerRadius(16),
                                VerticalAlignment = VerticalAlignment.Center };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        tile.BorderThickness = new Thickness(1);
        var mark = Icons.Visual("coffee", 30, Ui.B("Brush.Accent"));
        if (mark != null)
        {
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = mark;
        }
        head.Children.Add(tile);

        var titles = new StackPanel { Margin = new Thickness(16, 0, 0, 0),
                                      VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Tx(Lang.T("app.name"), 20, "Brush.Fg.Primary", true));
        titles.Children.Add(Tx(Lang.T("app.slogan"), 12, "Brush.Fg.Dim", false,
                               new Thickness(0, 3, 0, 0)));
        titles.Children.Add(Tx(string.Format(Lang.T("about.version"), "v" + ver), 11, "Brush.Fg.Dim",
                               false, new Thickness(0, 3, 0, 0)));
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        panel.Children.Add(head);

        panel.Children.Add(Tx(Lang.T("about.intro"), 12, "Brush.Fg.Muted", false,
                              new Thickness(0, 0, 0, 6)));

        // ---- 两栏：左边「开源组件」，右边「开发 + 运行环境」
        // 单栏排下来内容 646 高，而卡片最多 561，只能靠外层滚动条 —— 关于页
        // 一进来就该一屏看完，不该先滚一下。并成两栏正好把 700 的宽度用起来。
        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(cols);

        var colL = new StackPanel();
        var colR = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        cols.Children.Add(colL);
        Grid.SetColumn(colR, 2);
        cols.Children.Add(colR);

        // ---- 左栏：开源组件
        colL.Children.Add(SectionTitle("about.sec.components", 0));
        var comps = Card(colL);
        var head0 = new Grid { Margin = new Thickness(11, 7, 11, 5) };
        head0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        head0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var c1 = Tx(Lang.T("about.col.component"), 11, "Brush.Fg.Dim");
        var c2 = Tx(Lang.T("about.col.version"), 11, "Brush.Fg.Dim");
        var c3 = Tx(Lang.T("about.col.license"), 11, "Brush.Fg.Dim");
        Grid.SetColumn(c2, 1); Grid.SetColumn(c3, 2);
        head0.Children.Add(c1); head0.Children.Add(c2); head0.Children.Add(c3);
        comps.Children.Add(head0);

        // 版本一律**运行时读**，不手写。写死的数字在升级运行时 / 升级 NuGet 包
        // 之后就变成假的（原来那两行是照 csproj 抄的 "9.0" 和 "6.3.1"）。
        comps.Children.Add(CompRow(Lang.T("about.runtime"), Environment.Version.ToString(), "MIT"));
        comps.Children.Add(CompRow("AvalonEdit",
                                   AsmVersion(typeof(ICSharpCode.AvalonEdit.TextEditor).Assembly),
                                   "MIT"));
        // 第三方资产的许可不许猜：Morphicons 的许可没核到（公开的 "Morphicons"
        // 是另一个图标变形库），这里就写「见官方授权」，不替它编一个。
        comps.Children.Add(CompRow("Morphicons", Icons.Count.ToString() + " " + Lang.T("about.icons.unit"),
                                   Lang.T("about.license.unknown")));
        comps.Children.Add(Tx(Lang.T("about.own"), 11, "Brush.Fg.Dim", false,
                              new Thickness(11, 6, 11, 9)));

        // ---- 右栏：开发
        colR.Children.Add(SectionTitle("about.sec.team", 0));
        var team = Card(colR);
        team.Children.Add(KvRow(Lang.T("about.team"), company));
        // C++ 20 是编译开关，运行时问不到（别的地方也一样），只有 .NET 那段能实时取
        team.Children.Add(KvRow(Lang.T("about.stack"),
                                string.Format(Lang.T("about.stack.value"), Environment.Version)));

        // ---- 右栏：运行环境
        colR.Children.Add(SectionTitle("about.sec.env"));
        var env = Card(colR);
        env.Children.Add(KvRow(Lang.T("about.jdk"), Core.CurrentJdk().Label));
        env.Children.Add(KvRow(Lang.T("about.libs"), Core.Libs().Count.ToString()));
        env.Children.Add(KvRow(Lang.T("about.core"), CoreVersion()));
        // 配置目录是长路径：单行 + 省略号，不然它一条就能折三行
        env.Children.Add(KvRow(Lang.T("about.config"), Core.AppDataDir, true));

        // ---- 底部：复制信息 + 确定
        var foot = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Left };
        var copy = Ui.BtnKey("about.copy");
        copy.Click += (_, _) => CopyInfo(ver, company);
        left.Children.Add(copy);
        _copyHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Ok");
        left.Children.Add(_copyHint);
        foot.Children.Add(left);
        var ok = Ui.BtnKey("about.ok", true);
        ok.Click += (_, _) => CloseWith(true);
        Grid.SetColumn(ok, 1);
        foot.Children.Add(ok);
        panel.Children.Add(foot);

        Lang.Tag(this, "dlg.about");
        // 宽度给到 700：内容里有长说明句和长路径，窄了会多折两三行，
        // 卡片就得靠外层 ScrollViewer 才放得下（关于页最好一屏看完）。
        WantWidth = 700;
        Install(panel);
    }

    // ---- 排版小件（都是静态的，跟着主题走 DynamicResource）

    private static TextBlock Tx(string text, double size, string brush,
                                bool bold = false, Thickness margin = default)
    {
        var t = new TextBlock { Text = text, FontSize = size, Margin = margin,
                                TextWrapping = TextWrapping.Wrap };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>分组小标题。<paramref name="top"/> 是上边距（两栏里第一块给 0）。</summary>
    private static TextBlock SectionTitle(string key, double top = 11)
    {
        var t = Tx(Lang.T(key), 11.5, "Brush.Fg.Dim", true, new Thickness(2, top, 0, 6));
        t.Tag = key;
        return t;
    }

    /// <summary>
    /// 往 <paramref name="host"/> 里放一张卡片，返回能往里塞行的容器。
    ///
    /// ⚠️ 得把外层的 Border 一起加进去。返回「里面那个 StackPanel」而不加 Border
    /// 的话，那个 StackPanel 已经有了逻辑父级（Border），再往页面里 Add 会抛
    /// 「指定的元素已经是另一个元素的逻辑子元素」。
    /// </summary>
    private static StackPanel Card(StackPanel host)
    {
        var inner = new StackPanel();
        var b = new Border { Child = inner, CornerRadius = new CornerRadius(9) };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        b.BorderThickness = new Thickness(1);
        host.Children.Add(b);
        return inner;
    }

    /// <summary>组件表的一行：名字（可伸缩） + 版本 + 许可。</summary>
    private static Grid CompRow(string name, string ver, string lic)
    {
        var g = new Grid { Margin = new Thickness(11, 4, 11, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        g.Children.Add(Tx(name, 12.5, "Brush.Fg.Primary"));

        var v = Tx(ver, 12, "Brush.Fg.Muted");
        v.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(v, 1);
        g.Children.Add(v);

        // 许可做成一个小圆角标签，比又一列灰字好认（MIT / 本项目 一眼扫到）
        var chip = new Border { CornerRadius = new CornerRadius(5),
                                Padding = new Thickness(7, 2, 7, 2),
                                HorizontalAlignment = HorizontalAlignment.Left,
                                VerticalAlignment = VerticalAlignment.Center,
                                Child = Tx(lic, 11, "Brush.Fg.Muted") };
        chip.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Base");
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        chip.BorderThickness = new Thickness(1);
        Grid.SetColumn(chip, 2);
        g.Children.Add(chip);
        return g;
    }

    /// <summary>
    /// 「标签   值」一行。
    ///
    /// <paramref name="oneLine"/> 给长路径用：省成「…\JavaStudio」+ 悬停看全。</summary>
    private static Grid KvRow(string label, string value, bool oneLine = false)
    {
        var g = new Grid { Margin = new Thickness(11, 4, 11, 4) };
        // 标签列原来给 130，四字标签（开发团队 / 配置目录）只占 48，
        // 白白挤掉值那一列。收到 96，值那边多出 34px。
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = Tx(label, 12, "Brush.Fg.Dim");
        l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);

        var v = Tx(value, 12, "Brush.Fg.Primary");
        v.VerticalAlignment = VerticalAlignment.Center;
        v.ToolTip = value;
        if (oneLine)
        {
            // ⚠️ 光设 TextTrimming 没用：Tx 默认 TextWrapping.Wrap，
            // 而 Wrap 一生效 Trimming 就完全不作用 —— 结果是配置目录那种
            // 长路径折成三行，把整张卡片顶高。要省略号就得先把 Wrap 关掉。
            // 短的（JDK / 技术栈）照旧允许折行：宁可两行，也别被截掉半句。
            v.TextWrapping = TextWrapping.NoWrap;
            v.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    // ---- 版本：全部运行时读，一个数字都不手抄
    //
    // 关于页以前是照 csproj 抄的 "9.0" / "6.3.1"：升级一次运行时或升一次
    // NuGet 包，那两行就开始说谎，而且没人会发现。现在三处都从产物里问。

    /// <summary>读一个程序集特性里的字符串。取不到给空串，不抛。</summary>
    private static string AttrText<T>(Assembly asm, Func<T, string> pick) where T : Attribute
    {
        try
        {
            var a = asm.GetCustomAttributes(typeof(T), false);
            if (a.Length > 0 && a[0] is T t) return pick(t) ?? "";
        }
        catch { /* 读不到特性不算错误，退回兜底值 */ }
        return "";
    }

    /// <summary>把 informational 版本里 "+&lt;commit&gt;" 那段构建元数据剪掉 —— 那不是给人看的。</summary>
    private static string TrimMeta(string v)
    {
        int plus = v.IndexOf('+');
        return plus >= 0 ? v.Substring(0, plus) : v;
    }

    /// <summary>
    /// 本程序的版本串（"0.1.0-Preview.1"）。
    ///
    /// ⚠️ 必须读 AssemblyInformationalVersion，不能读 AssemblyVersion：
    /// AssemblyVersion 只收四个整数，SDK 把 <c>&lt;Version&gt;0.1.0-Preview.1&lt;/Version&gt;</c>
    /// 往里写的时候会把 "-Preview.1" 直接丢掉，只剩 0.1.0.0。
    /// informational 那份才是原样留着的。前面那个 "v" 在调用处加。
    /// </summary>
    private static string InfoVersion()
    {
        string v = TrimMeta(AttrText<AssemblyInformationalVersionAttribute>(
            typeof(AboutDialog).Assembly, a => a.InformationalVersion));
        return v.Length > 0
            ? v
            : typeof(AboutDialog).Assembly.GetName().Version?.ToString() ?? "";
    }

    /// <summary>第三方程序集的版本：优先 informational（NuGet 包一般是完整串），没有就退回 AssemblyVersion。</summary>
    private static string AsmVersion(Assembly asm)
    {
        string v = TrimMeta(AttrText<AssemblyInformationalVersionAttribute>(
            asm, a => a.InformationalVersion));
        return v.Length > 0 ? v : asm.GetName().Version?.ToString() ?? "";
    }

    /// <summary>
    /// 内核版本：向 javastudio_core.dll 自己要。问不到（老内核没编 js_version）
    /// 就如实写「未标注」—— 编一个数字出来不如说不知道。
    /// </summary>
    private static string CoreVersion()
    {
        string v = Core.CoreVersion();
        return v.Length > 0 ? v : Lang.T("about.version.unknown");
    }

    /// <summary>把这一页的内容拼成纯文本丢进剪贴板 —— 提问题时贴给开发者最省事。</summary>
    private void CopyInfo(string ver, string company)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            // 和上面显示的那份是同一个来源：内核版本也是问 DLL 要的。
            sb.AppendLine($"{Lang.T("app.name")} v{ver}");
            sb.AppendLine($"{Lang.T("about.team")}: {company}");
            sb.AppendLine($"{Lang.T("about.jdk")}: {Core.CurrentJdk().Label}");
            sb.AppendLine($"{Lang.T("about.libs")}: {Core.Libs().Count}");
            sb.AppendLine($"{Lang.T("about.core")}: {CoreVersion()}");
            sb.AppendLine($"{Lang.T("about.config")}: {Core.AppDataDir}");
            sb.AppendLine($"OS: {Environment.OSVersion.VersionString}");
            sb.AppendLine($".NET: {Environment.Version}");
            Clipboard.SetText(sb.ToString());
            _copyHint.Text = Lang.T("about.copied");
            _copyHint.Visibility = Visibility.Visible;
        }
        catch { /* 剪贴板被别的程序占着就算了，不值得报错打断 */ }
    }
}

// 图标名字表。从 C++ 那边读回来的字典里取，避免再列一遍 177 个名字。
internal static class IconNames
{
    private static List<string> _names;
    public static List<string> Names => _names ??= Load();

    private static List<string> Load()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(Native.IconsAll(Core.IconsDir));
            return doc.RootElement.EnumerateObject().Select(p => p.Name)
                      .OrderBy(x => x).ToList();
        }
        catch { return new List<string>(); }
    }
}
