// MainWindow.xaml.cs —— 主窗口逻辑
//
// 这一层只做「把界面上的意图翻译成 Core 的一次调用，再把结果画出来」。
// 所有真正干活的代码都在 javastudio_core.dll 里。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Win32;

namespace JavaStudio;

public sealed class TreeItem
{
    public string Name { get; set; }
    public string Path { get; set; }
    public bool IsDir { get; set; }
    public List<TreeItem> Children { get; set; } = new();
    public string Glyph => IsDir ? "\uD83D\uDCC1" : "\uD83D\uDCC4";
}

public partial class MainWindow : Window
{
    private string _root = "";
    private string _projectName = "";
    private string _mainClass = "";
    private string _tool = "javac";
    private List<string> _libJars = new();
    private readonly Dictionary<string, TextEditor> _open = new();
    /// <summary>md 文件那层「编辑/拆分/预览」容器，按路径存。切主题时要拿它重渲染。</summary>
    private readonly Dictionary<string, MdPane> _mdPanes = new();

    // JDK 选择（工具栏「运行」右边的下拉）
    //   _projectJdk     = 建项目时选的版本，从 pom.xml / build.gradle 里读回来
    //   _pickedJdkLabel = 用户在下拉里挑的那一项的文案；空 = 没挑，跟随项目
    private int _projectJdk = 0;
    private string _pickedJdkLabel = "";
    private ComboBox _jdkBox;
    private readonly List<JdkPick> _jdkPicks = new();
    private List<Core.JdkInfo> _jdkList = new();

    // To-code Studio Pro：右侧那个 Agent 面板。
    // 它跟随项目——没打开项目时整列隐藏（不是收起，是压根不出现），
    // 打开项目后才露出右侧入口图标。收起只改这一列宽度，聊天记录一直在磁盘上。
    private AgentPanel _agent;
    private bool _agentOpen;

    // API 小弹窗（主窗口内那张卡片）里的三个输入框。
    // 注意这个文件的 nullable 上下文是关的，别加 ? 否则报 CS8632。
    private TextBox _gateBase;
    private PasswordBox _gateKey;
    private ComboBox _gateModel;

    public MainWindow()
    {
        InitializeComponent();
        JavaStudio.Chrome.ApplyMainWindow(this);
        DlgHost.Attach(this);      // 内嵌对话框那一层要在任何对话框弹出前挂好
        // 主窗口一变形，弹窗卡片要跟着重算尺寸（不然缩小主窗口卡片就顶出去了）
        SizeChanged += (_, _) => DlgHost.OnResized();
        Lang.Load();
        Lang.ApplyMenu(MainMenu);
        Lang.ApplyMenu(Tree.ContextMenu);
        BuildToolbar();
        BuildBottomBar();
        BuildTreeActions();
        InitAgent();
        Core.OnLog += OnCoreLog;
        Loaded += (_, _) =>
        {
            RefreshStatusRight();
            // 启动时先按存下来的语言把界面刷一遍。
            // 构造函数里那些 Lang.T() 是在 Lang.Load() 之后调的（所以大体是对的），
            // 但 XAML 里写死的那几处（比如项目树标题）只有走一次刷新才会换过来。
            RefreshLanguage();
            // 命令行带了项目目录就进工作区，否则一律先落在主页上
            if (string.IsNullOrEmpty(_root)) ShowHome();

            // 新手引导：只在第一次启动自动走一遍。
            // 放到 ContextIdle 而不是上面紧接着跑 —— 向导是模态窗口，
            // 主窗口这会儿还没渲染出来，Owner 关系不稳，标题栏位置也算不准。
            bool shotMode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JS_SHOT"));
            bool force = Environment.GetEnvironmentVariable("JS_GUIDE") == "1";
            // JS_GUIDE_TOUR=1 跳过向导直接开巡礼（截图看高亮位置时省得先点几步）
            bool tourOnly = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JS_GUIDE_TOUR"));
            if (tourOnly)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StartTour();
                    GuideState.MarkDone();
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            // 「刚装上的这一份」也算第一次 —— 同一个安装包重装也要给引导。
            // ⚠️ IsFreshInstall 有副作用（比对完会把新 stamp 存下来），
            // 必须放在 || 的**最后**：被前面的条件短路掉时它就不会执行，
            // 那份 stamp 也就不会被"销赃"。shotMode 下同理，别让它白白清掉标记。
            else if ((GuideState.IsFirstRun && !shotMode) || force ||
                     (!shotMode && GuideState.IsFreshInstall()))
                Dispatcher.BeginInvoke(new Action(RunGuide),
                                       System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            // 主页上「上次打开」要显示的是关掉之前正在看的那个项目。
            // 在 OpenProject 里就写进去，所以这里只管清空「没项目」的情况。
            if (string.IsNullOrEmpty(_root)) Core.SetSetting("lastProject", "");
        };

        // 钩子要在构造函数里挂：Show() 之后 Loaded 可能已经发了，
        // 那时候再 += 就永远等不到。
        string shot = Environment.GetEnvironmentVariable("JS_SHOT");
        if (!string.IsNullOrEmpty(shot)) ShotWhenReady(shot);
    }

    // ---------------------------------------------------------------- To-code Studio Pro
    //
    // Agent 面板嵌在主窗口最右一列。开合方式有两种：右侧那条竖着的入口按钮，
    // 或者 Ctrl+Alt+A（用户指定的键；WPF 的 KeyGesture 没法把空格当修饰键，
    // 所以「Ctrl+Alt+空格+A」按 Ctrl+Alt+A 实现）。
    //
    // 它跟随项目：没打开项目时整列 Collapsed（不是收起，是不存在）。
    // 收起只改这一列宽度，面板对象和聊天记录都还在 —— 记录始终落在数据目录里。

    private void InitAgent()
    {
        _agent = new AgentPanel();
        _agent.RequestCollapse += () => SetAgentOpen(false);
        AgentHost.Content = _agent;

        // 收起态：右侧那条竖着的入口（图标在上，竖排名字在下，像 IDEA 的工具窗口条）
        var strip = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10)
        };
        var ic = Icons.Visual("sparkles", 18, "Brush.Fg.Muted");
        if (ic != null) strip.Children.Add(ic);

        var label = new TextBlock { Text = "To-code Studio Pro", FontSize = 10.5 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
        label.LayoutTransform = new System.Windows.Media.RotateTransform(-90);   // 竖排
        label.Margin = new Thickness(0, 14, 0, 0);
        strip.Children.Add(label);

        AgentLaunch.Content = strip;
        SetAgentOpen(false);
        BuildApiGate();
    }

    /// <summary>
    /// 主窗口内那张「填 API」小卡片的 contents。
    /// 用卡片而不是设置窗口：用户明确要求进 Agent 时不要直接甩一个完整设置窗出来，
    /// 这里只问最必要的三样 —— Base URL / API Key / 模型。
    /// </summary>
    private void BuildApiGate()
    {
        var body = ApiGateBody;
        body.Children.Clear();

        var h = new TextBlock { Text = Lang.T("gate.title"), FontSize = 15, FontWeight = FontWeights.SemiBold };
        h.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Primary");
        body.Children.Add(h);

        var tip = new TextBlock
        {
            Text = Lang.T("gate.tip"),
            FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        };
        tip.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        body.Children.Add(tip);

        var s = AgentSettings.Current;
        // 这一排固定宽度的控件全部显式 Left：面板比 340 宽一点点，靠默认的
        // Stretch（+ 显式 Width 会居中）它们就跟上面的标签错开几像素。
        _gateBase = Ui.Input(s.BaseUrl, 340);
        _gateKey = new PasswordBox { Width = 340, Margin = new Thickness(0, 0, 0, 10),
                                     HorizontalAlignment = HorizontalAlignment.Left };
        _gateKey.Password = s.ApiKey;
        _gateModel = new ComboBox
        { Width = 340, IsEditable = true, FontSize = 13, Margin = new Thickness(0, 0, 0, 4),
          HorizontalAlignment = HorizontalAlignment.Left };
        // 默认 deepseek-v4-pro；列表里也给几个常见的，选不到就直接在下拉里手敲
        foreach (var m in new[] { "deepseek-v4-pro", "deepseek-flash" })
            _gateModel.Items.Add(m);
        _gateModel.Text = string.IsNullOrWhiteSpace(s.Model) ? "deepseek-v4-pro" : s.Model;

        body.Children.Add(Ui.Label(Lang.T("gate.baseUrl"))); body.Children.Add(_gateBase);
        body.Children.Add(Ui.Label(Lang.T("gate.apiKey"))); body.Children.Add(_gateKey);
        body.Children.Add(Ui.Label(Lang.T("gate.model"))); body.Children.Add(_gateModel);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = Ui.Btn(Lang.T("msg.cancel"));
        cancel.Click += (_, _) => ApiGate.Visibility = Visibility.Collapsed;
        var ok = Ui.Btn(Lang.T("gate.ok"), true);
        ok.Click += (_, _) =>
        {
            var st = AgentSettings.Current;
            st.BaseUrl = (_gateBase.Text ?? "").Trim();
            st.ApiKey = _gateKey.Password.Trim();
            st.Model = (_gateModel.Text ?? "").Trim();
            if (!st.Ready) { SetStatus(Lang.T("gate.needBoth"), false); return; }
            st.Save();

            ApiGate.Visibility = Visibility.Collapsed;
            SetAgentOpen(true);
            _agent.Attach(_root);
            _agent.FocusInput();
        };
        row.Children.Add(cancel); row.Children.Add(ok);
        body.Children.Add(row);
    }

    /// <summary>没配 API 时把小卡片放出来。已填过的先回填，省得重打。</summary>
    private void ShowApiGate()
    {
        var s = AgentSettings.Current;
        _gateBase.Text = s.BaseUrl;
        _gateKey.Password = s.ApiKey;
        _gateModel.Text = string.IsNullOrWhiteSpace(s.Model) ? "deepseek-v4-pro" : s.Model;
        ApiGate.Visibility = Visibility.Visible;
        _gateKey.Focus();
    }

    /// <summary>没打开项目时把整列藏掉；打开项目才放出来。</summary>
    private void SetAgentAvailable(bool hasProject)
    {
        AgentArea.Visibility = hasProject ? Visibility.Visible : Visibility.Collapsed;
        if (!hasProject) SetAgentOpen(false);
    }

    /// <summary>
    /// 展开 / 收起。收起只是把这一列缩到 34（只留入口图标），
    /// 面板实例和历史都不动，所以「聊天记录永远保留」。
    /// </summary>
    private void SetAgentOpen(bool open)
    {
        _agentOpen = open;
        AgentCol.Width = new GridLength(open ? 300 : 34);
        AgentSplitCol.Width = new GridLength(open ? 4 : 0);
        AgentSplit.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AgentHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AgentLaunch.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 自检用：直接展开对话面板（正常要用户点右侧那个竖条）。
    /// 截图验证「思考过程折叠块」时必须展开才看得到，所以留这个入口。
    /// </summary>
    /// <summary>KeyDown 统一处理：引导层自己的 Esc 它自己管。</summary>
    private void OnGuide(object s, RoutedEventArgs e)
    {
        GuideChecklist.Expanded = true;
        GuideState.ResetGuide();
        RunGuide();
    }

    // ---------------------------------------------------------------- 新手引导
    //
    // 三个阶段串成一条：
    //   1) 向导窗口 —— 选外观、选 JDK、要不要示例项目
    //   2) 界面巡礼 —— 高亮讲一遍各处是干嘛的
    //   3) 首页清单 —— 之后一直挂在那儿，跟着真实状态打勾
    //
    // 「跳过」也算走完了：整套引导不再自动出现，但清单会留着，
    // 用户想看的时候从「帮助 → 新手引导」再点一次。

    private void RunGuide()
    {
        var wiz = new GuideWizard { Owner = this };
        bool finished = false;
        try { finished = wiz.ShowDialog() == true; }
        catch (InvalidOperationException) { return; }   // Owner 还没显示过，启动阶段别硬来

        // 不管走完还是跳过，之后都不再自动弹。引导不是常客。
        GuideState.MarkDone();

        // 示例项目用他在向导里挑的那个 JDK，别写死版本
        if (wiz.WantSampleProject) CreateSampleProject(wiz.ChosenJdkMajor);

        if (finished) StartTour();
        ShowHomeIfVisible();
    }

    private void StartTour()
    {
        var steps = new List<GuideTour.Step>
        {
            new(Toolbar,      "guide.t.toolbar.t", "guide.t.toolbar.d"),
            new(Tree,         "guide.t.tree.t",    "guide.t.tree.d"),
            new(Editors,      "guide.t.editor.t",  "guide.t.editor.d"),
            new(BottomBody,   "guide.t.bottom.t",  "guide.t.bottom.d"),
            new(StatusBarTop, "guide.t.status.t",  "guide.t.status.d"),
        };
        // 看不见的步骤 LayOut 那边会自动跳过 —— 没开项目时项目树是空的，
        // 对着空气讲一遍反而让人更懵。
        if (GuideTour.Run(this, steps)) GuideState.NoteTourFinished();
    }

    /// <summary>
    /// 示例项目：用内核那套约定值建一个能直接跑的 Hello World。
    /// jdkMajor 是用户在向导「选 JDK」那一步挑的版本 —— 0 表示他没挑（机器上
    /// 没检测到 JDK，或者那步被跳过了），这时候自己兜底，绝不写死。
    /// </summary>
    private void CreateSampleProject(int jdkMajor)
    {
        try
        {
            string parent = Core.DefaultProjectsDir();
            if (string.IsNullOrEmpty(parent)) parent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "JavaStudioProjects");

            // 名字撞了就往后排，别覆盖掉人家已有的东西
            string name = "HelloJava";
            for (int i = 2; Directory.Exists(Path.Combine(parent, name)); ++i)
                name = "HelloJava" + i;

            var r = Core.CreateProject(parent, name, "com.example", name.ToLowerInvariant(),
                                       "1.0", "maven", SampleJdkRelease(jdkMajor), true, false);
            if (r.Ok && Directory.Exists(r.Dir))
            {
                OpenProject(r.Dir);   // 打开成功的话它自己会往日志里记一条
                return;
            }

            // 建不出来必须说出来。这步是引导拍胸脯承诺过的，
            // 悄悄吞掉的话用户走完引导还是空手，而且完全不知道发生了什么。
            Fail(r.Error);
        }
        catch (Exception ex) { Fail(ex.Message); }
    }

    /// <summary>
    /// 示例项目用哪个 JDK 版本。顺序是：用户在向导里挑的 → 设置里当前生效的 → 机器上第一个能用的 → 17。
    /// 最后那个 17 只是「什么都不知道时」的兜底，正常走不到。
    /// </summary>
    private static string SampleJdkRelease(int chosen)
    {
        if (chosen > 0) return chosen.ToString();
        if (TryMajor(Core.Setting("jdk"), out int fromSetting) && fromSetting > 0)
            return fromSetting.ToString();
        var first = Core.Jdks().FirstOrDefault(j => j.Ok);
        return first != null && first.Major > 0 ? first.Major.ToString() : "17";
    }

    /// <summary>示例项目没建成时统一走这儿：状态栏 + 日志都留痕。</summary>
    private void Fail(string why)
    {
        SetStatus(Lang.T("guide.w.project.fail"), false);
        Core.Log("WARN", Lang.T("guide.w.project.fail") + " " + (why ?? ""));
    }

    private void ShowHomeIfVisible()
    {
        if (HomeView.Visibility == Visibility.Visible) ShowHome();
    }

    internal void OpenAgentForTest() => SetAgentOpen(true);

    /// <summary>
    /// 自检用：JS_TREE_MENU=dir|file|blank 把项目树右键菜单弹出来看（脚本按不了鼠标右键）。
    /// 三种目标各拍一张，才能确认「文件只有改/删、文件夹和空白能建」这条规则真的生效。
    /// 菜单是独立 Popup，主窗口那套 JS_SHOT（只渲染窗口自己）照不到，得配整屏抓。
    /// </summary>
    internal void ShowTreeMenuForTest(string kind)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Tree.ContextMenu is not ContextMenu m) return;
            if (Tree.ItemsSource is not List<TreeItem> src || src.Count == 0) return;
            Tree.UpdateLayout();

            if (kind == "blank")
            {
                _menuBlank = true;
                _menuDir = _root;
            }
            else
            {
                _menuBlank = false;
                _menuDir = "";
                SelectFirstIn(src, Tree, wantDir: kind != "file");
            }

            m.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
            m.PlacementTarget = Tree;
            m.HorizontalOffset = 20;
            m.VerticalOffset = 20;
            m.IsOpen = true;
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>选中第一个目录（wantDir=true）或第一个文件。找不到就一个都不选。</summary>
    private static bool SelectFirstIn(List<TreeItem> items, ItemsControl parent, bool wantDir)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem c) continue;
            var it = items[i];
            if (it.IsDir == wantDir) { c.IsSelected = true; c.BringIntoView(); return true; }
            if (!it.IsDir) continue;
            // 要找的可能在折叠着的子目录里，得先展开才能拿到它的容器
            c.IsExpanded = true;
            c.UpdateLayout();
            if (SelectFirstIn(it.Children, c, wantDir)) return true;
        }
        return false;
    }

    private void ToggleAgent()
    {
        if (string.IsNullOrEmpty(_root))
        {
            SetStatus(Lang.T("st.noApiProject"), false);
            return;
        }

        // 没填 API 就不给进对话界面：弹主窗口内那张小卡片（不是设置窗口）。
        // 卡片里保存成功会自己把面板展开，所以这里直接 return。
        // 收起方向不走这道闸 —— 已经进去了就别把人关在外面。
        if (!_agentOpen && !AgentSettings.Current.Ready)
        {
            ShowApiGate();
            return;
        }

        SetAgentOpen(!_agentOpen);
        if (_agentOpen) _agent?.FocusInput();
    }

    /// <summary>右侧入口按钮（XAML 里 Click 挂着）。</summary>
    private void OnToggleAgent(object s, RoutedEventArgs e) => ToggleAgent();

    // ---------------------------------------------------------------- 工具栏

    private void OnPlusButton(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) OnNewProject(s, e);
        else NewItem(0);                 // 开着项目：+ 就是建文件/夹
    }

    private void OnFolderButton(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) OnOpenProject(s, e);
        else OnImportFile(s, e);          // 开着项目：文件夹按钮就是导入文件
    }

    /// <summary>主工具栏。IDEA 的工具栏是纯图标 + 分组竖线，这里照做。</summary>
    private void BuildToolbar()
    {
        // 切语言会重新调一次，不清空的话按钮会越加越多
        ToolbarLeft.Children.Clear();
        ToolbarRight.Children.Clear();
        // 开着项目时，+ 是「新建文件/夹」、文件夹是「导入」；
        // 没开项目时才是「新建项目 / 打开项目」（对应截图里红蓝两个箭头的语义）
        AddBtn(ToolbarLeft, "plus", "", OnPlusButton, Lang.T("tip.newFile"));
        AddBtn(ToolbarLeft, "folder-open", "", OnFolderButton, Lang.T("tip.openImport"));
        AddBtn(ToolbarLeft, "save", "", OnSave, Lang.T("tip.save"));
        Sep(ToolbarLeft);
        AddBtn(ToolbarLeft, "zap", "", OnCompile, Lang.T("tip.compile"));
        // 运行是绿色播放键，IDEA 的绿三角
        AddBtn(ToolbarLeft, "play", "", OnCompileRun, Lang.T("tip.run"), "Brush.Ok");
        BuildJdkPicker();          // 运行键右边紧跟着「用哪个 JDK」的下拉
        AddBtn(ToolbarLeft, "check-circle", "", OnCheck, Lang.T("tip.check"));
        AddBtn(ToolbarLeft, "package", "", OnLibs, Lang.T("tip.libs"));
        Sep(ToolbarLeft);
        AddBtn(ToolbarLeft, "home", "", OnHome, Lang.T("tip.home"));
        Sep(ToolbarRight);
        AddBtn(ToolbarRight, "grid", "", OnToggleTree, Lang.T("tip.tree"));
        AddBtn(ToolbarRight, Theme.IsDark ? "sun" : "moon", "", OnToggleTheme, Lang.T("tip.theme"));
        AddBtn(ToolbarRight, "settings", "", OnSettings, Lang.T("tip.settings"));
    }

    // ---------------------------------------------------------------- JDK 选择
    //
    // 装 Rust JDK 的时候装了 17 / 21 / 25 三套都很常见，一个项目该用哪套得当场挑。
    // 规矩是：下拉里挑了就用挑的；不挑（停在第一行）就用建项目时选的那个版本；
    // 项目也没说，就退回设置里指定的外部 JDK，再退回内核自己挑的那套。
    // 这套优先级全落在 JdkHome() 这一个方法里，编译和运行都调它，不会有两套版本。

    /// <summary>下拉里的一项。Home 空着 = 按版本号去找；定了 = 就用这一个。</summary>
    private sealed record JdkPick(string Label, int Major, string Home);

    /// <summary>工具栏上的 JDK 下拉。BuildToolbar 每次重建时都会调到这儿。</summary>
    private void BuildJdkPicker()
    {
        // 重建前先摘掉旧事件，不然旧下拉还会回调到这儿来
        if (_jdkBox != null) _jdkBox.SelectionChanged -= OnJdkPicked;
        _jdkBox = null;
        _jdkList = Core.Jdks().Where(j => j.Ok).ToList();
        if (_jdkList.Count == 0) return;    // 一台 JDK 都没有，摆出来也没得选

        var box = new ComboBox
        {
            Width = 142, Height = 28, FontSize = 12,
            Margin = new Thickness(6, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Lang.T("tip.jdk")
        };
        box.SelectionChanged += OnJdkPicked;
        ToolbarLeft.Children.Add(box);
        _jdkBox = box;
        FillJdkPicks();
    }

    /// <summary>重算下拉的内容，并把之前挑的那项还回去。</summary>
    private void FillJdkPicks()
    {
        if (_jdkBox == null) return;
        _jdkPicks.Clear();
        _jdkBox.Items.Clear();

        // 第一项永远是「跟随项目」：意思是跟着这个项目自己的版本走。
        // 项目说了是 17 就把 17 写在文案里，免得还得点开才知道。
        string auto = _projectJdk > 0
            ? string.Format(Lang.T("jdk.projectJdk"), _projectJdk)
            : Lang.T("jdk.followProject");
        _jdkBox.Items.Add(auto);
        _jdkPicks.Add(new JdkPick(auto, _projectJdk, ""));
        foreach (var j in _jdkList)
        {
            _jdkBox.Items.Add(j.Label);
            _jdkPicks.Add(new JdkPick(j.Label, j.Major, j.Home));
        }

        // 切主题 / 切语言都会重建工具栏，别把用户挑的那项弄丢
        int idx = string.IsNullOrEmpty(_pickedJdkLabel)
            ? 0
            : _jdkPicks.FindIndex(p => p.Label == _pickedJdkLabel && p.Home.Length > 0);
        if (idx < 0) { idx = 0; _pickedJdkLabel = ""; }
        _jdkBox.SelectedIndex = idx;
    }

    private void OnJdkPicked(object s, SelectionChangedEventArgs e)
    {
        if (_jdkBox == null) return;
        int i = _jdkBox.SelectedIndex;
        _pickedJdkLabel = i > 0 && i < _jdkPicks.Count ? _jdkPicks[i].Label : "";
        RefreshStatusRight();
    }

    /// <summary>打开 / 换项目时调用：读出这个项目建的时候用的 JDK，下拉跟着变。</summary>
    private void SyncJdkForProject(string root)
    {
        _projectJdk = DetectProjectJdk(root);
        _pickedJdkLabel = "";      // 换项目＝重新回到「跟随项目」
        FillJdkPicks();
        RefreshStatusRight();
    }

    /// <summary>
    /// 项目建的时候选的 JDK 版本。内核建项目时把它写进了构建脚本里：
    /// pom.xml 的 maven.compiler.release / java.version，或 build.gradle 的
    /// JavaLanguageVersion.of(...)。这里直接读回来，读不到就当没说（返回 0）。
    /// </summary>
    private static int DetectProjectJdk(string root)
    {
        if (string.IsNullOrEmpty(root)) return 0;
        int major = 0;
        try
        {
            string pom = Path.Combine(root, "pom.xml");
            if (File.Exists(pom))
            {
                string xml = File.ReadAllText(pom);
                foreach (var tag in new[]
                         { "maven.compiler.release", "release", "java.version",
                           "maven.compiler.source", "maven.compiler.target" })
                {
                    var m = Regex.Match(xml, "<" + Regex.Escape(tag) + @">\s*(1\.\d|\d{1,2})\s*<");
                    if (m.Success && TryMajor(m.Groups[1].Value, out major)) return major;
                }
            }
            foreach (var name in new[] { "build.gradle", "build.gradle.kts" })
            {
                string g = Path.Combine(root, name);
                if (!File.Exists(g)) continue;
                string t = File.ReadAllText(g);
                var m = Regex.Match(t, @"JavaLanguageVersion\.of\(\s*(\d{1,2})\s*\)");
                if (m.Success && TryMajor(m.Groups[1].Value, out major)) return major;
                m = Regex.Match(t, @"(?:sourceCompatibility|targetCompatibility)\s*=\s*JavaVersion\.VERSION_(\d+)");
                if (m.Success && TryMajor(m.Groups[1].Value, out major)) return major;
                m = Regex.Match(t, @"(?:sourceCompatibility|targetCompatibility)\s*=\s*['""]?(1\.\d|\d{1,2})['""]?");
                if (m.Success && TryMajor(m.Groups[1].Value, out major)) return major;
            }
        }
        catch { /* 构建脚本读不动就当项目没指定版本 */ }
        return 0;
    }

    /// <summary>把 "17"、"21"、"1.8" 这些写法统一成一个主版本号。</summary>
    private static bool TryMajor(string s, out int major)
    {
        major = 0;
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("1.", StringComparison.Ordinal))
            return int.TryParse(s.Substring(2), out major) && major > 0;
        return int.TryParse(s, out major) && major > 0;
    }

    /// <summary>当前真正在用的那套 JDK（状态栏右侧显示用）。</summary>
    private Core.JdkInfo ActiveJdk()
    {
        string home = JdkHome();
        if (!string.IsNullOrEmpty(home))
        {
            var exact = _jdkList.FirstOrDefault(j => j.Ok && SamePath(j.Home, home));
            if (exact != null) return exact;
        }
        return _jdkList.FirstOrDefault(j => j.Ok);
    }

    /// <summary>
    /// 底部工具窗口按钮条。IDEA 里这排按钮常驻，点哪个就把上面那块切成哪个。
    /// </summary>
    private void BuildBottomBar()
    {
        // 这两句缺一不可：_bottomBtns 只是「哪个按钮对应哪个页」的记账表，
        // 真正挂在界面上的是 BottomBarLeft 的子元素。只清记账表不清控件，
        // 切一次语言就多出一排按钮——原来「输出/日志/问题」变成三中三英就是这么来的。
        _bottomBtns.Clear();
        BottomBarLeft.Children.Clear();
        BottomBarRight.Children.Clear();
        AddToolWin("file-text", Lang.T("bottom.output"), 0);
        AddToolWin("terminal", Lang.T("bottom.log"), 1);
        AddToolWin("check-circle", Lang.T("bottom.problems"), 2);
        AddBtn(BottomBarRight, "chevron-down", "", OnToggleBottom, Lang.T("tip.hideBottom"));
        ShowBottom(0);
    }

    private void BuildTreeActions()
    {
        // 标题栏上就这两个位置：+ 是新建文件/夹，↓ 是导入。
        // 原来占着这两个位置的是「刷新」和「隐藏」——刷新还能在别处做，
        // 隐藏工具栏里也有，这两个才是每天要点的。
        // 同理：先清容器再重建，否则切语言会一路往后堆。
        TreeActions.Children.Clear();
        AddIconBtn(TreeActions, "plus", (_, _) => NewItem(0), Lang.T("tip.newFileFolder"));
        AddIconBtn(TreeActions, "arrow-down", OnImportFile, Lang.T("tip.importFile"));
    }

    // ---------------------------------------------------------------- 导入
    //
    // 两步走：先在项目里挑一个文件夹（放到哪儿），再去外面挑要导入的东西
    // （文件或者整个文件夹）。顺序反过来的话，用户很容易把一堆文件直接
    // 甩到项目根上，回头还得自己挪。

    /// <summary>导入的目标目录 = 项目树里选中的那个；没选或选到项目外就提示并返回 false。</summary>
    private bool TryImportTarget(out string target)
    {
        target = "";
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return false; }

        target = TreeTargetDir();   // 选中的是文件就用它所在的目录
        if (string.IsNullOrEmpty(target) ||
            !target.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(Lang.T("st.selectFolder"), false);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 导入文件。用系统的 OpenFileDialog 挑源文件（多选），不再用自绘选择器。
    /// 同名文件直接跳过、不覆盖 —— 这样连「覆盖吗？」那个系统弹窗也不用问了。
    /// </summary>
    private void OnImportFile(object s, RoutedEventArgs e)
    {
        if (!TryImportTarget(out string target)) return;
        try
        {
            var files = Pickers.PickFile(this,
                string.Format(Lang.T("pk.importFileTo"), Path.GetFileName(target.TrimEnd('\\', '/'))),
                "*.*", multi: true, start: target);
            if (files.Length == 0) return;

            int n = 0, skipped = 0;
            foreach (var f in files)
            {
                string to = Path.Combine(target, Path.GetFileName(f));
                if (File.Exists(to)) { skipped++; continue; }
                File.Copy(f, to, false);
                n++;
            }
            RefreshTree();
            SetStatus(string.Format(Lang.T("st.importDone"), n) +
                      (skipped > 0 ? string.Format(Lang.T("st.importSkip"), skipped) : ""), true);
        }
        catch (Exception ex) { SetStatus(Lang.T("st.importFail") + ex.Message, false); }
    }

    /// <summary>导入整个文件夹。同样用系统的目录选择器，不用自绘的。</summary>
    private void OnImportDir(object s, RoutedEventArgs e)
    {
        if (!TryImportTarget(out string target)) return;
        try
        {
            string from = Pickers.PickFolder(this,
                string.Format(Lang.T("pk.importFolderTo"), Path.GetFileName(target.TrimEnd('\\', '/'))),
                target);
            if (string.IsNullOrEmpty(from)) return;

            string name = Path.GetFileName(from.TrimEnd('\\', '/'));
            CopyDir(from, Path.Combine(target, name));
            RefreshTree();
            SetStatus(Lang.T("st.importFolderDone") + name, true);
        }
        catch (Exception ex) { SetStatus(Lang.T("st.importFail") + ex.Message, false); }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from))
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private void Sep(Panel p)
    {
        var r = new System.Windows.Shapes.Rectangle
        { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0) };
        // 资源引用而不是静态画刷，切主题时这跟竖线才不会留在旧颜色上
        r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Border");
        p.Children.Add(r);
    }

    private void AddBtn(Panel p, string icon, string text, RoutedEventHandler click,
                        string tip = "", string brushKey = null)
    {
        var b = new Button { Style = (Style)FindResource("ToolBtn"), ToolTip = string.IsNullOrEmpty(tip) ? text : tip };
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        // 传键名（不是画刷对象）：图标自己挂资源引用，切主题跟着变
        var vis = Icons.Visual(icon, 16, brushKey ?? "Brush.Fg.Muted");
        if (vis != null) stack.Children.Add(vis);
        if (!string.IsNullOrEmpty(text))
            stack.Children.Add(new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(6, 0, 0, 0) });
        b.Content = stack;
        b.Click += click;
        p.Children.Add(b);
    }

    private void AddIconBtn(Panel p, string icon, RoutedEventHandler click, string tip)
    {
        var b = new Button
        {
            Style = (Style)FindResource("ToolBtn"), ToolTip = tip,
            Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(1, 0, 1, 0)
        };
        var vis = Icons.Visual(icon, 14, "Brush.Fg.Dim");
        b.Content = vis ?? (object)"";
        b.Click += click;
        p.Children.Add(b);
    }

    // ---------------------------------------------------------------- 工具窗口

    private readonly List<(Button Btn, int Index)> _bottomBtns = new();
    private int _bottom = 0;
    private double _bottomH = 170;   // 工具窗口收起前有多高，展开时还回去

    private void AddToolWin(string icon, string text, int index)
    {
        var b = new Button
        {
            Style = (Style)FindResource("ToolBtn"),
            Padding = new Thickness(9, 4, 9, 4), FontSize = 11,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = text, FontSize = 11 }
                }
            }
        };
        var vis = Icons.Visual(icon, 13, "Brush.Fg.Muted");
        if (vis != null) ((StackPanel)b.Content).Children.Insert(0,
            new Border { Child = vis, Margin = new Thickness(0, 0, 6, 0) });

        b.Click += (_, _) => ShowBottom(index);
        BottomBarLeft.Children.Add(b);
        _bottomBtns.Add((b, index));
    }

    /// <summary>切换底部工具窗口：0 输出（javac）、1 日志（java 跑起来的输出）、2 问题。</summary>
    private void ShowBottom(int index)
    {
        _bottom = index;
        BuildOut.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunOut.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Problems.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (btn, i) in _bottomBtns)
        {
            bool on = i == index;
            // 用资源引用挂上，切主题时这排按钮的前景/底色自己跟着变，
            // 不用等下一次 ShowBottom 才刷（原来静态画刷会卡在旧主题）。
            btn.SetResourceReference(Button.ForegroundProperty, on ? "Brush.Fg.Primary" : "Brush.Fg.Muted");
            btn.SetResourceReference(Button.BackgroundProperty, on ? "Brush.Bg.Hover" : "Brush.Bg.Panel");
        }
    }

    // ---------------------------------------------------------------- 输出 / 日志
    //
    // 面板分工（按用户定的规矩）：
    //   输出 = 编译器（javac）说的话 + 程序自己打出来的纯文本（System.out …）
    //   日志 = IDE 自己的运行记录（打开项目、保存、下载库、报错…）
    //   问题 = 诊断列表
    // 两个文本面板都按项目落盘，切项目/重启后还能看到，输出不是一次性的。

    private string _logOutPath = "";
    private string _openTabsPath = "";   // 这次还开着的文件清单，存在项目数据目录，下次开项目恢复
    private string _logRunPath = "";
    private string _logProbPath = "";    // 「问题」面板的诊断清单（JSON），下次开项目读回来

    private void OnCoreLog(string level, string msg)
    {
        // 回调来自 C++ 的线程，回 UI 必须切回去
        Dispatcher.BeginInvoke(new Action(() =>
        {
            bool bad = level == "ERROR" || level == "WARN";
            SetStatus(msg, !bad);
            StatusLeft.Foreground = (Brush)FindResource(
                level == "ERROR" ? "Brush.Error"
              : level == "WARN" ? "Brush.Warn"
              : level == "OK" ? "Brush.Ok"
              : "Brush.Fg.Muted");
            // IDE 自己的记录进「日志」。这是软件日志，不是程序输出，
            // 跟「输出」分开（用户要的是纯程序文本不进日志）。
            if (!string.IsNullOrEmpty(_root))
                AppendLog($"[{level}] {msg}");
        }));
    }

    /// <summary>写「输出」：编译输出 + 程序自己打的字，都落这儿。</summary>
    private void AppendBuild(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        BuildOut.AppendText(s + "\n");
        BuildOut.ScrollToEnd();
        Persist(_logOutPath, s);
    }

    /// <summary>写「日志」：IDE 自己的运行记录。</summary>
    private void AppendLog(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        RunOut.AppendText(s + "\n");
        RunOut.ScrollToEnd();
        Persist(_logRunPath, s);
    }

    // ---------------------------------------------------------------- 项目日志落盘

    /// <summary>
    /// 每个项目一份日志目录，放在 AppData 下（不塞进项目里，免得在项目树上碍眼）。
    /// key 用「目录名 + 路径哈希」，不同位置的同名项目也不会串。
    /// </summary>
    private void SetupProjectLog(string root)
    {
        _logOutPath = ""; _logRunPath = ""; _openTabsPath = ""; _logProbPath = "";
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            string norm = Path.GetFullPath(root).TrimEnd('\\', '/').ToLowerInvariant();
            using var md5 = System.Security.Cryptography.MD5.Create();
            var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(norm));
            string key = Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
            string name = Path.GetFileName(norm);
            if (string.IsNullOrEmpty(name)) name = "project";
            foreach (var bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            string dir = Path.Combine(Core.AppDataDir, "logs", name + "-" + key);
            Directory.CreateDirectory(dir);
            _logOutPath = Path.Combine(dir, "output.log");
            _logRunPath = Path.Combine(dir, "app.log");
            _openTabsPath = Path.Combine(dir, "tabs.txt");
            _logProbPath = Path.Combine(dir, "problems.json");
        }
        catch { /* 落不了盘也不影响用 */ }
    }

    // 不带 BOM 的 UTF-8：带 BOM 的话新建文件会在第一行头插一个看不见的字符
    private static readonly System.Text.UTF8Encoding LogEnc = new(false);

    /// <summary>追加一行到日志文件。失败就当没这回事，不能让日志把主流程搞崩。</summary>
    private static void Persist(string path, string text)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(text)) return;
        try { File.AppendAllText(path, text + "\n", LogEnc); }
        catch { }
    }

    /// <summary>把上次留下的输出/日志读回面板——这是「日志永久保留」的关键一步。</summary>
    private void LoadProjectLog()
    {
        BuildOut.Clear();
        RunOut.Clear();
        string bo = ReadTail(_logOutPath, 400_000);
        if (bo.Length > 0) { BuildOut.Text = bo; BuildOut.ScrollToEnd(); }
        string rl = ReadTail(_logRunPath, 200_000);
        if (rl.Length > 0) { RunOut.Text = rl; RunOut.ScrollToEnd(); }

        // 「问题」也一起读回来。以前这里只有一句 Clear()，
        // 于是输出和日志都留着、唯独问题清空 —— 切个项布回去一看，报错全没了，
        // 明明「记录报错」才是这个面板的活。
        ShowProblems(LoadProblems());
    }

    /// <summary>
    /// 诊断清单落盘。存 JSON 而不是纯文本：读回来还要能点着跳行，
    /// 文件/行/列/级别都得留着，纯文本粘回去就定位不了了。
    /// </summary>
    private void SaveProblems(List<Core.Diagnostic> diags)
    {
        if (string.IsNullOrEmpty(_logProbPath)) return;
        try
        {
            // 只留能定位的，外加没定位信息的纯文本行（见 HarvestErrors）——
            // 后者没文件没行号，但它是报错原文，扔了就真丢了。
            var dump = diags.Select(d => new
            {
                lv = d.Level, f = d.File, ln = d.Line, c = d.Col, m = d.Message
            }).ToList();
            File.WriteAllText(_logProbPath,
                System.Text.Json.JsonSerializer.Serialize(dump), LogEnc);
        }
        catch { /* 落不了盘不影响用 */ }
    }

    /// <summary>读回上次存的诊断。文件不在或者格式坏了就当没有，不能让开项目失败。</summary>
    private List<Core.Diagnostic> LoadProblems()
    {
        var list = new List<Core.Diagnostic>();
        if (string.IsNullOrEmpty(_logProbPath) || !File.Exists(_logProbPath)) return list;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_logProbPath));
            foreach (var x in doc.RootElement.EnumerateArray())
            {
                list.Add(new Core.Diagnostic(
                    GetStr(x, "lv"), GetStr(x, "f"), GetInt(x, "ln"), GetInt(x, "c"), GetStr(x, "m")));
            }
        }
        catch { list.Clear(); }
        return list;
    }

    private static string GetStr(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int GetInt(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
            ? v.GetInt32() : 0;

    /// <summary>读文件末尾若干字符——日志可能很长，没必要全塞进文本框。</summary>
    private static string ReadTail(string path, int max)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
        try
        {
            var fi = new FileInfo(path);
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fi.Length > max) fs.Seek(fi.Length - max, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
            return sr.ReadToEnd().TrimEnd();
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- 项目

    public void OpenProject(string root)
    {
        SaveOpenTabs();   // 先把上一个项目还开着的文件记下来，再换项目
        var info = Core.OpenProject(root);
        if (!info.Ok) { SetStatus(Lang.T("st.openFail") + info.Error, false); return; }

        _root = info.Path;
        _projectName = info.Name;
        _tool = info.Tool;
        _mainClass = info.MainClass;
        _libJars = info.Jars;

        // 记下来：下次启动时主页上的「上次打开」就是这个
        // （首页清单里「新建或打开一个项目」那步也是读它，不用另外打标记）
        Core.SetSetting("lastProject", _root);
        HideHome();
        // Agent 面板跟随项目：打开项目才把它放出来，并接到这个项目上
        // （换项目会换会话文件，各自的聊天记录互不干扰）
        SetAgentAvailable(true);
        _agent?.Attach(_root);

        // 换项目就是换世界：上一个项目的输出不该留，但这个项目上次留下的要读回来
        CloseAllTabs();
        SetupProjectLog(_root);
        LoadProjectLog();
        RestoreOpenTabs();   // 把上次关这个项目时还开着的文件重新打开

        Title = $"{_projectName} — Java Studio";
        Tree.ItemsSource = ToItems(info.Tree);
        SetStatus(string.Format(Lang.T("st.projectOpened"), _projectName), true);
        StatusMid.Text = string.IsNullOrEmpty(_mainClass)
            ? "" : Lang.T("st.mainClass") + _mainClass;

        // 建项目时选的 JDK 版本写在了 pom/gradle 里，读回来当下拉的默认值
        SyncJdkForProject(_root);

        Core.Log("OK", string.Format(Lang.T("log.projectOpened"), _projectName, _tool));
        if (_libJars.Count > 0)
            Core.Log("OK", string.Format(Lang.T("log.libsOnCp"), _libJars.Count));
        if (info.Missing.Count > 0)
            Core.Log("WARN", string.Format(Lang.T("log.libsMissing"), string.Join("、", info.Missing)));

        Core.RecentAdd(root);
    }

    private static List<TreeItem> ToItems(List<Core.TreeNode> nodes) => nodes
        .Select(n => new TreeItem
        {
            Name = n.Name, Path = n.Path, IsDir = n.IsDir,
            Children = ToItems(n.Children)
        }).ToList();

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeItem { IsDir: false } it) OpenFile(it.Path);
    }

    // ------------------------------------------------- 右键：先定目标，再定菜单内容
    //
    // 两件事，顺序不能反：
    //
    // 1) 目标。TreeView 右键**不会**改变选中项，而这一排命令（新建 / 导入 / 重命名 /
    //    删除 / 复制路径）都是拿「当前目标」算的（见 TreeTargetDir）。不先把光标底下
    //    那一项选上，命令打的就是上一次选中的那项 ——「删除」会删错文件。
    //
    // 2) 内容。右键对象决定菜单里有什么：
    //      文件夹      → 新建 / 导入 / 重命名 / 删除 / 复制路径（全给）
    //      文件        → 只给 重命名 / 删除 / 复制路径（文件里建不了东西）
    //      树下面空白  → 新建 / 导入，目标就是项目根目录
    //    树里没有「根目录」那一项（js_dir_tree 给的直接是根下面的子项），
    //    所以空白处没法靠「选中某一个节点」表达，只能单独记一个目录。

    private bool _menuBlank;             // 这一轮是点在空白（= 项目根目录）
    private string _menuDir = "";        // 非空白时为空，交给 Tree.SelectedItem

    private void OnTreeRightDown(object sender, MouseButtonEventArgs e)
    {
        var item = SelfOrVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
        {
            _menuBlank = false;
            _menuDir = "";
            item.IsSelected = true;   // 跟左键单击同一条路径
            item.Focus();
            return;
        }

        // 树下面的空白处：目标是项目根目录。没开项目就没什么可操作的，菜单都不开。
        if (string.IsNullOrEmpty(_root)) { e.Handled = true; return; }
        _menuBlank = true;
        _menuDir = _root;
    }

    private void OnTreeMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) e.Handled = true;   // 没项目，菜单无从谈起
    }

    /// <summary>菜单弹出来之前按目标筛一遍条目：文件不给「新建 / 导入」。</summary>
    private void OnTreeMenuOpened(object sender, RoutedEventArgs e)
    {
        bool hasItem = !_menuBlank && Tree.SelectedItem is TreeItem;
        bool isFile = hasItem && ((TreeItem)Tree.SelectedItem).IsDir == false;
        bool canCreate = !isFile;

        SetVisible(canCreate, MiNewClass, MiNewJava, MiNewFolder, MiNewXml, MiNewCustom, SepCreate);
        SetVisible(canCreate, MiImportFile, MiImportDir);
        // 根目录这一档没有具体项，SepImport 留着会在菜单尾巴上拖一条多余的线
        SetVisible(hasItem, SepImport);
        SetVisible(hasItem, MiRename, MiDelete, SepEdit, MiCopyPath);
    }

    private void OnTreeMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuBlank = false;
        _menuDir = "";
    }

    private static void SetVisible(bool show, params UIElement[] els)
    {
        foreach (var el in els) el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>从某个可视元素往上找第一个 T（含自己）。找不到返回 null。</summary>
    private static T SelfOrVisualParent<T>(DependencyObject node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T hit) return hit;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // ---------------------------------------------------------------- 编辑器

    private IHighlightingDefinition _java;

    private IHighlightingDefinition JavaSyntax()
    {
        if (_java != null) return _java;
        using var s = typeof(MainWindow).Assembly
            .GetManifestResourceStream("JavaStudio.Syntax.Java.xshd");
        if (s != null)
        {
            using var r = new System.IO.StreamReader(s);
            string xshd = r.ReadToEnd();
            // 默认文字色跟着主题走：浅色用深字，深色用浅字。
            // 否则切主题后普通代码还是旧颜色（黑字在深色底上直接看不见）。
            var fg = (Color)FindResource("Fg.Primary");
            string hex = "#" + fg.R.ToString("X2") + fg.G.ToString("X2") + fg.B.ToString("X2");
            xshd = xshd.Replace("#{S_THEME}", hex);
            using var xr = new System.Xml.XmlTextReader(new System.IO.StringReader(xshd));
            _java = HighlightingLoader.Load(xr, HighlightingManager.Instance);
        }
        return _java;
    }

    // ---- 自动保存 ----
    // 每个文件一个节流定时器：停下笔 1.2 秒才写盘，避免一边敲一边反复存。
    private readonly Dictionary<string, System.Windows.Threading.DispatcherTimer> _autoSave = new();
    /// <summary>最近一次落盘的内容：用来判断「真的改过没」，也是「回到上次更改前」的依据。</summary>
    private readonly Dictionary<string, string> _lastSavedText = new();
    /// <summary>比 _lastSavedText 再早一版的已保存内容（回退的目标）。</summary>
    private readonly Dictionary<string, string> _prevSaved = new();
    private static readonly System.TimeSpan AutoSaveDelay = System.TimeSpan.FromMilliseconds(1200);

    private void OpenFile(string path)
    {
        if (_open.ContainsKey(path)) { Focus(_open[path]); return; }

        var res = Core.ReadFile(path);
        if (!res.Ok) { Core.Log("WARN", res.Error + "：" + path); return; }

        var ed = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            ShowLineNumbers = true,
            Background = Brushes.Transparent,
            LineNumbersForeground = (Brush)FindResource("Brush.Fg.Dim"),
            SyntaxHighlighting = JavaSyntax(),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        ed.SetResourceReference(TextEditor.ForegroundProperty, "Brush.Fg.Primary");
        ed.Document.Text = res.Text;
        ed.Document.FileName = path;
        AttachEditor(ed, path, res.Text);   // 右键菜单 / 行标注 / Tab 补全

        // 刚读进来的内容就是当前已保存版本；事件放在赋值之后挂，
        // 免得 ed.Document.Text = res.Text 把自己当成一次改动。
        _lastSavedText[path] = res.Text;
        ed.Document.TextChanged += (_, _) => OnDocChanged(path, ed);

        // md 不是代码：给它套一层「编辑 / 拆分 / 预览」的容器（右上角有切换按钮）。
        // 保存、自动保存那些逻辑照样认 TextEditor，所以 _open 里存的还是 ed，
        // 只有 TabItem.Content 换成容器 —— 找编辑器的地方要绕一层（见 CurrentEditor）。
        //
        // ⚠️ 先决定装什么、再一次性赋给 Content。反过来写（先 Content = ed，
        // 再造容器把 ed Add 进去）会抛「元素已经是另一个元素的逻辑子元素」：
        // ed 已经被 TabItem 收作逻辑子节点，不能再挂到第二个父级上。
        UIElement content = ed;
        if (IsMarkdown(path))
        {
            var pane = new MdPane(ed);
            _mdPanes[path] = pane;
            content = pane;
        }

        var tab = new TabItem { Header = TabHeader(path), Content = content, Tag = path };
        // 中键关标签，IDEA 也是这个手势
        tab.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { CloseTab(path); e.Handled = true; }
        };
        Editors.Items.Add(tab);
        _open[path] = ed;
        tab.IsSelected = true;
        TouchTab(path);
        TrimTabs(path);
    }

    /// <summary>md / markdown 走 MdPane（带预览），其它文件还是直接上编辑器。</summary>
    private static bool IsMarkdown(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".md" || ext == ".markdown";
    }

    private void Focus(TextEditor ed)
    {
        foreach (TabItem t in Editors.Items)
            // Content 可能是 MdPane（md），也可能是 TextEditor（其它）
            if (t.Content == ed || (t.Content is MdPane p && p.Editor == ed)) { t.IsSelected = true; break; }
        ed.Focus();
    }

    private TextEditor CurrentEditor()
    {
        if (Editors.SelectedItem is not TabItem t) return null;
        if (t.Content is TextEditor e) return e;
        return t.Content is MdPane p ? p.Editor : null;
    }

    // ---------------------------------------------------------------- 编辑器标签
    //
    // 照 IDEA 的编辑器标签来：
    //   - 顶部一条标签，按最近使用排序
    //   - 改动过的文件：标签文字变绿并加星号（IDEA 也是绿 + 星号）
    //   - 悬停才出现 ×，中键也能关
    //   - 开到第 11 个时关掉最久没动过的那个（IDEA 默认上限 10）
    //   - 关掉的文件进 _closed，Ctrl+E 的「最近文件」里还能找回来

    private const int MaxTabs = 10;

    private readonly List<string> _tabOrder = new();   // 最近使用在前
    private readonly Dictionary<string, TextBlock> _tabTitles = new();
    private readonly List<string> _recentFiles = new(); // 打开过的所有文件（含已关）
    private readonly List<string> _closed = new();      // 关掉的，供「最近文件」用

    private UIElement TabHeader(string path)
    {
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();

        var box = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icons.Visual(ext == ".jar" ? "box" : ext == ".xml" ? "grid" : "file-text",
                                13, (Brush)FindResource("Brush.Fg.Dim"));
        if (icon != null) { icon.Margin = new Thickness(0, 0, 6, 0); box.Children.Add(icon); }

        var tb = new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Muted");
        _tabTitles[path] = tb;
        box.Children.Add(tb);

        var x = new Button
        {
            Content = "×", FontSize = 13, Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(8, 0, 0, 0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0
        };
        x.SetResourceReference(Button.ForegroundProperty, "Brush.Fg.Dim");
        x.Click += (_, _) => CloseTab(path);
        box.Children.Add(x);

        // × 只在鼠标压着这条标签的时候露出来
        box.MouseEnter += (_, _) => x.Opacity = 1;
        box.MouseLeave += (_, _) => x.Opacity = 0;
        return box;
    }

    /// <summary>改过了就把标签刷绿加星号，存盘后恢复。</summary>
    public void MarkTabDirty(string path, bool dirty)
    {
        if (!_tabTitles.TryGetValue(path, out var tb)) return;
        string name = Path.GetFileName(path);
        tb.Text = dirty ? name + " *" : name;
        tb.SetResourceReference(TextBlock.ForegroundProperty,
                                dirty ? "Brush.Ok" : "Brush.Fg.Muted");
    }

    /// <summary>把这条挪到最近使用的最前面。</summary>
    private void TouchTab(string path)
    {
        _tabOrder.Remove(path);
        _tabOrder.Insert(0, path);
        if (!_recentFiles.Contains(path)) _recentFiles.Insert(0, path);
        else { _recentFiles.Remove(path); _recentFiles.Insert(0, path); }
        if (_closed.Remove(path)) { }
    }

    /// <summary>超过上限就关掉最久没用的那个（脏的也关，IDEA 也是这么干的）。</summary>
    private void TrimTabs(string keep)
    {
        while (_tabOrder.Count > MaxTabs)
        {
            string lru = _tabOrder[_tabOrder.Count - 1];
            if (lru == keep) break;
            CloseTab(lru);
        }
    }

    private void CloseTab(string path)
    {
        // 关之前先把没存的写上（自动保存的 1.2 秒还没走完就关了标签的情况）
        if (_open.TryGetValue(path, out var closing) &&
            _lastSavedText.TryGetValue(path, out var savedText) &&
            savedText != closing.Document.Text)
            SaveFile(path, closing, auto: true);
        if (_autoSave.TryGetValue(path, out var timer)) { timer.Stop(); _autoSave.Remove(path); }

        foreach (TabItem t in Editors.Items)
            if ((t.Tag as string) == path) { Editors.Items.Remove(t); break; }
        _open.Remove(path);
        _mdPanes.Remove(path);
        _states.Remove(path);
        _tabOrder.Remove(path);
        _tabTitles.Remove(path);
        _closed.Remove(path);
        _closed.Insert(0, path);
    }

    /// <summary>
    /// 自检用：直接打开一个文件（不限 .java），可选顺手切到某个 md 视图模式。
    /// JS_OPEN_FILE=&lt;路径&gt; [JS_MD_MODE=edit|split|preview] [JS_MD_SWAP=1]
    /// </summary>
    public void OpenFileForTest(string path, string mdMode)
    {
        OpenFile(path);
        if (string.IsNullOrEmpty(mdMode) || !_mdPanes.TryGetValue(path, out var pane)) return;
        pane.SetMode(mdMode switch
        {
            "edit" => MdViewMode.Edit,
            "preview" => MdViewMode.Preview,
            _ => MdViewMode.Split,
        });
        // JS_MD_SWAP=1 —— 自检里模拟点一下「交换左右」，看两栏是不是真的对调了
        if (Environment.GetEnvironmentVariable("JS_MD_SWAP") == "1") pane.Swap();
    }

    /// <summary>自检用：打开项目里前 n 个 .java，看标签条长什么样。</summary>
    public void OpenFirstJavaFiles(int count)
    {
        if (string.IsNullOrEmpty(_root)) return;
        var files = Directory.GetFiles(_root, "*.java", SearchOption.AllDirectories)
                             .OrderBy(f => f).Take(count);
        foreach (var f in files) OpenFile(f);
    }

    /// <summary>自检用：自动编译一次（JS_COMPILE=1）。</summary>
    public async void TriggerCompileForTest()
    {
        await Task.Delay(600);   // 等项目树先建好
        await CompileAsync();
    }

    /// <summary>
    /// 自检用：跑一遍「检查」并停在「问题」页（JS_CHECK=1）。
    /// 编译那个钩子停在「输出」页，拍不到诊断列表，所以另开一个。
    /// </summary>
    public async void TriggerCheckForTest()
    {
        await Task.Delay(600);
        OnCheck(null, null);
    }

    /// <summary>自检用：JS_BOTTOM=0|1|2 直接切到底部某一页（截图用，等价于点那三个按钮）。</summary>
    public void ShowBottomForTest(int index) => ShowBottom(index);

    /// <summary>自检用：JS_NEWPROJ="名称|包名"，打开新建项目对话框并填值。</summary>
    public void ShowNewProjectForTest(string spec)
    {
        var parts = spec.Split('|');
        var dlg = new NewProjectDialog { Owner = this };
        dlg.Prefill(parts.Length > 0 ? parts[0] : "",
                    parts.Length > 1 ? parts[1] : "");
        // ⚠️ 原来这里是 Show()。对话框改成内嵌页之后内容已经不在自己身上了，
        // Show() 只能弹一个空壳。ShowDialog() 才是把它挂进主窗口那一页的版本
        // （内部走 PushFrame，消息循环照跑，JS_SHOT 的延时截图照样到点）。
        dlg.ShowDialog();
    }

    /// <summary>
    /// 自检用：JS_NEWITEM="类型|名字"，不弹窗直接建一个条目，把结果写进
    /// JS_ITEM_OUT 指定的文件：成功是「路径 + 文件内容」，失败是「ERR:原因」。
    ///
    /// 验的就是这三件事：后缀补得对不对、自定义后缀没写会不会被拦、
    /// 生成的代码跟文件类型对不对得上。
    /// </summary>
    public void CreateItemForTest(string spec)
    {
        var parts = spec.Split('|');
        if (!int.TryParse(parts[0], out int kind)) return;

        var dlg = new NewItemDialog(TreeTargetDir()) { Owner = this };
        dlg.SetKind(kind, false);
        string r = dlg.CreateForTest(parts.Length > 1 ? parts[1] : "");

        string outPath = Environment.GetEnvironmentVariable("JS_ITEM_OUT");
        if (string.IsNullOrEmpty(outPath)) return;
        File.WriteAllText(outPath, r.StartsWith("ERR:")
            ? r
            : r + "\n----\n" + (File.Exists(r) ? File.ReadAllText(r) : "(没这个文件)"));
    }

    /// <summary>Ctrl+E：最近打开的文件（关掉的也算），回车打开。</summary>
    private void ShowRecentFiles()
    {
        var dlg = new RecentFilesDialog(_recentFiles.Concat(_closed).Distinct().ToList())
        { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Picked))
            if (File.Exists(dlg.Picked)) OpenFile(dlg.Picked);
    }

    // ---------------------------------------------------------------- 文件菜单

    private void OnNewProject(object s, RoutedEventArgs e)
    {
        var dlg = new NewProjectDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.CreatedDir))
            OpenCreatedProject(dlg.CreatedDir);
    }

    /// <summary>
    /// 新建完的项目从这里进去。除了打开，还要把「项目创建时选的 JDK」补上：
    /// 构建系统选了「无」的时候 pom / gradle 都没有，DetectProjectJdk 读不出来，
    /// 只能靠向导——它刚把版本号写进了 Config.PreferredJdk。
    /// </summary>
    private void OpenCreatedProject(string dir)
    {
        int made = TryMajor(Config.PreferredJdk, out int mv) ? mv : 0;
        OpenProject(dir);
        if (_projectJdk <= 0 && made > 0)
        {
            _projectJdk = made;
            FillJdkPicks();
            RefreshStatusRight();
        }
    }

    private void OnOpenProject(object s, RoutedEventArgs e)
    {
        // 用系统资源管理器选目录，别用自绘那套（见 Pickers.PickFolder 的注释）。
        string dir = Pickers.PickFolder(this, Lang.T("m.openProject"), Core.DefaultProjectsDir());
        if (!string.IsNullOrEmpty(dir)) OpenProject(dir);
    }

    private void OnNewFile(object s, RoutedEventArgs e)
        => NewItem(0);

    // ---------------------------------------------------------------- 项目树右键

    /// <summary>右键菜单里「新建」的目标目录：选中谁就在谁下面建。</summary>
    private string TreeTargetDir()
    {
        // 右键点在树空白处时目标是项目根目录。这种情况树里没有任何一项可选，
        // 所以 _menuDir 是唯一的来源（Tree.SelectedItem 留着的是上一次选的东西）。
        if (!string.IsNullOrEmpty(_menuDir)) return _menuDir;

        if (Tree.SelectedItem is TreeItem it)
            return it.IsDir ? it.Path : (Path.GetDirectoryName(it.Path) ?? _root);
        foreach (var d in new[] { "src/main/java", "src" })
        {
            var p = Path.Combine(_root, d);
            if (Directory.Exists(p)) return p;
        }
        return _root;
    }

    /// <param name="kind">类型下标。</param>
    /// <param name="lockKind">
    /// true = 进到弹窗后类型不能再改。右键菜单里「新建 XML 文件」这种已经点明了
    /// 类型的入口要锁住，否则弹窗里那个下拉还能改，用户会以为自己刚才点错了。
    /// 顶部菜单的「新建文件 Ctrl+N」是通用入口，不锁，让人随便挑。
    /// </param>
    private void NewItem(int kind, bool lockKind = false)
    {
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return; }
        var dlg = new NewItemDialog(TreeTargetDir()) { Owner = this };
        dlg.SetKind(kind, lockKind);
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.CreatedPath)) return;
        RefreshTree();
        if (File.Exists(dlg.CreatedPath)) OpenFile(dlg.CreatedPath);
        else SetStatus(Lang.T("st.created") + Path.GetFileName(dlg.CreatedPath), true);
    }

    private void OnNewJavaClass(object s, RoutedEventArgs e) => NewItem(0, true);
    private void OnNewJavaFile(object s, RoutedEventArgs e) => NewItem(1, true);
    private void OnNewFolder(object s, RoutedEventArgs e) => NewItem(2, true);
    private void OnNewXml(object s, RoutedEventArgs e) => NewItem(3, true);
    private void OnNewCustom(object s, RoutedEventArgs e) => NewItem(4, true);

    private void RefreshTree()
    {
        if (string.IsNullOrEmpty(_root)) return;
        Tree.ItemsSource = ToItems(Core.DirTree(_root));
    }

    private void OnRenameItem(object s, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is not TreeItem it) { SetStatus(Lang.T("st.selectItem"), false); return; }
        var dlg = new TextDialog(Lang.T("m.rename"), Path.GetFileName(it.Path)) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
        string dst = Path.Combine(Path.GetDirectoryName(it.Path) ?? _root, dlg.Value.Trim());
        try
        {
            if (it.IsDir) Directory.Move(it.Path, dst); else File.Move(it.Path, dst);
            RefreshTree();
            SetStatus(Lang.T("st.renameDone"), true);
        }
        catch (Exception ex) { SetStatus(Lang.T("st.renameFail") + ex.Message, false); }
    }

    private void OnDeleteItem(object s, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is not TreeItem it) { SetStatus(Lang.T("st.selectItem"), false); return; }
        if (!AppMsg.Ask(this, Lang.T("m.delete"),
                        string.Format(Lang.T("ask.recycle"), it.Name)))
            return;
        if (_states.ContainsKey(it.Path)) CloseTab(it.Path);
        if (it.IsDir)
            foreach (var k in _states.Keys.Where(k => k.StartsWith(it.Path)).ToList()) CloseTab(k);
        if (!Recycle.Send(it.Path, out string err))
        {
            AppMsg.Show(this, Lang.T("m.delete"), Lang.T("st.deleteFail") + err);
            return;
        }
        RefreshTree();
        SetStatus(Lang.T("st.deleteDone") + it.Name, true);
    }

    // 关标签统一走上面编辑器标签那节的 CloseTab（它还要维护 MRU 和已关列表）

    private void OnCopyPath(object s, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is not TreeItem it) return;
        try { Clipboard.SetText(it.Path); SetStatus(Lang.T("st.pathCopied"), true); } catch { }
    }

    /// <summary>删除整个项目（文件菜单）。同样送进回收站，并从最近列表划掉。</summary>
    private void OnDeleteProject(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return; }
        if (!AppMsg.Ask(this, Lang.T("m.deleteProject"),
                        string.Format(Lang.T("ask.recycleProject"), _projectName, _root))) return;

        foreach (var k in _states.Keys.ToList()) CloseTab(k);
        string root = _root;
        if (!Recycle.Send(root, out string err))
        {
            AppMsg.Show(this, Lang.T("m.deleteProject"), Lang.T("st.deleteFail") + err);
            return;
        }
        Core.RecentRemove(root);
        Core.SetSetting("lastProject", "");
        _root = ""; _projectName = ""; _mainClass = ""; _libJars = new();
        SyncJdkForProject("");     // 没项目了，下拉回到「跟随项目」
        Tree.ItemsSource = null;
        Title = Lang.T("app.name");
        ShowHome();
        SetStatus(Lang.T("st.deleteDone") + Path.GetFileName(root.TrimEnd('\\', '/')), true);
    }

    /// <summary>切换语言后把菜单、工具栏、主页、底部栏、树操作、Agent 面板全部重刷一遍。</summary>
    public void RefreshLanguage()
    {
        Lang.ApplyMenu(MainMenu);
        Lang.ApplyMenu(Tree.ContextMenu);
        // 「项目」这个标题写在 XAML 里，不在菜单里，ApplyMenu 管不到，
        // 得单独刷一次（XAML 里已经给它挂了 Tag="tree.title"）。
        Lang.Translate(TreeTitle);
        BuildToolbar();
        BuildBottomBar();
        BuildTreeActions();
        RefreshEditorMenus();      // 已开编辑器的右键菜单是开文件时建的，得重建
        RefreshDialogMenus();      // 新建/打开/设置那几个弹窗的菜单也是启动时建的一次性的
        _agent?.RefreshLanguage();
        if (ApiGate.Visibility == Visibility.Visible) BuildApiGate();
        if (HomeView.Visibility == Visibility.Visible) ShowHome();
        SetStatus(Lang.T("st.ready"), true);
    }

    /// <summary>切语言后把每个已打开编辑器的右键菜单换一遍。</summary>
    private void RefreshEditorMenus()
    {
        foreach (var kv in _states)
            kv.Value.Ed.ContextMenu = BuildEditorMenu(kv.Value.Ed);
    }

    /// <summary>
    /// 切语言后把主窗口里那几个「常驻弹窗」重刷一遍。
    ///
    /// 新建项目 / 打开项目 / 设置这三块是从主页三个方块点开的，用的时候才 new，
    /// 构造那一刻就把 Lang.T() 的文案锁死了 -- 只刷菜单和主页的话，弹出来还是旧语言。
    ///
    /// 之前这里写的是几个不存在的控件名（NewProjectView 之类），编译不过，
    /// 而且思路也错了：真正的做法不是在主窗口里找那些弹窗（它们还没被 new 出来），
    /// 而是让弹窗自己支持「随时重新翻译」。Lang.Translate 就是那个能力，
    /// 每个弹窗构造完各给自己打上 Tag，弹出前 / 语言变时调一次就行。
    /// </summary>
    private void RefreshDialogMenus()
    {
        // 弹窗是按需 new 的，这里没有常驻实例可刷。
        // 保留这个方法名是因为 RefreshLanguage 的调用序列里它得在，
        // 真正干活的是各处弹窗里的 Lang.Translate(this)。
    }

    private void OnSave(object s, RoutedEventArgs e)
    {
        var ed = CurrentEditor();
        if (ed == null) return;
        string p = ed.Document.FileName;
        if (string.IsNullOrEmpty(p)) return;
        SaveFile(p, ed, auto: false);
    }

    private void OnSaveAll(object s, RoutedEventArgs e) => SaveAllDirty(auto: false);

    // ---------------------------------------------------------------- 保存
    //
    // 以前必须手动 Ctrl+S：编辑器压根没挂文本变更事件，MarkTabDirty 也没人调用，
    // 于是 OnSaveAll 里那个 "dirty" 判断永远不成立，等于一次都没自动存过。
    // 现在：改动 → 标脏 → 停笔 1.2 秒自动落盘；关标签、关窗口前再各补存一遍。

    /// <summary>内容变了：标签加星号，并（重新）开始自动保存倒计时。</summary>
    private void OnDocChanged(string path, TextEditor ed)
    {
        ed.Tag = "dirty";
        MarkTabDirty(path, true);
        if (!_autoSave.TryGetValue(path, out var t))
        {
            t = new System.Windows.Threading.DispatcherTimer { Interval = AutoSaveDelay };
            t.Tick += (_, _) => { t.Stop(); SaveFile(path, ed, auto: true); };
            _autoSave[path] = t;
        }
        t.Stop();     // 重新计时：停下笔才写盘，别一边敲一边存
        t.Start();
    }

    /// <summary>
    /// 写盘。保存前先把上一版已保存内容挪进 _prevSaved，
    /// 「回到上次更改前」退的就是那一版。
    /// </summary>
    private bool SaveFile(string path, TextEditor ed, bool auto)
    {
        string text = ed.Document.Text;
        if (_lastSavedText.TryGetValue(path, out var saved) && saved != text)
            _prevSaved[path] = saved;

        if (!Core.WriteFile(path, text))
        {
            Core.Log("ERROR", Lang.T("log.saveFail") + path);
            return false;
        }
        _lastSavedText[path] = text;
        MarkSaved(path, text);          // 清星号 + 刷新行标注
        // 已经存过了，排队中的自动保存取消，别把同一份内容再写一遍
        if (_autoSave.TryGetValue(path, out var t)) t.Stop();
        Core.Log("OK", string.Format(auto ? Lang.T("log.autoSaved") : Lang.T("log.savedFile"),
                                    Path.GetFileName(path)));
        if (!auto) SetStatus(Lang.T("st.saved"), true);
        return true;
    }

    /// <summary>把改过但还没落盘的文件都存一遍（按内容比，不靠 Tag，漏标脏也存得下来）。</summary>
    private void SaveAllDirty(bool auto)
    {
        foreach (var kv in _open)
        {
            string text = kv.Value.Document.Text;
            if (_lastSavedText.TryGetValue(kv.Key, out var saved) && saved == text) continue;
            SaveFile(kv.Key, kv.Value, auto);
        }
    }

    /// <summary>关窗口前把没存的都写上（退出 / Alt+F4 / 点 × 都走这里）。</summary>
    private void OnWindowClosing(object s, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var t in _autoSave.Values) t.Stop();
        SaveAllDirty(auto: true);
        SaveOpenTabs();   // 退出时也记一下，免得最后这次会话的打开文件丢了
    }

    /// <summary>「回到上次更改前」：把文件退回上一次保存的那一版。</summary>
    private void OnRevert(object s, RoutedEventArgs e)
    {
        var ed = CurrentEditor();
        if (ed == null) return;
        string p = ed.Document.FileName;
        if (string.IsNullOrEmpty(p)) return;

        if (!_prevSaved.TryGetValue(p, out var prev))
        {
            SetStatus(Lang.T("st.noEarlier"), false);
            return;
        }
        if (_autoSave.TryGetValue(p, out var t)) t.Stop();
        ed.Document.Text = prev;
        SaveFile(p, ed, auto: false);   // 退回的内容当场落盘
        SetStatus(Lang.T("st.reverted"), true);
        Core.Log("OK", Lang.T("log.reverted") + Path.GetFileName(p));
    }

    /// <summary>关掉所有标签页。切项目/关项目都走这里。</summary>
    private void CloseAllTabs()
    {
        foreach (var t in _autoSave.Values) t.Stop();
        _autoSave.Clear();
        _lastSavedText.Clear();
        _prevSaved.Clear();
        Editors.Items.Clear();
        _open.Clear();
        _states.Clear();
        _tabOrder.Clear();
        _tabTitles.Clear();
    }

    /// <summary>把当前还开着的文件（按最近使用顺序）写进项目数据目录，下次开这个项目时恢复。</summary>
    private void SaveOpenTabs()
    {
        if (string.IsNullOrEmpty(_openTabsPath)) return;   // 没项目（在主页）就不写
        try
        {
            // _tabOrder 是「最近使用在前」，直接按这个顺序存，恢复时顺序也对
            var open = _tabOrder.Where(p => _open.ContainsKey(p));
            File.WriteAllLines(_openTabsPath, open, LogEnc);
        }
        catch { /* 写不下去也不影响用 */ }
    }

    /// <summary>重新打开上次关项目时还开着的文件（文件没了就跳过，超过标签页上限就截断）。</summary>
    private void RestoreOpenTabs()
    {
        if (string.IsNullOrEmpty(_openTabsPath) || !File.Exists(_openTabsPath)) return;
        try
        {
            var lines = File.ReadAllLines(_openTabsPath)
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0 && File.Exists(l))
                            .Take(MaxTabs)
                            .ToList();
            // 逆序打开：存的是「最近使用在前」，逆序后最近使用的最后开，正好成为当前选中页
            foreach (var p in ((IEnumerable<string>)lines).Reverse())
                if (!_open.ContainsKey(p)) OpenFile(p);
        }
        catch { }
    }

    private void OnCloseProject(object s, RoutedEventArgs e)
    {
        SaveAllDirty(auto: true);   // 先把改动落盘，免得下次恢复时少最后一笔
        SaveOpenTabs();             // 记下这次还开着的文件
        _root = ""; _projectName = ""; _mainClass = ""; _libJars = new();
        CloseAllTabs();
        _logOutPath = ""; _logRunPath = ""; _logProbPath = "";   // 日志文件留着，下次打开这个项目再读回来
        BuildOut.Clear();
        RunOut.Clear();
        Problems.Items.Clear();
        Tree.ItemsSource = null;
        Title = "Java Studio";
        Core.SetSetting("lastProject", "");
        ShowHome();
    }

    private void OnExit(object s, RoutedEventArgs e) => Close();

    private void OnEdit(object s, RoutedEventArgs e)
    {
        var ed = CurrentEditor();
        if (ed == null) return;
        // Tag 现在是语言表的 key（m.undo …），动作名是去掉 m. 前缀的那截
        string a = (s as MenuItem)?.Tag as string ?? "";
        if (a.StartsWith("m.")) a = a.Substring(2);
        switch (a)
        {
            case "undo": ed.Undo(); break;
            case "redo": ed.Redo(); break;
            case "cut": ed.Cut(); break;
            case "copy": ed.Copy(); break;
            case "paste": ed.Paste(); break;
            case "selectAll": ed.SelectAll(); break;
        }
    }

    // ---------------------------------------------------------------- 视图

    private void OnHome(object s, RoutedEventArgs e) => ShowHome();

    private void OnWorkspace(object s, RoutedEventArgs e)
    {
        HideHome();
        Editors.Focus();
    }

    private void OnToggleTree(object s, RoutedEventArgs e)
    {
        bool show = TreeCol.Width.Value == 0;
        if (!show && TreeCol.Width.Value > 0) _treeW = TreeCol.Width.Value;
        TreeCol.Width = new GridLength(show ? _treeW : 0);
    }

    /// <summary>隐藏/展开底部工具窗口。收起时连分割条一起收，不然会留一条 4px 的缝。</summary>
    private void OnToggleBottom(object s, RoutedEventArgs e)
    {
        bool show = BottomRow.Height.Value == 0;
        if (!show && BottomRow.Height.Value > 0) _bottomH = BottomRow.Height.Value;
        BottomRow.Height = new GridLength(show ? _bottomH : 0);
        BottomSplitRow.Height = new GridLength(show ? 4 : 0);
        BottomBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleTheme(object s, RoutedEventArgs e)
    {
        Theme.Toggle();
        // 工具栏图标是代码里建的（不是 XAML），日/月图标也要跟着换，重建一次最省事
        BuildToolbar();
        ApplyThemeToEditors();
        // 主页是自己搭出来的控件树，颜色用的是 SetResourceReference，
        // 换主题会跟着变；但布局是在 ShowHome 里定的，重建一次最省事。
        if (HomeView.Visibility == Visibility.Visible) ShowHome();
    }

    /// <summary>主题换完，把已打开编辑器的高亮/行号色按新主题重建。</summary>
    public void ApplyThemeToEditors()
    {
        _java = null;   // 让 JavaSyntax() 重新按新主题生成高亮（普通代码默认色）
        var lineNo = (Brush)FindResource("Brush.Fg.Dim");
        foreach (var ed in _open.Values)
        {
            ed.SyntaxHighlighting = JavaSyntax();
            ed.LineNumbersForeground = lineNo;   // 行号色也跟着换，不然深色下还是旧的灰
        }
        // md 预览里的 HTML 是把颜色**写死**在 <style> 里的（Trident 不吃 DynamicResource），
        // 所以切主题必须重新渲染一遍，否则会留着一个浅色底挂在深色界面上。
        foreach (var pane in _mdPanes.Values) pane.ReTheme();
    }

    private void OnSettings(object s, RoutedEventArgs e)
    {
        new SettingsDialog { Owner = this }.ShowDialog();
        RefreshStatusRight();
    }

    private void OnRefreshTree(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return; }
        Tree.ItemsSource = ToItems(Core.DirTree(_root));
        SetStatus(Lang.T("st.treeRefreshed"), true);
    }


    private void OnAbout(object s, RoutedEventArgs e)
        => new AboutDialog { Owner = this }.ShowDialog();

    private void OnGotoMain(object s, RoutedEventArgs e) => OpenMainSource();

    /// <summary>打开主类源文件（导航 → 主类）。</summary>
    public void OpenMainSource()
    {
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return; }
        var cls = string.IsNullOrEmpty(_mainClass) ? Core.GuessMain(_root) : _mainClass;
        if (string.IsNullOrEmpty(cls)) { SetStatus(Lang.T("st.noMain"), false); return; }
        // com.example.idea1.Main → src/main/java/com/example/idea1/Main.java
        foreach (var dir in new[] { "src/main/java", "src", "" })
        {
            var p = Path.Combine(_root, dir) +
                    Path.DirectorySeparatorChar +
                    cls.Replace('.', Path.DirectorySeparatorChar) + ".java";
            if (File.Exists(p)) { OpenFile(p); return; }
        }
        SetStatus(Lang.T("st.noSrc").Replace("{0}", cls), false);
    }

    /// <summary>编辑器标签切换时更新底部面包屑（IDEA 也把路径放在编辑区下沿）。</summary>
    private void OnEditorTabChanged(object s, SelectionChangedEventArgs e)
    {
        var path = Editors.SelectedItem is TabItem t ? t.Tag as string : null;
        if (string.IsNullOrEmpty(path)) { Crumb.Visibility = Visibility.Collapsed; return; }
        TouchTab(path);   // 点过的排到最前面，超出上限时最后被关掉
        Crumb.Visibility = Visibility.Visible;
        var rel = !string.IsNullOrEmpty(_root) &&
                  path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(_root.Length).TrimStart('\\', '/')
            : path;
        CrumbText.Text = (_projectName.Length > 0 ? _projectName + "  ›  " : "") +
                         rel.Replace('\\', ' ').Replace("/", "  ›  ");
    }

    // ---------------------------------------------------------------- 主页
    //
    // 主页一共四个版块：新建项目 / 项目列表 / 打开项目 / 设置。
    // 上面一排是四个圆角正方形卡片，点哪个，就弹哪个对应的对话框。
    // 主题、语言、外部 Java 全都塞在「设置」这块里，不再到处开对话框。

    private int _homeTab = 1;        // 默认停在项目列表
    private double _treeW = 260;     // 项目树收起前的宽度

    private void HideHome()
    {
        HomeView.Visibility = Visibility.Collapsed;
        Chrome(true);
    }

    /// <summary>
    /// 主页是一张干净的欢迎页：菜单栏和工具栏都不显示（IDEA 的欢迎界面
    /// 也没有这两样）。工作区本身被主页整块盖住，所以不用去动树宽、
    /// 面板高度这些尺寸，让它自己留着就行。
    /// </summary>
    private void Chrome(bool workbench)
    {
        var v = workbench ? Visibility.Visible : Visibility.Collapsed;
        MainMenu.Visibility = v;
        Toolbar.Visibility = v;
    }

    /// <summary>状态栏右侧那一串：JDK / 编码 / 行尾 / 缩进，照 IDEA 的排。</summary>
    private void RefreshStatusRight()
    {
        var jdk = Core.Jdks().FirstOrDefault(j => j.Ok);
        StatusRight.Text = jdk == null
            ? Lang.T("st.editorInfo")
            : $"{jdk.Label}  ·  UTF-8  ·  LF  ·  4";
    }

    private void ShowHome()
    {
        SetAgentAvailable(false);   // Agent 跟随项目，回主页（没项目）就整列收掉
        HomePanel.Children.Clear();

        var page = new StackPanel
        {
            MaxWidth = 880,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        HomePanel.Children.Add(page);

        // ---- 头部：图标 + Java Studio + 灰色副标题
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 26)
        };
        var logo = new Border
        { Width = 54, Height = 54, CornerRadius = new CornerRadius(15) };
        logo.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Panel");
        logo.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        logo.BorderThickness = new Thickness(1);
        var mark = Icons.Visual("coffee", 28, (Brush)FindResource("Brush.Accent"));
        if (mark != null)
        {
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            mark.VerticalAlignment = VerticalAlignment.Center;
            logo.Child = mark;
        }
        head.Children.Add(logo);

        var titles = new StackPanel
        { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        titles.Children.Add(HText(Lang.T("app.name"), 27, "Brush.Fg.Primary", true));
        titles.Children.Add(HText(Lang.T("app.slogan"), 13, "Brush.Fg.Dim", false,
                                  new Thickness(0, 3, 0, 0)));
        head.Children.Add(titles);
        page.Children.Add(head);

        // ---- 三个入口卡片（项目列表在下面直接列出，不用再点进一个框）
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 18)
        };
        row.Children.Add(HomeCard("plus", "home.new", "home.new.hint", 0));
        row.Children.Add(HomeCard("folder-open", "home.open", "home.open.hint", 2));
        row.Children.Add(HomeCard("settings", "home.settings", "home.settings.hint", 3));
        page.Children.Add(row);

        // ---- 新手清单：挂在入口卡片下面，跟着真实完成状态打勾
        // 收起/展开要重建整页才能生效，委托指回 ShowHome —— 别绕别的路，
        // 首页其他内容本来也会被语言切换之类的重建，走同一条最省心。
        GuideChecklist.RefreshNeeded = ShowHome;
        page.Children.Add(GuideChecklist.Build(RunGuide));

        // ---- 项目列表
        //
        // ⚠️ 别再把「上次打开」单独列一块、然后在下面那块把它**排除**掉
        // （`.Where(p => !SamePath(p, last))`）。只有一个项目的用户，看到的
        // 就是上面挂着一行项目、紧接着一句「还没有项目。点上面的『新建项目』
        // 建一个。」—— 同一屏上既说有又说没有，这个报过两次了。
        //
        // 现在合并成一个列表，顺序 =
        //   当前打开的 → 上次打开的 → 最近打开过的 → item 目录里躺着的。
        // 按完整路径去重、只列磁盘上还在的目录；只要列表非空，就不会出现空态。
        var picks = new List<(string Dir, bool Continue)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Pick(string p, bool cont)
        {
            if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) return;
            string key;
            try { key = Path.GetFullPath(p).TrimEnd('\\', '/'); }
            catch { return; }
            if (!seen.Add(key)) return;
            picks.Add((key, cont));
        }

        Pick(_root, true);                          // 正开着的排最前
        Pick(Core.Setting("lastProject"), true);    // 上次打开的（跟上面同一条时自然去重）
        foreach (var p in Core.Recent()) Pick(p, false);
        // item 目录里躺着的也该在列表里 —— 它本来就是「项目放哪儿」的默认位置，
        // 老项目没打开过、或者从别处拷进来的，不该因为 session.txt 里没记过就消失。
        foreach (var p in ProjectsUnderDefaultDir()) Pick(p, false);
        // ⚠️ 上限别再写死 8：光「打开过的」那一段最多也就 8 个，
        // 现在 item 里的一起进来，8 个很容易就把它们截没了。
        if (picks.Count > 20) picks.RemoveRange(20, picks.Count - 20);

        page.Children.Add(HText(Lang.T("home.recent"), 12, "Brush.Fg.Muted", true,
                                new Thickness(0, 12, 0, 8)));
        if (picks.Count == 0)
            page.Children.Add(HText(Lang.T("home.recent.empty"), 12, "Brush.Fg.Dim"));
        else
            foreach (var (dir, cont) in picks) page.Children.Add(ProjectRow(dir, cont));

        // 主页上没有项目时，状态栏也不能再挂着上一个项目 ——
        // 不然页面写着「还没有项目」、底下写着「项目已打开：xxx」，同一屏两个说法。
        if (string.IsNullOrEmpty(_root))
        {
            SetStatus(Lang.T("st.ready"), true);
            StatusMid.Text = "";
        }

        Chrome(false);
        HomeView.Visibility = Visibility.Visible;
        HomeView.ScrollToTop();
        DumpTextsForTest(HomePanel, "home");
    }

    /// <summary>
    /// 默认项目目录（&lt;安装目录&gt;\item）下面的一层子目录，按名字排好。
    ///
    /// 为什么要有这一份：项目列表原来只认 session.txt —— 那是「打开过的项目」。
    /// 于是 item 里明明躺着好几个项目，只要这次没打开过，首页就当它们不存在。
    /// 而 item 本来就是「项目放哪儿」的答案，里面的东西天然就是项目。
    ///
    /// 只翻一层，不递归：项目目录不会互相嵌套，递归只会把 src / target 这类
    /// 子目录也捞上来，列表里冒出一堆「项目」其实都是同一个项目的零件。
    ///
    /// ⚠️ 整个方法不许抛：首页要画的时候任何一个异常都会让整页出不来，
    /// 而「扫一个目录」这件事失败了大不了就是不显示，没到要崩的程度。
    /// </summary>
    private static List<string> ProjectsUnderDefaultDir()
    {
        var list = new List<string>();
        try
        {
            string dir = Core.DefaultProjectsDir();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;

            foreach (var d in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(d);
                // 以点开头的（.git、.javastudio 这类）不是项目，别列
                if (string.IsNullOrEmpty(name) || name[0] == '.') continue;
                list.Add(d);
            }
        }
        catch { return list; }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ---------------------------------------------------------------- 版块内容

    /// <summary>
    /// 自检用：JS_TEXT_DUMP=路径 时把这一层的可见文字按出现顺序抄进文件。
    ///
    /// 为什么不能只截图：「首页写着『还没有项目』，可上面明明列着一条项目」这种
    /// 前后矛盾，截图得靠人眼看，自检里也没法断言。把 TextBlock / Button 的文字
    /// 全 dump 出来，脚本里直接查「那句空态文案出现了没有」就够了 ——
    /// 静态文案这类问题用文本断言比看像素可靠得多。
    /// </summary>
    internal static void DumpTextsForTest(DependencyObject root, string tag)
    {
        string path = Environment.GetEnvironmentVariable("JS_TEXT_DUMP");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== " + tag);
            CollectTexts(root, sb, 0);
            File.AppendAllText(path, sb.ToString());
        }
        catch { /* 自检写不出来不该影响界面 */ }
    }

    private static void CollectTexts(DependencyObject node, StringBuilder sb, int depth)
    {
        string pad = new string(' ', Math.Min(depth, 14));
        if (node is TextBlock tb && tb.Visibility == Visibility.Visible &&
            !string.IsNullOrWhiteSpace(tb.Text))
        {
            sb.AppendLine(pad + tb.Text);
        }
        else if (node is Button b && b.Visibility == Visibility.Visible &&
                 b.Content is string s && s.Length > 0)
        {
            sb.AppendLine(pad + "[按钮] " + s);
            return;   // 按钮的内容会被模板再渲染成一个 TextBlock，别再抄一遍
        }

        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            CollectTexts(VisualTreeHelper.GetChild(node, i), sb, depth + 1);
    }

    private static TextBlock HText(string text, double size, string brushKey,
                                   bool bold = false, Thickness margin = default)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = size, Margin = margin,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return tb;
    }

    private static TextBlock HLabel(string text)
        => HText(text, 12, "Brush.Fg.Muted", true, new Thickness(0, 0, 0, 5));

    private static StackPanel HRow(UIElement left, UIElement right)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        p.Children.Add(left);
        if (right != null) p.Children.Add(right);
        return p;
    }

    private Button HBtn(string text, Action click, bool primary)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "PrimaryBtn" : "GhostBtn"),
            Cursor = Cursors.Hand
        };
        b.Click += (_, _) => click();
        return b;
    }

    private Border HomeCard(string icon, string titleKey, string hintKey, int index)
    {
        bool on = _homeTab == index;
        var b = new Border
        {
            Width = 158, Height = 158, CornerRadius = new CornerRadius(16),
            Margin = new Thickness(6), Padding = new Thickness(12), Cursor = Cursors.Hand
        };
        b.SetResourceReference(Border.BackgroundProperty, on ? "Brush.Bg.Active" : "Brush.Bg.Panel");
        b.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.Border");
        b.BorderThickness = new Thickness(on ? 1.5 : 1);

        var st = new StackPanel
        { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var vis = Icons.Visual(icon, 30, on ? "Brush.Accent" : "Brush.Fg.Muted");
        if (vis != null)
        {
            vis.HorizontalAlignment = HorizontalAlignment.Center;
            st.Children.Add(vis);
        }
        var t = HText(Lang.T(titleKey), 14, on ? "Brush.Fg.Primary" : "Brush.Fg.Muted", true,
                      new Thickness(0, 12, 0, 4));
        t.HorizontalAlignment = HorizontalAlignment.Center;
        st.Children.Add(t);
        var hint = HText(Lang.T(hintKey), 11, "Brush.Fg.Dim");
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.TextAlignment = TextAlignment.Center;
        st.Children.Add(hint);
        b.Child = st;

        b.MouseLeftButtonUp += (_, _) => { _homeTab = index; OpenHomeDialog(index); };
        return b;
    }

    /// <summary>主页上每个方块各自开一个框，不再把内容堆在主页里。</summary>
    private void OpenHomeDialog(int index)
    {
        switch (index)
        {
            case 0:     // 新建项目
            {
                var dlg = new NewProjectDialog { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.CreatedDir))
                    OpenCreatedProject(dlg.CreatedDir);
                break;
            }
            case 2:     // 打开项目
                OnOpenProject(this, new RoutedEventArgs());
                break;
            case 3:     // 设置
                OnSettings(this, new RoutedEventArgs());
                break;
        }
    }

    private Border ProjectRow(string dir, bool isLast)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = Icons.Visual(isLast ? "clock" : "folder", 17,
                                 (Brush)FindResource("Brush.Fg.Dim"));
        if (glyph != null)
        {
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.Margin = new Thickness(0, 0, 11, 0);
            Grid.SetColumn(glyph, 0);
            row.Children.Add(glyph);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(HText(Path.GetFileName(dir.TrimEnd('\\', '/')), 14,
                                "Brush.Fg.Primary", true));
        text.Children.Add(HText(dir, 11, "Brush.Fg.Dim", false, new Thickness(0, 3, 0, 0)));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var right = new StackPanel
        { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // 「上次打开」那行用一个实心播放三角（play）当「继续」标识，比文字更像 IDEA
        if (isLast)
        {
            var play = Icons.Visual("play", 15, (Brush)FindResource("Brush.Accent"));
            if (play != null) { play.Margin = new Thickness(0, 0, 9, 0); right.Children.Add(play); }
        }
        // 删除按钮：之前删项目失败一半是因为根本没这个按钮
        var del = new Button
        {
            Content = Icons.Visual("trash-2", 14, (Brush)FindResource("Brush.Fg.Dim"))
                      ?? (object)Lang.T("m.delete"),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, Padding = new Thickness(4, 2, 4, 2),
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center
        };
        del.Click += (_, _) => DeleteProject(dir);
        right.Children.Add(del);
        var arrow = Icons.Visual("arrow-right", 15, (Brush)FindResource("Brush.Fg.Dim"));
        if (arrow != null) right.Children.Add(arrow);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);

        var b = new Border
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(14, 11, 14, 11),
            Cursor = Cursors.Hand, Child = row
        };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Base");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        b.BorderThickness = new Thickness(1);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Hover");
        b.MouseLeave += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Base");
        b.MouseLeftButtonUp += (_, _) => OpenProject(dir);
        return b;
    }

    /// <summary>主页上删项目：确认 → 回收站 → 从最近列表划掉 → 刷新主页。</summary>
    private void DeleteProject(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (!AppMsg.Ask(this, Lang.T("m.deleteProject"),
                        string.Format(Lang.T("ask.recycleProject"), name, dir)))
            return;
        if (!Recycle.Send(dir, out string err))
        {
            AppMsg.Show(this, Lang.T("m.deleteProject"), Lang.T("st.deleteFail") + err);
            return;
        }
        Core.RecentRemove(dir);
        if (SamePath(Core.Setting("lastProject") ?? "", dir))
            Core.SetSetting("lastProject", "");
        ShowHome();
        SetStatus(Lang.T("st.deleteDone") + name, true);
    }

    // ---------------------------------------------------------------- 主页动作

    private static string BrowseFolder(string start)
    {
        var d = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Lang.T("pk.pickDir"),
            SelectedPath = Directory.Exists(start) ? start : Core.DefaultProjectsDir()
        };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : "";
    }

    private void SetLang(string lang)
    {
        Lang.Set(lang);
        Lang.ApplyMenu(MainMenu);
        Lang.ApplyMenu(Tree.ContextMenu);
        BuildToolbar();
        ShowHome();
        SetStatus(Lang.T("st.ready"), true);
    }

    private static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                                 Path.GetFullPath(b).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 运行环境
    //
    // Python 有 venv，Java 这边也照着做一个：项目下 .javastudio\venv
    // 里放一份 java.home（指向 JDK）、两个 bat 和一个装依赖 jar 的 lib 目录。
    // 好处是换 JDK 只改一个文件，运行脚本自己就能跑起来。

    public static string VenvDir(string root) => Path.Combine(root, ".javastudio", "venv");

    public static void CreateVenv(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        { Core.Log("WARN", Lang.T("log.noProject")); return; }

        var jdk = Core.CurrentJdk();
        string home = Core.Setting("extJava");
        if (string.IsNullOrEmpty(home)) home = jdk.Home;

        var dir = VenvDir(root);
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        File.WriteAllText(Path.Combine(dir, "java.home"), home);
        File.WriteAllText(Path.Combine(dir, "env.json"),
            "{\n  \"javaHome\":" + System.Text.Json.JsonSerializer.Serialize(home) +
            ",\n  \"created\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\"\n}\n");

        File.WriteAllText(Path.Combine(dir, "build.bat"),
            "@echo off\r\nset JAVA_HOME=" + home + "\r\n" +
            "if not exist out mkdir out\r\n" +
            "\"%JAVA_HOME%\\bin\\javac\" -encoding UTF-8 -d out -cp \"out;lib\\*\" " +
            "src\\main\\java\\**\\*.java 2>nul\r\n" +
            "if errorlevel 1 \"%JAVA_HOME%\\bin\\javac\" -encoding UTF-8 -d out -cp \"out;lib\\*\" " +
            "src\\*.java\r\n");
        File.WriteAllText(Path.Combine(dir, "run.bat"),
            "@echo off\r\nset JAVA_HOME=" + home + "\r\n" +
            "set MAIN=%1\r\nif \"%MAIN%\"==\"\" set MAIN=" + (Core.GuessMain(root) ?? "") + "\r\n" +
            "\"%JAVA_HOME%\\bin\\java\" -Dfile.encoding=UTF-8 -cp \"out;lib\\*\" %MAIN%\r\n");

        Core.Log("OK", Lang.T("home.venv.done") + "：" + dir);
    }

    private void OnCreateVenv(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return; }
        CreateVenv(_root);
        SetStatus(Lang.T("home.venv.done"), true);
    }

    private async void OnVenvRun(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) { SetStatus(Lang.T("st.noProject"), false); return; }
        var dir = VenvDir(_root);
        if (!File.Exists(Path.Combine(dir, "run.bat"))) CreateVenv(_root);

        ShowBottom(1);
        SetStatus(Lang.T("st.running"), true);
        var bat = Path.Combine(dir, "run.bat");
        string main = string.IsNullOrEmpty(_mainClass) ? Core.GuessMain(_root) : _mainClass;

        var outp = await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(bat)
                {
                    WorkingDirectory = _root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(main ?? "");
                using var p = System.Diagnostics.Process.Start(psi);
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, o);
            }
            catch (Exception ex) { return (-1, ex.Message); }
        });

        AppendBuild(outp.Item2.TrimEnd());
        SetStatus(outp.Item1 == 0 ? Lang.T("st.runDone") : "exit " + outp.Item1,
                  outp.Item1 == 0);
    }


    // ---------------------------------------------------------------- 构建

    private string Classpath() => string.Join(";", _libJars);

    /// <summary>
    /// 编译 / 运行用哪套 JDK。优先级：
    ///   1) 工具栏下拉里挑的那一项
    ///   2) 没挑 → 建项目时选的版本（pom.xml / build.gradle 里写的 release）
    ///   3) 项目也没说 → 设置里指定的外部 Java
    ///   4) 还是没有 → 返回空，让内核自己挑一套现成的
    /// </summary>
    private string JdkHome()
    {
        // 1) 下拉里挑了明确的某套（也可能是「跟随项目」那一行：跟着项目版本走）
        if (_jdkBox != null && _jdkBox.SelectedIndex > 0 &&
            _jdkBox.SelectedIndex < _jdkPicks.Count)
        {
            var p = _jdkPicks[_jdkBox.SelectedIndex];
            if (!string.IsNullOrEmpty(p.Home)) return p.Home;
            string byMajor = JdkHomeByMajor(p.Major);
            if (byMajor.Length > 0) return byMajor;
        }

        // 2) 没挑（或挑的这套装没了）：用建项目时选的版本
        string byProject = JdkHomeByMajor(_projectJdk);
        if (byProject.Length > 0) return byProject;

        // 3) 设置里指定的外部 Java；没指定就返回空给内核自己挑
        return Core.Setting("extJava");
    }

    /// <summary>按主版本号在本机的 JDK 里找一套能用的；找不到返回空串。</summary>
    private string JdkHomeByMajor(int major)
    {
        if (major <= 0) return "";
        if (_jdkList.Count == 0) _jdkList = Core.Jdks().Where(j => j.Ok).ToList();
        var hit = _jdkList.FirstOrDefault(j => j.Ok && j.Major == major);
        return hit?.Home ?? "";
    }

    private async void OnCompile(object s, RoutedEventArgs e) => await CompileAsync();

    private async void OnCompileRun(object s, RoutedEventArgs e)
    {
        var r = await CompileAsync();
        if (r != null && r.Ok) await RunAsync();
    }

    private async void OnRunOnly(object s, RoutedEventArgs e) => await RunAsync();

    private async Task<Core.BuildResult> CompileAsync()
    {
        if (string.IsNullOrEmpty(_root)) { Core.Log("WARN", Lang.T("log.noProject")); return null; }
        SetStatus(Lang.T("st.compiling"), true);
        ShowBottom(0);

        // ⚠️ JdkHome() / Classpath() 里读的是 UI 控件（_jdkBox），必须在 UI 线程上
        // 先把值取好再交给后台线程 —— 后台线程碰控件就是「调用线程无法访问此对象」。
        // 这里原来漏了这一步（RunAsync / OnCheck 都补过，就这条漏网），所以
        // 「编译一次失败，再点一次就崩」：失败那次走的是 r == null 的早退分支，
        // 没碰到控件；第二次真跑起来，lambda 已经在后台线程里了，_jdkBox 一读就炸。
        string jdkHome = JdkHome();
        string cp = Classpath();
        var r = await Core.OnWorker(() => Core.Compile(_root, jdkHome, cp, false));
        if (r == null) { SetStatus(Lang.T("st.compileFail"), false); return null; }

        AppendBuild(r.Output);
        // 编译器根本没跑起来（javac 起不来 / 源码目录是空的 / mvn 失败）时 Diags 是空的，
        // 直接从输出里捞报错行 —— 否则「输出」一片红，「问题」却写着「无问题」，
        // 这个面板就白设了。有结构化诊断就优先用它，捞出来的没有行列号、跳不了行。
        var diags = r.Diags.Count > 0 || r.Ok ? r.Diags : HarvestErrors(r.Output);
        ShowProblems(diags);
        if (r.Ok) GuideState.NoteBuildOk();   // 首页清单里「编译并运行一次」这一步算过了
        SetStatus(r.Ok ? Lang.T("st.compiled") : string.Format(Lang.T("st.compileFailN"), r.ErrorCount), r.Ok);
        return r;
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrEmpty(_root)) { Core.Log("WARN", Lang.T("log.noProject")); return; }
        if (string.IsNullOrEmpty(_mainClass))
            _mainClass = Core.GuessMain(_root);
        if (string.IsNullOrEmpty(_mainClass))
        {
            Core.Log("ERROR", Lang.T("log.noMain"));
            SetStatus(Lang.T("st.noMain"), false);
            return;
        }
        SetStatus(Lang.T("st.running"), true);
        ShowBottom(0);                 // 程序打出来的纯文本进「输出」，不进「日志」
        // 同 CompileAsync：JdkHome() 读 UI 控件，先在主线程取值再交给后台 lambda。
        string jdkHome = JdkHome();
        string cp = Classpath();
        var r = await Core.OnWorker(() => Core.Run(_root, jdkHome, _mainClass, cp));
        if (r == null) { SetStatus(Lang.T("st.runFail"), false); return; }
        AppendBuild(r.Output);
        SetStatus(r.Ok ? Lang.T("st.runDone") : string.Format(Lang.T("st.exitCode"), r.ExitCode), r.Ok);
    }

    private async void OnCheck(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) { Core.Log("WARN", Lang.T("log.noProject")); return; }
        SetStatus(Lang.T("st.checking"), true);

        // 以前这里调 Core.Check()，内核那套是「纯文本数括号」：只跳过字符串和注释，
        // 泛型、lambda、文本块都会让它误报，报出来的大半是假问题（就是瞎检查）。
        // 改成跑真正的 javac，诊断才是真的。
        //
        // ⚠️ JdkHome() / Classpath() 里读的是 UI 控件（_jdkBox），必须在 UI 线程上
        // 先把值取好再交给后台线程 —— 后台线程碰控件就是「调用线程无法访问此对象」。
        string jdkHome = JdkHome();
        string cp = Classpath();
        var r = await Core.OnWorker(() => Core.Compile(_root, jdkHome, cp, false));

        var diags = r?.Diags ?? new List<Core.Diagnostic>();
        // 同 CompileAsync：编译器整体失败时 Diags 是空的，从输出里捞报错行兜底
        if (diags.Count == 0 && r is { Ok: false }) diags = HarvestErrors(r.Output);
        ShowProblems(diags);
        SetStatus(diags.Count == 0 ? Lang.T("st.noProblem") : string.Format(Lang.T("st.problemsN"), diags.Count), diags.Count == 0);
        if (diags.Count > 0) ShowBottom(2);   // 问题
    }

    private void ShowProblems(List<Core.Diagnostic> diags)
    {
        ApplyDiagnostics(diags);      // 出错的行在编辑器里标红（波浪线见 EditorEx）
        Problems.Items.Clear();
        SaveProblems(diags);          // 顺手落盘，切项目/重启后这个面板还是满的

        var errs = diags.Where(d => d.Level == "error").ToList();
        var warns = diags.Where(d => d.Level == "warning").ToList();

        // ---- 总日志：一行汇总，一眼看几个错几个警告
        var total = new TextBlock
        {
            Text = string.Format(Lang.T("prob.summary"), errs.Count, warns.Count),
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        total.SetResourceReference(TextBlock.ForegroundProperty,
            errs.Count > 0 ? "Brush.Error" : "Brush.Fg.Muted");
        Problems.Items.Add(new ListBoxItem { Content = total, IsHitTestVisible = false });

        if (errs.Count == 0 && warns.Count == 0)
        {
            var none = new TextBlock { Text = Lang.T("st.noProblem"), FontSize = 12 };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Fg.Dim");
            Problems.Items.Add(new ListBoxItem { Content = none, IsHitTestVisible = false });
            return;
        }

        // ---- 每条报错一行：文件:行:列 + 原因，点一下/双击自动跳到那一行
        foreach (var d in diags)
        {
            string loc = BuildLoc(d);
            var tb = new TextBlock
            {
                // 没有位置信息的（编译器整体失败了、从输出里捞出来的那种）就只留原文，
                // 别在前面顶一句「未知文件」——那行字对定位没用，但是很吵。
                Text = loc.Length == 0 ? d.Message : $"{loc}  {d.Message}",
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty,
                d.Level == "error" ? "Brush.Error" : "Brush.Warn");
            var item = new ListBoxItem { Content = tb, Tag = d, Cursor = Cursors.Hand };
            // 只有能定位的才给手型光标和跳转，否则点了没反应像坏了
            if (d.File.Length == 0 || d.Line <= 0) item.Cursor = Cursors.Arrow;
            else item.PreviewMouseLeftButtonUp += (_, _) => JumpTo(d);
            Problems.Items.Add(item);
        }
    }

    /// <summary>
    /// 编译器整个失败（javac 起不来、mvn 报错、源码目录空…）时，Diags 是空的，
    /// 「问题」面板就会显示「无问题」—— 明明上面「输出」里一大堆红字。
    /// 这里从构建输出里把报错行捞出来，至少让「问题」不漏掉这些。
    ///
    /// 只认明确的报错措辞，不做泛化匹配：宁可少捞，也不要把普通输出当报错塞进来。
    /// </summary>
    private static List<Core.Diagnostic> HarvestErrors(string output)
    {
        var list = new List<Core.Diagnostic>();
        if (string.IsNullOrEmpty(output)) return list;
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            bool bad =
                line.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("错误:", StringComparison.Ordinal) ||
                line.Contains("错误：", StringComparison.Ordinal) ||
                line.Contains("Exception", StringComparison.Ordinal) ||
                line.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("编译失败", StringComparison.Ordinal) ||
                line.StartsWith("BUILD FAILURE", StringComparison.OrdinalIgnoreCase);
            if (!bad) continue;
            // 纯文本报错，没有文件行号 —— 给个空 File，UI 那边就只显示原文
            list.Add(new Core.Diagnostic("error", "", 0, 0, line));
            if (list.Count >= 200) break;   // 别把整个 javac 输出搬进面板
        }
        return list;
    }

    /// <summary>文件:行:列；文件识别不出来就明说，别给个空位置。
    /// File 为空 = 这条本来就没有位置信息（见 HarvestErrors），返回空串让调用方只显示原文。</summary>
    private static string BuildLoc(Core.Diagnostic d)
    {
        if (string.IsNullOrEmpty(d.File)) return "";
        string file = Path.GetFileName(d.File);
        if (d.Line <= 0)
            return string.Format(Lang.T("prob.cantLocateFile"), file);
        return d.Col > 0 ? $"{file}:{d.Line}:{d.Col}" : $"{file}:{d.Line}";
    }

    /// <summary>自动定位：打开文件、滚到那一行、把光标怼过去、选中那个词。</summary>
    private void JumpTo(Core.Diagnostic d)
    {
        if (string.IsNullOrEmpty(d.File) || d.Line <= 0 || !File.Exists(d.File))
        {
            SetStatus(Lang.T("st.cantLocate"), false);
            return;
        }
        OpenFile(d.File);
        ShowBottom(2);      // 留在「问题」窗口，方便接着点下一条
        if (!_open.TryGetValue(d.File, out var ed)) return;

        int line = Math.Max(1, Math.Min(d.Line, ed.LineCount));
        ed.ScrollToLine(line);
        var dl = ed.Document.GetLineByNumber(line);
        ed.Select(dl.Offset, Math.Min(dl.Length, Math.Max(1, d.Col)));
        ed.CaretOffset = dl.Offset + Math.Min(dl.Length, Math.Max(0, d.Col));
        ed.TextArea.Caret.BringCaretToView();
        ed.Focus();
    }

    private void OnProblemJump(object s, MouseButtonEventArgs e)
    {
        if (Problems.SelectedItem is ListBoxItem { Tag: Core.Diagnostic d })
            JumpTo(d);
    }

    // ---------------------------------------------------------------- 主窗口里的弹窗

    /// <summary>弹窗标题栏右上角那个「×」= 取消这个弹窗。</summary>
    private void OnDlgClose(object s, RoutedEventArgs e) => DlgHost.CloseNothing();

    /// <summary>点遮罩空白处也是取消，跟点「×」一个意思（弹窗的常规手感）。</summary>
    private void OnDlgScrimClick(object s, MouseButtonEventArgs e) => DlgHost.CloseNothing();

    // ---------------------------------------------------------------- 第三方库

    private async void OnLibs(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_root)) { Core.Log("WARN", Lang.T("log.noProject")); return; }
        var dlg = new LibsDialog(_root) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // 生效的依赖按「pom + libs.txt」合起来算（见 Core.LibsEffective）：
        // 只认 libs.txt 的话，往 pom 里加的依赖根本进不了 classpath。
        var res = await Core.OnWorker(() =>
            Core.LibsResolve(_root, string.Join(";", Core.LibsEffective(_root))));
        if (res != null)
        {
            _libJars = res.Jars;
            Core.Log("OK", string.Format(Lang.T("log.libsOnCp"), res.Jars.Count));
        }
    }

    // ---------------------------------------------------------------- 预览

    private void OnPreview(object s, RoutedEventArgs e) => PreviewSelected();
    private void OnPreviewItem(object s, RoutedEventArgs e) => PreviewSelected();

    private void PreviewSelected()
    {
        if (Tree.SelectedItem is not TreeItem it)
        {
            SetStatus(Lang.T("preview.none"), false);
            return;
        }
        if (!File.Exists(it.Path))
        {
            SetStatus(Lang.T("preview.folder"), false);
            return;
        }
        var ext = Path.GetExtension(it.Path).ToLowerInvariant();
        if (ext != ".svg" && ext != ".md" && ext != ".markdown")
            SetStatus(Lang.T("preview.unsupported"), false);
        new PreviewWindow(it.Path) { Owner = this }.Show();
    }

    // ---------------------------------------------------------------- 杂项

    /// <summary>
    /// 渲染一张自己的截图（JS_SHOT 用）。
    ///
    /// 两个可选参数，看小控件（比如标题栏那几个 13px 的圆点）时离不了：
    ///   JS_SHOT_SCALE=4   按 4 倍密度采样。注意要连 DPI 一起乘，
    ///                     只改像素尺寸的话 WPF 会把画面裁掉一角而不是放大。
    ///   JS_SHOT_CROP=x,y,w,h   只留这一块（坐标是放大后的像素）。
    /// </summary>
    public void ShotWhenReady(string path)
    {
        Loaded += async (_, _) =>
        {
            await Task.Delay(DelayMs());        // 等布局稳定（JS_SHOT_DELAY 可调）
            try
            {
                double scale = 1.0;
                if (double.TryParse(Environment.GetEnvironmentVariable("JS_SHOT_SCALE"), out double sv)
                    && sv > 0.1 && sv <= 8)
                    scale = sv;

                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(ActualWidth * scale), (int)(ActualHeight * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bmp.Render(this);

                System.Windows.Media.Imaging.BitmapSource src = bmp;
                string crop = Environment.GetEnvironmentVariable("JS_SHOT_CROP");
                if (!string.IsNullOrEmpty(crop))
                {
                    var p = crop.Split(',');
                    if (p.Length == 4 &&
                        int.TryParse(p[0], out int cx) && int.TryParse(p[1], out int cy) &&
                        int.TryParse(p[2], out int cw) && int.TryParse(p[3], out int ch) &&
                        cw > 0 && ch > 0)
                        src = new System.Windows.Media.Imaging.CroppedBitmap(
                            bmp, new Int32Rect(cx, cy, cw, ch));
                }

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                using var fs = System.IO.File.Create(path);
                enc.Save(fs);
            }
            catch (Exception ex) { Core.Log("WARN", Lang.T("log.shotFail") + ex.Message); }
        };
    }

    /// <summary>截图前等多久。编译类的自检要把 JS_SHOT_DELAY 拉到 8000+。</summary>
    private static int DelayMs()
        => int.TryParse(Environment.GetEnvironmentVariable("JS_SHOT_DELAY"), out int v) && v > 0
            ? v : 1200;

    private void SetStatus(string text, bool ok)
    {
        // 跨线程保险：SetStatus 是全项目调用点最多的 UI 方法，
        // 不在 UI 线程就先切回去 —— FindResource 和控件赋值都要求 UI 线程。
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetStatus(text, ok)); return; }

        StatusLeft.Text = text;
        StatusLeft.Foreground = (Brush)FindResource("Brush.Fg.Muted");
        StatusDot.Fill = (Brush)FindResource(ok ? "Brush.Ok" : "Brush.Error");
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        if (ctrl && !shift && e.Key == Key.S) { OnSave(sender, e); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { OnSaveAll(sender, e); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.B) { OnCompile(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { OnCompileRun(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            // Esc 回主页，跟菜单「视图 → 起始页」一个手势
            ShowHome(); e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.E) { ShowRecentFiles(); e.Handled = true; }
        else if (ctrl && e.Key == Key.L) { OnToggleBottom(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == Key.T) { OnToggleTheme(sender, e); e.Handled = true; }
        // Ctrl+Alt+A：开合 To-code Studio Pro（用户指定的快捷键）
        else if (ctrl && alt && e.Key == Key.A) { ToggleAgent(); e.Handled = true; }
    }
}
