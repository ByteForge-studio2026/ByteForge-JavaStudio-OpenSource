// 图标库：加载索引 + 绘制
#include "icons/iconlib.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace spp {
namespace icons {

// ---------------------------------------------------------------- JSON 索引
//
// 这个解析器和 svg.cpp 里的重复了一点（都是取字符串字段），
// 但那边是解析 SVG 属性，这边是解析索引文件，格式稳定且简单，
// 合成一个抽象反而更难读。

namespace {

const char* skipWs(const char* p) {
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    return p;
}

const char* parseStr(const char* p, std::string& out) {
    out.clear();
    if (*p != '"') return p;
    p++;
    while (*p && *p != '"') {
        if (*p == '\\' && p[1]) {
            p++;
            switch (*p) {
                case 'n': out += '\n'; break;
                case 't': out += '\t'; break;
                case 'r': out += '\r'; break;
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'u': {
                    char hex[5] = {p[1], p[2], p[3], p[4], 0};
                    unsigned cp = (unsigned)strtoul(hex, nullptr, 16);
                    if (cp < 0x80) out += (char)cp;
                    else if (cp < 0x800) {
                        out += (char)(0xC0 | (cp >> 6));
                        out += (char)(0x80 | (cp & 0x3F));
                    } else {
                        // 代理对
                        if (cp >= 0xD800 && cp <= 0xDBFF && p[5] == '\\' &&
                            p[6] == 'u') {
                            char h2[5] = {p[7], p[8], p[9], p[10], 0};
                            unsigned lo = (unsigned)strtoul(h2, nullptr, 16);
                            if (lo >= 0xDC00 && lo <= 0xDFFF) {
                                unsigned c = 0x10000 + ((cp - 0xD800) << 10) +
                                             (lo - 0xDC00);
                                out += (char)(0xF0 | (c >> 18));
                                out += (char)(0x80 | ((c >> 12) & 0x3F));
                                out += (char)(0x80 | ((c >> 6) & 0x3F));
                                out += (char)(0x80 | (c & 0x3F));
                                p += 6;
                                break;
                            }
                        }
                        out += (char)(0xE0 | (cp >> 12));
                        out += (char)(0x80 | ((cp >> 6) & 0x3F));
                        out += (char)(0x80 | (cp & 0x3F));
                    }
                    p += 4;
                    break;
                }
                default: out += *p; break;
            }
            p++;
        } else {
            out += *p++;
        }
    }
    return *p == '"' ? p + 1 : p;
}

}  // namespace

Library::Library(std::string dir) : dir_(std::move(dir)) {}

int Library::load() {
    std::string path = plat::joinPath(dir_, "icons.json");
    std::string text = plat::readFile(path);
    if (text.empty()) {
        err_ = "读不到 " + path;
        return 0;
    }

    const char* p = skipWs(text.c_str());
    if (*p != '{') {
        err_ = "icons.json 不是对象";
        return 0;
    }
    p = skipWs(p + 1);

    while (*p && *p != '}') {
        std::string key;
        p = parseStr(p, key);
        p = skipWs(p);
        if (*p != ':') break;
        p = skipWs(p + 1);
        if (*p != '{') break;
        p = skipWs(p + 1);

        Entry e;
        // 读这个图标的三个字段
        while (*p && *p != '}') {
            std::string field;
            p = parseStr(p, field);
            p = skipWs(p);
            if (*p != ':') break;
            p = skipWs(p + 1);

            if (*p == '"') {
                std::string val;
                p = parseStr(p, val);
                if (field == "keywords") e.keywords = val;
                else if (field == "category") e.category = val;
                else if (field == "svg") e.svg = val;
            } else {
                // 非字符串字段（当前没有），跳过到逗号
                while (*p && *p != ',' && *p != '}') p++;
            }

            p = skipWs(p);
            if (*p == ',') p = skipWs(p + 1);
        }
        if (*p == '}') p++;
        p = skipWs(p);
        if (*p == ',') p = skipWs(p + 1);

        if (!key.empty()) {
            entries_[key] = std::move(e);
            names_.push_back(key);
        }
    }

    std::sort(names_.begin(), names_.end());

    // 有些名字可能只在目录里有 .svg 而没有进 json，
    // 补一遍目录扫描，保证一个都不少。
    auto files = plat::listDir(dir_);
    for (const auto& f : files) {
        if (f.isDir) continue;
        if (f.name.size() > 4 &&
            f.name.compare(f.name.size() - 4, 4, ".svg") == 0) {
            std::string base = f.name.substr(0, f.name.size() - 4);
            if (!entries_.count(base)) {
                Entry e;
                e.svg = plat::readFile(plat::joinPath(dir_, f.name));
                entries_[base] = std::move(e);
                names_.push_back(base);
            }
        }
    }
    std::sort(names_.begin(), names_.end());
    names_.erase(std::unique(names_.begin(), names_.end()), names_.end());
    return (int)names_.size();
}

std::string Library::svgOf(const std::string& name) const {
    auto it = entries_.find(name);
    if (it != entries_.end() && !it->second.svg.empty()) return it->second.svg;

    // 回退：直接读同名 .svg
    std::string p = plat::joinPath(dir_, name + ".svg");
    return plat::readFile(p);
}

const std::vector<Shape>& Library::shapes(const std::string& name) const {
    auto c = cache_.find(name);
    if (c != cache_.end()) return c->second;

    std::string svg = svgOf(name);
    std::vector<Shape> s;
    if (!svg.empty()) s = parseSvg(svg);

    auto res = cache_.emplace(name, std::move(s));
    return res.first->second;
}

std::string Library::category(const std::string& name) const {
    auto it = entries_.find(name);
    return it == entries_.end() ? std::string() : it->second.category;
}

std::vector<std::string> Library::search(const std::string& q, int limit) const {
    std::vector<std::pair<int, std::string>> scored;
    std::string lower;
    for (char c : q) lower += (char)tolower((unsigned char)c);

    for (const auto& n : names_) {
        int score = 0;
        auto it = entries_.find(n);
        const std::string& kw = (it != entries_.end()) ? it->second.keywords : "";
        const std::string& cat = (it != entries_.end()) ? it->second.category : "";

        std::string nl;
        for (char c : n) nl += (char)tolower((unsigned char)c);

        if (nl == lower) score = 100;
        else if (nl.rfind(lower, 0) == 0) score = 80;
        else if (nl.find(lower) != std::string::npos) score = 60;
        else if (kw.find(q) != std::string::npos) score = 40;
        else if (cat.find(q) != std::string::npos) score = 20;

        if (score > 0) scored.emplace_back(score, n);
    }

    std::sort(scored.begin(), scored.end(),
              [](const std::pair<int, std::string>& a,
                 const std::pair<int, std::string>& b) {
                  if (a.first != b.first) return a.first > b.first;
                  return a.second < b.second;
              });

    std::vector<std::string> out;
    for (size_t i = 0; i < scored.size() && (int)out.size() < limit; i++) {
        out.push_back(scored[i].second);
    }
    return out;
}

int Library::warmAll() {
    int total = 0, shapes_ = 0, failed = 0;
    for (const auto& n : names_) {
        const auto& s = shapes(n);
        if (s.empty()) failed++;
        total++;
        shapes_ += (int)s.size();
    }
    char buf[160];
    snprintf(buf, sizeof(buf), "图标预解析：%d 个，图形 %d 个，失败 %d 个", total,
             shapes_, failed);
    plat::logWrite("INFO", buf);
    return total;
}

// ---------------------------------------------------------------- 绘制

namespace {

// SVG 设计尺寸是 24x24，按目标边长缩放
const float kDesignSize = 24.0f;

// 描边宽度。设计稿是 24x24 上 2px，缩放后：
//
//   图标 16px → k=0.667 → 2*0.667 = 1.33 → 1
//   图标 18px → k=0.75  → 1.50        → 2（但不能真给 2，见下）
//   图标 24px → k=1.0   → 2.0         → 2
//
// 1px 在 24 格的设计稿上就是把一条 2 格的线压成 1 格。这是
// **可以接受**的：真实 IDE 的小图标也是这个视觉密度。
// 关键在于线本身不能丢（见下面 drawPolyline 的说明）。
float strokeOf(float k, float strokeScale) {
    float t = 2.0f * k * strokeScale;
    if (t < 0.85f) t = 0.85f;   // 下限：再细也保证能画出来
    return t;
}

}  // namespace

// 一条折线。**这里不能用整数取整**。
//
// 原来的写法是把每个点的 x/y 各自 (int)(v*k+0.5f) 取整再交给
// drawLine。16px 的图标 k≈0.67，一个「M4 6 L4 18」的竖线两端
// 映射出来只差 8 个像素，还行；但像 chevron 这种
// 「M9 6 L15 12 L9 18」，三个点取整后可能变成
// (6,4) (10,8) (6,12) 甚至重合，中间那一段就退化成零长度 ——
// 表现就是**图标整个不见或者缺一半**。
//
// 平台层的 drawLine 内部是浮点光栅化（aaLine），它自己会做
// 抗锯齿，所以传浮点进去是正确用法，不用我们提前取整。
static void drawPolyline(Canvas& c, const std::vector<float>& pts, bool closed,
                         float ox, float oy, float k, Color color,
                         float thickness) {
    if (pts.size() < 4) return;

    auto px = [&](size_t i) { return ox + pts[i] * k; };
    auto py = [&](size_t i) { return oy + pts[i + 1] * k; };

    for (size_t i = 0; i + 3 < pts.size(); i += 2) {
        plat::drawLineF(c, px(i), py(i), px(i + 2), py(i + 3), color, thickness);
    }
    if (closed) {
        size_t n = pts.size();
        plat::drawLineF(c, px(n - 2), py(n - 1), px(0), py(1), color, thickness);
    }
}

void draw(Canvas& c, const std::vector<Shape>& shapes, int x, int y, int size,
          Color color, float strokeScale) {
    if (shapes.empty() || size <= 0) return;

    float k = (float)size / kDesignSize;
    float thickness = strokeOf(k, strokeScale);

    // 图标在设计稿里通常会留 1~2 格内边距，缩放时这圈留白
    // 也跟着缩，但为了让小图标看起来「饱满」一点，把整条路径
    // 往中心挤一点点。0.5 像素级的调整，视觉上更接近 IDEA。
    float inset = (size >= 20) ? 0.0f : 0.5f;
    float ox = (float)x + inset;
    float oy = (float)y + inset;

    for (const auto& s : shapes) {
        switch (s.kind) {
            case ShapeKind::Path:
            case ShapeKind::Polyline:
            case ShapeKind::Polygon:
                drawPolyline(c, s.pts, s.closed || s.kind == ShapeKind::Polygon,
                             ox, oy, k, color, thickness);
                break;

            case ShapeKind::Line: {
                if (s.pts.size() < 4) break;
                plat::drawLineF(c, ox + s.pts[0] * k, oy + s.pts[1] * k,
                                ox + s.pts[2] * k, oy + s.pts[3] * k, color,
                                thickness);
                break;
            }

            case ShapeKind::Circle: {
                // 圆用 40 段折线。段数少了小圆看着像多边形，
                // 多了在这个尺寸上也没区别。
                const int seg = 40;
                float cx = ox + s.cx * k;
                float cy = oy + s.cy * k;
                float r = s.r * k;
                float prevX = cx + r, prevY = cy;
                for (int i = 1; i <= seg; i++) {
                    float a = 6.2831853f * i / seg;
                    float nx = cx + r * cosf(a);
                    float ny = cy + r * sinf(a);
                    plat::drawLineF(c, prevX, prevY, nx, ny, color, thickness);
                    prevX = nx;
                    prevY = ny;
                }
                break;
            }

            case ShapeKind::Rect: {
                float x0 = ox + s.cx * k;
                float y0 = oy + s.cy * k;
                float rw = s.rw * k;
                float rh = s.rh * k;
                float rr = s.rx * k;
                if (rr > 0.5f) {
                    plat::strokeRoundRectF(c, x0, y0, rw, rh, rr, color,
                                           thickness);
                } else {
                    plat::drawLineF(c, x0, y0, x0 + rw, y0, color, thickness);
                    plat::drawLineF(c, x0 + rw, y0, x0 + rw, y0 + rh, color,
                                    thickness);
                    plat::drawLineF(c, x0 + rw, y0 + rh, x0, y0 + rh, color,
                                    thickness);
                    plat::drawLineF(c, x0, y0 + rh, x0, y0, color, thickness);
                }
                break;
            }
        }
    }
}

}  // namespace icons
}  // namespace spp
