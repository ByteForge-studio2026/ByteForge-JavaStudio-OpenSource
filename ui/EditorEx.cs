// 这个文件里用了可空注解（Popup?、Dictionary<int, Brush>?），
// 工程整体是 <Nullable>disable</Nullable>，所以单独打开它。
#nullable enable

// EditorEx.cs —— 编辑器那点额外的事：右键菜单、行标注、Tab 补全
//
// 拆到这个文件是因为 MainWindow 已经够长了，而这些都是围绕 TextEditor
// 的一组自包含功能，塞回去只会让主页那块更难找。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace JavaStudio;

/// <summary>
/// 每个打开的文件记一份：原始内容（用来比对改了哪几行）+ 标注 + 波浪线。
/// 错误信息不只有行号，还带列号和整行的文本，这样波浪线才能画到
/// 出错的那一小段文字底下，而不是糊到整行上。
/// </summary>
public sealed class EditorState
{
    public TextEditor Ed = null!;
    public string PathValue = "";
    public string[] Saved = Array.Empty<string>();
    public readonly HashSet<int> Errors = new();
    public readonly List<(int Line, int Start, int Len, string Msg)> Squiggles = new();
    public readonly Dictionary<int, Brush> Marks = new();
    public readonly LineMarkRenderer Renderer = new();
    public readonly SquiggleRenderer Squiggle = new();
}

/// <summary>
/// 给整行刷底色：改动过的行刷绿，有错误的行刷红。
/// 画在 KnownLayer.Background，坐标要减掉纵向滚动偏移才是可见区坐标。
/// </summary>
public sealed class LineMarkRenderer : IBackgroundRenderer
{
    public Dictionary<int, Brush>? Marks;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        var marks = Marks;
        if (marks == null || marks.Count == 0 || !textView.VisualLinesValid) return;
        foreach (var vl in textView.VisualLines)
        {
            if (!marks.TryGetValue(vl.FirstDocumentLine.LineNumber, out var b)) continue;
            double y = vl.VisualTop - textView.VerticalOffset;
            if (y + vl.Height < 0 || y > textView.ActualHeight) continue;
            dc.DrawRectangle(b, null, new Rect(0, y, textView.ActualWidth, vl.Height));
        }
    }
}

/// <summary>
/// 红色波浪线：画在出错的那段文字正下方，IDEA / VS 同款。
/// 挂在 KnownLayer.Selection 之上（用 Background 会被行底色盖住）。
/// </summary>
public sealed class SquiggleRenderer : IBackgroundRenderer
{
    public List<(int Line, int Start, int Len, string Msg)>? Errors;

    private static readonly Pen RedPen =
        new(new SolidColorBrush(Color.FromRgb(0xE0, 0x32, 0x32)), 1.4);

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        var errs = Errors;
        if (errs == null || errs.Count == 0 || !textView.VisualLinesValid) return;

        foreach (var e in errs)
        {
            var doc = textView.Document;
            if (e.Line < 1 || e.Line > doc.LineCount) continue;
            var line = doc.GetLineByNumber(e.Line);

            int startOff = line.Offset + e.Start;
            int endOff = Math.Min(line.EndOffset, startOff + Math.Max(1, e.Len));

            // 找到这条行对应的可视行（AvalonEdit 有换行折叠，一个文档行可能占多条可视行）
            var vl = textView.GetVisualLine(line.LineNumber);
            if (vl == null) continue;

            // 用 BackgroundGeometryBuilder 拿这段文字的矩形
            var seg = new TextSegment { StartOffset = startOff, EndOffset = endOff };
            var rects = BackgroundGeometryBuilder.GetRectsForSegment(textView, seg);
            foreach (var r in rects)
            {
                double y = r.Bottom + 1;
                if (y < 0 || y > textView.ActualHeight) continue;
                double x0 = r.Left, x1 = r.Right;
                if (x1 - x0 < 2) x1 = x0 + 4;   // 太窄了补一点，至少看得出是条线

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    bool up = true;
                    for (double x = x0; x <= x1; x += 2.0)
                    {
                        if (x == x0) ctx.BeginFigure(new Point(x, y), false, false);
                        else ctx.LineTo(new Point(x, up ? y : y + 1.6), true, false);
                        up = !up;
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, RedPen, geo);
            }
        }
    }
}

public partial class MainWindow
{
    private readonly Dictionary<string, EditorState> _states = new();

    private static readonly Brush GreenMark =
        new SolidColorBrush(Color.FromArgb(48, 46, 160, 67));
    private static readonly Brush RedMark =
        new SolidColorBrush(Color.FromArgb(60, 214, 69, 80));

    // ---------------------------------------------------------------- 挂钩

    /// <summary>新开的编辑器装上右键菜单、行标注和按键处理。</summary>
    private void AttachEditor(TextEditor ed, string path, string text)
    {
        var st = new EditorState
        { Ed = ed, PathValue = path, Saved = text.Split('\n') };
        st.Renderer.Marks = st.Marks;
        st.Squiggle.Errors = st.Squiggles;
        _states[path] = st;

        ed.TextArea.TextView.BackgroundRenderers.Add(st.Renderer);
        ed.TextArea.TextView.BackgroundRenderers.Add(st.Squiggle);
        ed.ContextMenu = BuildEditorMenu(ed);
        ed.PreviewKeyDown += (_, e) => OnEditorKey(ed, e);
        ed.TextChanged += (_, _) => { ed.Tag = "dirty"; MarkTabDirty(path, true); RefreshMarks(path); };
        ed.PreviewMouseDown += (_, _) => CloseCompletion();
    }

    /// <summary>保存之后，这一版内容就成了新的基线，绿色标注跟着清掉。</summary>
    private void MarkSaved(string path, string text)
    {
        if (!_states.TryGetValue(path, out var st)) return;
        st.Saved = text.Split('\n');
        st.Ed.Tag = null;
        MarkTabDirty(path, false);
        RefreshMarks(path);
    }

    private void RefreshMarks(string path)
    {
        if (!_states.TryGetValue(path, out var st)) return;
        var cur = st.Ed.Document.Text.Split('\n');
        st.Marks.Clear();

        // 绿：和保存过的那一版不一样的行（多出来的行也算新行）
        for (int i = 0; i < cur.Length; i++)
        {
            string? old = i < st.Saved.Length ? st.Saved[i] : null;
            if (old == null || !string.Equals(old, cur[i])) st.Marks[i + 1] = GreenMark;
        }
        // 红：编译/检查报出来的行，盖过绿色
        foreach (int ln in st.Errors) st.Marks[ln] = RedMark;

        st.Ed.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        st.Ed.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>把诊断结果摊到各个打开的文件上（标红 + 波浪线）。</summary>
    private void ApplyDiagnostics(List<Core.Diagnostic> diags)
    {
        foreach (var st in _states.Values) { st.Errors.Clear(); st.Squiggles.Clear(); }
        foreach (var d in diags)
        {
            if (!_states.TryGetValue(d.File, out var st) || d.Line <= 0) continue;
            st.Errors.Add(d.Line);

            // 定位波浪线的范围：优先用 javac 给的列号，没有就画整行
            var line = st.Ed.Document.GetLineByNumber(Math.Min(d.Line, st.Ed.LineCount));
            int col = Math.Max(0, d.Col - 1);
            int start = Math.Min(col, line.Length);
            int len = Math.Max(1, line.Length - start);
            // 列号指向的如果是标识符，就把整段标识符画进去
            if (col < line.Length && char.IsLetterOrDigit(st.Ed.Document.GetCharAt(line.Offset + col)))
            {
                int s = col, en = col;
                while (s > 0 && char.IsLetterOrDigit(st.Ed.Document.GetCharAt(line.Offset + s - 1))) s--;
                while (en < line.Length && char.IsLetterOrDigit(st.Ed.Document.GetCharAt(line.Offset + en))) en++;
                start = s; len = en - s;
            }
            st.Squiggles.Add((d.Line, start, Math.Max(1, len), d.Message));
        }
        foreach (var kv in _states) RefreshMarks(kv.Key);
    }

    // ---------------------------------------------------------------- 右键菜单

    private ContextMenu BuildEditorMenu(TextEditor ed)
    {
        var m = new ContextMenu();
        void Add(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        Add(Lang.T("ed.undo"), () => ed.Undo());
        Add(Lang.T("ed.redo"), () => ed.Redo());
        m.Items.Add(new Separator());
        Add(Lang.T("ed.cut"), () => ed.Cut());
        Add(Lang.T("ed.copy"), () => ed.Copy());
        Add(Lang.T("ed.paste"), () => ed.Paste());
        Add(Lang.T("ed.selectAll"), () => ed.SelectAll());
        m.Items.Add(new Separator());
        Add(Lang.T("ed.complete"), () => ShowCompletion(ed));
        Add(Lang.T("ed.indent"), () => Indent(ed, false));
        m.Items.Add(new Separator());
        Add(Lang.T("ed.save"), () => OnSave(this, new RoutedEventArgs()));
        return m;
    }

    // ---------------------------------------------------------------- Tab / 补全

    private Popup? _pop;
    private ListBox? _popList;
    private TextEditor? _popEd;
    private int _popOffset;

    private static readonly string[] JavaWords =
    {
        "abstract","assert","boolean","break","byte","case","catch","char","class","const",
        "continue","default","do","double","else","enum","extends","final","finally","float",
        "for","if","implements","import","instanceof","int","interface","long","native","new",
        "package","private","protected","public","return","short","static","strictfp","super",
        "switch","synchronized","this","throw","throws","transient","try","void","volatile","while",
        "System","String","StringBuilder","List","ArrayList","Map","HashMap","Set","HashSet",
        "public static void main","System.out.println","try {} catch","for (int i = 0; i < ; i++)"
    };

    private void OnEditorKey(TextEditor ed, KeyEventArgs e)
    {
        // 补全列表开着的时候，方向键和回车归列表
        if (_pop != null && _pop.IsOpen)
        {
            if (e.Key == Key.Down)
            {
                if (_popList != null && _popList.SelectedIndex < _popList.Items.Count - 1)
                    _popList.SelectedIndex++;
                e.Handled = true; return;
            }
            if (e.Key == Key.Up)
            {
                if (_popList != null && _popList.SelectedIndex > 0) _popList.SelectedIndex--;
                e.Handled = true; return;
            }
            if (e.Key == Key.Enter || e.Key == Key.Tab)
            { AcceptCompletion(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { CloseCompletion(); e.Handled = true; return; }
        }

        if (e.Key == Key.Space && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { ShowCompletion(ed); e.Handled = true; return; }

        if (e.Key == Key.Tab)
        {
            // 没开补全列表时，Tab 就是缩进；按住 Shift 往回缩
            Indent(ed, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    private static void Indent(TextEditor ed, bool back)
    {
        const string unit = "    ";   // 4 空格，跟状态栏上写的「4」对上
        var doc = ed.Document;
        int start = ed.SelectionStart, len = ed.SelectionLength;

        if (len > 0 && doc.Text.Substring(start, len).Contains('\n'))
        {
            // 多行：整块一起缩进
            var seg = ed.Document.GetLineByOffset(start);
            var end = ed.Document.GetLineByOffset(start + len);
            for (int ln = seg.LineNumber; ln <= end.LineNumber; ln++)
            {
                var line = doc.GetLineByNumber(ln);
                string t = doc.GetText(line);
                if (back)
                {
                    int cut = 0;
                    while (cut < unit.Length && cut < t.Length && t[cut] == ' ') cut++;
                    doc.Replace(line.Offset, cut, "");
                }
                else if (t.TrimStart().Length > 0)
                    doc.Insert(line.Offset, unit);
            }
            return;
        }

        if (back)
        {
            var line = doc.GetLineByOffset(ed.CaretOffset);
            int lineStart = line.Offset;
            int cut = 0;
            while (cut < unit.Length && ed.CaretOffset - cut > lineStart &&
                   doc.GetCharAt(ed.CaretOffset - cut - 1) == ' ') cut++;
            if (cut > 0) doc.Remove(ed.CaretOffset - cut, cut);
        }
        else
            doc.Insert(ed.CaretOffset, unit);
    }

    private static string WordBeforeCaret(TextEditor ed, out int offset)
    {
        int i = ed.CaretOffset - 1;
        while (i >= 0)
        {
            char c = ed.Document.GetCharAt(i);
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') break;
            i--;
        }
        offset = i + 1;
        return ed.Document.GetText(offset, ed.CaretOffset - offset);
    }

    private void ShowCompletion(TextEditor ed)
    {
        string prefix = WordBeforeCaret(ed, out int off);
        var shortPrefix = prefix.Contains('.') ? prefix.Substring(prefix.LastIndexOf('.') + 1) : prefix;

        // 候选：Java 关键字 + 当前文件里出现过的标识符
        var ids = Regex.Matches(ed.Document.Text, @"\b[A-Za-z_][A-Za-z0-9_]{1,}\b")
                       .Select(m => m.Value);
        var list = JavaWords.Concat(ids)
            .Where(w => w.Length > shortPrefix.Length &&
                        w.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(w => w.StartsWith(shortPrefix, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(w => w)
            .Take(40).ToList();
        if (list.Count == 0) return;

        _popList = new ListBox
        {
            ItemsSource = list, SelectedIndex = 0, MaxHeight = 220, Width = 260,
            FontSize = 12,
            Background = (Brush)FindResource("Brush.Bg.Panel"),
            Foreground = (Brush)FindResource("Brush.Fg.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border")
        };
        _popList.MouseDoubleClick += (_, _) => AcceptCompletion();

        _popEd = ed;
        _popOffset = off + (prefix.Length - shortPrefix.Length);
        var rect = ed.TextArea.Caret.CalculateCaretRectangle();
        _pop = new Popup
        {
            Child = _popList, PlacementTarget = ed, Placement = PlacementMode.Relative,
            PlacementRectangle = rect, StaysOpen = false, IsOpen = true
        };
    }

    private void AcceptCompletion()
    {
        if (_popList?.SelectedItem is not string word || _popEd == null) { CloseCompletion(); return; }
        string prefix = WordBeforeCaret(_popEd, out _);
        var shortPrefix = prefix.Contains('.') ? prefix.Substring(prefix.LastIndexOf('.') + 1) : prefix;
        _popEd.Document.Replace(_popEd.CaretOffset - shortPrefix.Length, shortPrefix.Length, word);
        CloseCompletion();
    }

    private void CloseCompletion()
    {
        if (_pop != null) { _pop.IsOpen = false; _pop = null; }
        _popList = null;
        _popEd = null;
    }
}
