// Pickers.cs —— 文件 / 目录选择器
//
// 历史：这里原来有一套自绘的 FolderPicker / FilePicker（WPF 重写，
// 为了让深色主题下选择框也是深色的）。试过之后回退了，原因是它把
// 「展开的树节点」「路径框里的文本」「双击/单选的落点」三套状态各自
// 维护，一旦用户先点树再改路径框，最后选中的目录就跟眼睛看到的不一致；
// 表现就是「选完了却打开别的地方 / 界面像坏了」。
// 选目录这件事，交给系统资源管理器最稳，用户也本来就是那么操作的。
// 代价是深色主题下弹出来是亮白的系统框 —— 认了，稳定性优先。
//
// 所以现在这个文件只剩一层薄封装：把调用方习惯的 "*.*" 写法
// 翻译成系统对话框要的「说明|通配」格式，其余全交给 OS。

using System;
using System.IO;
using System.Windows;

namespace JavaStudio;

// ================================================================ 静态入口

internal static class Pickers
{
    /// <summary>
    /// 选一个目录。返回路径，取消返回 ""。
    ///
    /// 这里用 Windows 自带的 FolderBrowserDialog。
    /// 为什么不自己画：见本文件顶部那段说明。
    /// </summary>
    public static string PickFolder(Window owner, string title, string start = "")
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            d.SelectedPath = start;

        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : "";
    }

    /// <summary>
    /// 选文件（可多选）。返回路径数组，取消返回空数组。
    /// 同样走系统 OpenFileDialog，理由见 PickFolder。
    /// </summary>
    public static string[] PickFile(Window owner, string title,
        string filter = "*.*", bool multi = false, string start = "")
    {
        using var d = new System.Windows.Forms.OpenFileDialog
        {
            Title = title,
            Multiselect = multi,
            Filter = FilterText(filter),
        };
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
            d.InitialDirectory = start;

        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? d.FileNames : Array.Empty<string>();
    }

    /// <summary>
    /// 内部用的 "*.*" 那种写法转成系统对话框认的「说明|通配」格式。
    /// 传进来的可能是 "*.*"，也可能是 "*.java;*.txt" 这种分号串。
    /// </summary>
    private static string FilterText(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter == "*.*")
            return Lang.T("pk.fileTypeAll") + "|*.*";

        return Lang.T("pk.fileType") + "|" + filter + "|"
             + Lang.T("pk.fileTypeAll") + "|*.*";
    }
}
