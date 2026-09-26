// 图标库
//
// 用户已有 177 个线性风格 SVG 图标（skills/ui-icon-library），
// 这里直接读那套 icons.json + .svg，不重新设计。
//
// 关键点：解析出来的图形要能按任意尺寸和任意颜色重绘，
// 所以只存几何路径，颜色在绘制时给。
#pragma once

#include <map>
#include <memory>
#include <string>
#include <vector>

#include "platform/platform.h"

namespace spp {
namespace icons {

using plat::Canvas;
using plat::Color;

// 一个图形。和 SVG 的图元一一对应。
enum class ShapeKind { Path, Polyline, Polygon, Line, Circle, Rect };

struct Shape {
    ShapeKind kind = ShapeKind::Path;
    std::vector<float> pts;      // 路径/折线的点，x,y 交替
    bool closed = false;         // 路径是否闭合（描边闭合缝）
    float cx = 0, cy = 0, r = 0; // 圆
    float rw = 0, rh = 0;        // 矩形宽高
    float rx = 0, ry = 0;        // 矩形圆角
};

// 图标库
class Library {
public:
    // dir 里应当有 icons.json；找不到单个图标时回退到 <name>.svg
    explicit Library(std::string dir);

    // 读 icons.json + 建立名字索引。返回读到的图标数。
    int load();

    int count() const { return (int(names_.size())); }
    const std::vector<std::string>& names() const { return names_; }

    // 名字 → SVG 源码。没有返回空串。
    std::string svgOf(const std::string& name) const;

    // 名字 → 图形列表。带缓存。
    const std::vector<Shape>& shapes(const std::string& name) const;

    // 按关键词搜索，返回按相关度排序的名字
    std::vector<std::string> search(const std::string& q, int limit = 40) const;

    // 分类（icons.json 里的 category 字段）
    std::string category(const std::string& name) const;

    // 预解析全部图标，启动时调一次，避免画的时候卡顿
    int warmAll();

    const std::string& error() const { return err_; }

private:
    std::string dir_;
    std::string err_;

    struct Entry {
        std::string keywords;
        std::string category;
        std::string svg;
    };
    std::map<std::string, Entry> entries_;
    std::vector<std::string> names_;

    // 解析缓存。mutable 是因为 shapes() 是 const 的但要填缓存。
    mutable std::map<std::string, std::vector<Shape>> cache_;
};

// 把图形画到画布上。size 是目标边长（原始按 24x24 设计），
// color 用于描边。线性图标只描边不填充。
void draw(Canvas& c, const std::vector<Shape>& shapes, int x, int y, int size,
          Color color, float strokeScale = 1.0f);

// 从 SVG 源码解析出图形。暴露出来是为了单测。
std::vector<Shape> parseSvg(const std::string& svg);

// 解析 SVG path 的 d 属性
std::vector<Shape> parsePathData(const std::string& d);

}  // namespace icons
}  // namespace spp
