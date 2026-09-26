// Icons.cs —— 把 C++ 侧解析好的图标图形变成 WPF 的 Geometry
//
// 为什么不自己解析 SVG：C++ 里那个解析器已经过了 177/177 的验证，
// 连弧线转折线、紧凑数字写法（1.35.09）这些坑都踩过了。
// 在 C# 里重写一遍纯属给自己找活。所以这里只做「图形 → Geometry」。
//
// 图标按 24x24 设计，返回的 Geometry 也在 24x24 坐标系里。
// 用的时候套一个 Stretch="Uniform" 的 Path，要多大给多大。
//
// 图标原作是 Morphicons（MIT），描边风格改成 24×24 后用在 Java Studio 里。
// 出处集中放在 Credit 这一个常量上：图标库窗口和「关于」窗口都读它，
// 免得两处文案改了一处忘了另一处。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace JavaStudio;

internal static class Icons
{
    /// <summary>图标出处。界面上凡是要标注来源的地方（图标库、关于）都用这一句。</summary>
    /// 用属性而不是 const：const 在编译期就写死，切语言换不掉。
    public static string Credit => Lang.T("icons.credit");

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static int Count => Cache.Count;

    public static void Load()
    {
        if (Cache.Count > 0) return;

        string json;
        try
        {
            json = Native.IconsAll(Core.IconsDir);
        }
        catch (Exception ex)
        {
            Core.Log("WARN", Lang.T("log.iconReadFail") + ex.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            Core.Log("WARN", Lang.T("log.iconDirEmpty") + Core.IconsDir);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                Cache[prop.Name] = Build(prop.Value);
        }
        catch (Exception ex)
        {
            Core.Log("WARN", Lang.T("log.iconParseFail") + ex.Message);
        }
    }

    private static Geometry Build(JsonElement shapes)
    {
        var group = new GeometryGroup();
        if (shapes.ValueKind != JsonValueKind.Array) return group;

        foreach (var sh in shapes.EnumerateArray())
        {
            string kind = sh.TryGetProperty("kind", out var k) ? k.GetString() : "path";
            switch (kind)
            {
                case "circle":
                {
                    double cx = Num(sh, "cx"), cy = Num(sh, "cy"), r = Num(sh, "r");
                    group.Children.Add(new EllipseGeometry(new Point(cx, cy), r, r));
                    break;
                }
                case "rect":
                {
                    double x = Num(sh, "x"), y = Num(sh, "y");
                    double w = Num(sh, "w"), h = Num(sh, "h"), rx = Num(sh, "rx");
                    group.Children.Add(new RectangleGeometry(new Rect(x, y, w, h), rx, rx));
                    break;
                }
                default:
                {
                    if (!sh.TryGetProperty("pts", out var pts) ||
                        pts.ValueKind != JsonValueKind.Array) break;

                    bool closed = sh.TryGetProperty("closed", out var c) &&
                                  c.ValueKind == JsonValueKind.True;
                    var figure = new PathFigure { IsClosed = closed };
                    bool first = true;
                    Point? pending = null;
                    foreach (var v in pts.EnumerateArray())
                    {
                        double val = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                        if (pending == null) { pending = new Point(val, 0); }
                        else
                        {
                            var pt = new Point(pending.Value.X, val);
                            if (first) { figure.StartPoint = pt; first = false; }
                            else figure.Segments.Add(new LineSegment(pt, true));
                            pending = null;
                        }
                    }
                    if (!first) group.Children.Add(new PathGeometry(new[] { figure }));
                    break;
                }
            }
        }
        return group;
    }

    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;

    /// <summary>拿到图标的 Geometry。没有这个名字就返回 null。</summary>
    public static Geometry Get(string name)
        => name != null && Cache.TryGetValue(name, out var g) ? g : null;

    /// <summary>库里有没有这个图标。拼错名字时好歹能退化成文字，不至于空一块。</summary>
    public static bool Has(string name) => Get(name) != null;

    /// <summary>直接给一个画好的控件。size 是边长（像素）。</summary>
    public static FrameworkElement Visual(string name, int size, Brush stroke, double thickness = 1.6)
    {
        var geo = Get(name);
        if (geo == null) return null;
        // 图标是 24x24 设计的，线宽按尺寸等比缩放才不会大图标变细线
        double k = size / 24.0;
        return new System.Windows.Shapes.Path
        {
            Data = geo,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Stroke = stroke,
            StrokeThickness = thickness * k,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
    }

    /// <summary>
    /// 同上，但颜色给的是主题里的画刷键名（"Brush.Fg.Muted" 这种）。
    /// 走 SetResourceReference，切主题时图标颜色自己跟着变——工具栏常驻不重建，
    /// 用静态画刷的话切深色后图标还是旧颜色（顶栏图标切深色不变白就是这个坑）。
    /// </summary>
    public static FrameworkElement Visual(string name, int size, string brushKey, double thickness = 1.6)
    {
        var geo = Get(name);
        if (geo == null) return null;
        double k = size / 24.0;
        var path = new System.Windows.Shapes.Path
        {
            Data = geo,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            StrokeThickness = thickness * k,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, brushKey);
        return path;
    }
}
