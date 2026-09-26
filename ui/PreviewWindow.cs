// PreviewWindow.cs —— 从项目树「预览」打开的独立预览窗口
//
// 它和编辑器里的 md 预览是两回事：
//   · 这个是**独立窗口**，给「就想看一眼这个文件」用的；
//   · md 在编辑器里打开时用的是 MdPane（编辑 / 拆分 / 预览三态，见 MdPreview.cs）。
// 两边共用 MdDoc 那套转换，免得同一份 md 在两个地方渲染出两种样子。
//
// ⚠️ md 走 **WPF 原生渲染（FlowDocument）**，不走 WebBrowser。
// WPF 的 WebBrowser 用的是退役的 Trident 内核，在这台机器上**根本画不出东西**
// （喂它纯红 body 也是一个红点都没有，已实测）。svg 暂时还得用它 ——
// WPF 自己没有 SVG 解析器，换别的路子是另一件事，先不动。

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace JavaStudio;

internal sealed class PreviewWindow : Window
{
    public PreviewWindow(string path)
    {
        Title = Path.GetFileName(path) + " · " + Lang.T("preview.of");
        Width = 860; Height = 660;
        MinWidth = 480; MinHeight = 360;
        Background = Ui.B("Brush.Bg.Base");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = Build(path);
    }

    private static UIElement Build(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".md" || ext == ".markdown")
                return Doc(MdDoc.Build(File.ReadAllText(path)));

            if (ext == ".svg")
            {
                var wb = new WebBrowser();
                wb.Navigate(new Uri(path));
                return wb;
            }
            return Doc(MdDoc.Plain(Lang.T("preview.unsupported")));
        }
        catch (Exception ex)
        {
            return Doc(MdDoc.Plain(Lang.T("preview.fail") + ex.Message));
        }
    }

    private static FlowDocumentScrollViewer Doc(System.Windows.Documents.FlowDocument d)
        => new()
        {
            Document = d,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
        };
}
