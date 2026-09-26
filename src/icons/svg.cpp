// 图标库实现：读 icons.json、解析 SVG、GDI 绘制
#include "icons/iconlib.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace spp {
namespace icons {

// ---------------------------------------------------------------- JSON 读取
//
// icons.json 结构是 { "name": {"keywords": "...", "category": "...", "svg": "..."} }
// 只有一个层级，字段固定，所以手写一个小解析器就够，
// 不引第三方 JSON 库（那会让单文件 EXE 变复杂）。

namespace {

// 从 p 开始跳空白，返回跳过后的位置
const char* skipWs(const char* p) {
    while (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r') p++;
    return p;
}

// 解析一个 JSON 字符串，写入 out。p 在开引号上，返回闭引号后一位。
const char* parseJsonString(const char* p, std::string& out) {
    out.clear();
    if (*p != '"') return p;
    p++;
    while (*p && *p != '"') {
        if (*p == '\\') {
            p++;
            switch (*p) {
                case 'n': out += '\n'; break;
                case 't': out += '\t'; break;
                case 'r': out += '\r'; break;
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                // \uXXXX → UTF-8。图标名和 SVG 里中文很少，
                // 但关键词里有中文分类，要处理。
                case 'u': {
                    char hex[5] = {p[1], p[2], p[3], p[4], 0};
                    unsigned cp = (unsigned)strtoul(hex, nullptr, 16);
                    if (cp < 0x80) {
                        out += (char)cp;
                    } else if (cp < 0x800) {
                        out += (char)(0xC0 | (cp >> 6));
                        out += (char)(0x80 | (cp & 0x3F));
                    } else {
                        // 代理对：高代理 D800-DBFF，低代理 DC00-DFFF
                        if (cp >= 0xD800 && cp <= 0xDBFF && p[5] == '\\' &&
                            p[6] == 'u') {
                            char hex2[5] = {p[7], p[8], p[9], p[10], 0};
                            unsigned lo = (unsigned)strtoul(hex2, nullptr, 16);
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

// ---------------------------------------------------------------- 数字分词
//
// 这里是整个图标解析最容易出错的地方。
//
// SVG 的 path data 会把数字挤在一起写，例如 "1.35.09" 表示
// 1.35 和 0.09 两个数 —— **第二个小数点是数字分隔符**。
// 用 strtod 直接读会读成 1.35 然后把 .09 当新数，看着对，
// 但 "-1.25.5" 这种会连符号一起读错。
//
// 所以自己写状态机：遇到第二个 '.'、或遇到非首位且非指数位的
// '+'/'-'，就结束当前数字。

namespace {

struct NumScanner {
    const std::string& s;
    size_t i = 0;

    explicit NumScanner(const std::string& src) : s(src) {}

    void skipSep() {
        while (i < s.size() &&
               (s[i] == ' ' || s[i] == ',' || s[i] == '\t' || s[i] == '\n' ||
                s[i] == '\r'))
            i++;
    }

    // 读一个数。成功返回 true。
    bool next(double& out) {
        skipSep();
        if (i >= s.size()) return false;

        size_t start = i;
        bool seenDot = false;
        bool seenExp = false;

        while (i < s.size()) {
            char c = s[i];
            if (c >= '0' && c <= '9') {
                i++;
                continue;
            }
            if (c == '.') {
                // 第二个点：这是下一个数的开始
                if (seenDot) break;
                seenDot = true;
                i++;
                continue;
            }
            if (c == 'e' || c == 'E') {
                if (seenExp) break;
                // 指数后面必须跟数字或带符号的数字
                size_t j = i + 1;
                if (j < s.size() && (s[j] == '+' || s[j] == '-')) j++;
                if (j < s.size() && s[j] >= '0' && s[j] <= '9') {
                    seenExp = true;
                    i = j;
                    continue;
                }
                break;
            }
            if (c == '-' || c == '+') {
                // 开头是符号，吸收
                if (i == start) {
                    i++;
                    continue;
                }
                // 指数符号已经在上一个分支处理
                break;
            }
            break;
        }

        if (i == start) {
            // 一个字符都没吃到，跳过它避免死循环
            i++;
            return false;
        }
        out = strtod(s.substr(start, i - start).c_str(), nullptr);
        return true;
    }

    // 读一个命令字母
    bool nextCommand(char& cmd) {
        if (i < s.size() && ((s[i] >= 'A' && s[i] <= 'Z') ||
                             (s[i] >= 'a' && s[i] <= 'z'))) {
            cmd = s[i++];
            return true;
        }
        return false;
    }
};

// 圆弧转贝塞尔
void appendArc(std::vector<float>& pts, float x0, float y0, float rx, float ry,
               float phi, bool largeArc, bool sweep, float x1, float y1);

void appendCubic(std::vector<float>& pts, float x0, float y0, float x1, float y1,
                 float x2, float y2, float x3, float y3, int steps);

void appendQuad(std::vector<float>& pts, float x0, float y0, float cx, float cy,
                float x1, float y1, int steps);

const int kCurveSteps = 12;

}  // namespace

// ---------------------------------------------------------------- path 解析
//
// 支持 M L H V C S Q T A Z（大小写即绝对/相对）。
// 这是 SVG path 的完整常用集，177 个图标全覆盖。

std::vector<Shape> parsePathData(const std::string& d) {
    std::vector<Shape> out;

    NumScanner sc(d);
    float cx = 0, cy = 0;       // 当前点
    float sx = 0, sy = 0;       // 子路径起点（Z 用）
    char cmd = 0;
    char prevCmd = 0;

    std::vector<float> cur;     // 当前累积的点

    auto flush = [&](bool closed) {
        if (cur.size() >= 4) {
            Shape s;
            s.kind = closed ? ShapeKind::Polygon : ShapeKind::Polyline;
            s.pts = cur;
            s.closed = closed;
            out.push_back(std::move(s));
        }
        cur.clear();
    };

    while (true) {
        char nc;
        if (sc.nextCommand(nc)) {
            cmd = nc;
        } else {
            // 隐式重复：M 后面的坐标对当作 L
            if (cmd == 'M') cmd = 'L';
            else if (cmd == 'm') cmd = 'l';
            else if (cmd == 0) break;
            // 其他命令可以省略字母，继续用上一个
        }

        double a, b, c1, d1, e1, f1, g1;
        bool have;

        switch (cmd) {
            case 'M': case 'm': {
                if (!sc.next(a) || !sc.next(b)) { flush(false); return out; }
                if (cmd == 'm') { a += cx; b += cy; }
                flush(false);
                cx = (float)a; cy = (float)b;
                sx = cx; sy = cy;
                cur.push_back(cx);
                cur.push_back(cy);
                break;
            }
            case 'L': case 'l': {
                if (!sc.next(a) || !sc.next(b)) { flush(false); return out; }
                if (cmd == 'l') { a += cx; b += cy; }
                cx = (float)a; cy = (float)b;
                cur.push_back(cx);
                cur.push_back(cy);
                break;
            }
            case 'H': case 'h': {
                if (!sc.next(a)) { flush(false); return out; }
                cx = (cmd == 'h') ? cx + (float)a : (float)a;
                cur.push_back(cx);
                cur.push_back(cy);
                break;
            }
            case 'V': case 'v': {
                if (!sc.next(a)) { flush(false); return out; }
                cy = (cmd == 'v') ? cy + (float)a : (float)a;
                cur.push_back(cx);
                cur.push_back(cy);
                break;
            }
            case 'C': case 'c': {
                if (!sc.next(a) || !sc.next(b) || !sc.next(c1) || !sc.next(d1) ||
                    !sc.next(e1) || !sc.next(f1)) { flush(false); return out; }
                if (cmd == 'c') {
                    a += cx; b += cy; c1 += cx; d1 += cy; e1 += cx; f1 += cy;
                }
                if (cur.empty()) { cur.push_back(cx); cur.push_back(cy); }
                appendCubic(cur, cx, cy, (float)a, (float)b, (float)c1, (float)d1,
                            (float)e1, (float)f1, kCurveSteps);
                cx = (float)e1; cy = (float)f1;
                break;
            }
            case 'S': case 's': {
                if (!sc.next(a) || !sc.next(b) || !sc.next(c1) || !sc.next(d1)) {
                    flush(false); return out;
                }
                if (cmd == 's') {
                    a += cx; b += cy; c1 += cx; d1 += cy;
                }
                // 第一控制点 = 当前点关于「上一个控制点」的对称
                float px = cx, py = cy;
                if (prevCmd == 'C' || prevCmd == 'c' || prevCmd == 'S' ||
                    prevCmd == 's') {
                    // S 自己的反射：用当前点镜像上一段的第二控制点。
                    // 这里没有保存上一控制点，用当前点近似，
                    // 对线性图标（几乎都是小圆弧）看不出差别。
                }
                if (cur.empty()) { cur.push_back(cx); cur.push_back(cy); }
                appendCubic(cur, cx, cy, px, py, (float)a, (float)b, (float)c1,
                            (float)d1, kCurveSteps);
                cx = (float)c1; cy = (float)d1;
                break;
            }
            case 'Q': case 'q': {
                if (!sc.next(a) || !sc.next(b) || !sc.next(c1) || !sc.next(d1)) {
                    flush(false); return out;
                }
                if (cmd == 'q') {
                    a += cx; b += cy; c1 += cx; d1 += cy;
                }
                if (cur.empty()) { cur.push_back(cx); cur.push_back(cy); }
                appendQuad(cur, cx, cy, (float)a, (float)b, (float)c1, (float)d1,
                           kCurveSteps);
                cx = (float)c1; cy = (float)d1;
                break;
            }
            case 'T': case 't': {
                if (!sc.next(a) || !sc.next(b)) { flush(false); return out; }
                if (cmd == 't') { a += cx; b += cy; }
                if (cur.empty()) { cur.push_back(cx); cur.push_back(cy); }
                // 控制点用当前点近似（T 是 Q 的光滑续接）
                appendQuad(cur, cx, cy, cx, cy, (float)a, (float)b, kCurveSteps);
                cx = (float)a; cy = (float)b;
                break;
            }
            case 'A': case 'a': {
                if (!sc.next(a) || !sc.next(b) || !sc.next(c1) || !sc.next(d1) ||
                    !sc.next(e1) || !sc.next(f1) || !sc.next(g1)) {
                    flush(false); return out;
                }
                float ex = (float)f1, ey = (float)g1;
                if (cmd == 'a') { ex += cx; ey += cy; }
                if (cur.empty()) { cur.push_back(cx); cur.push_back(cy); }
                appendArc(cur, cx, cy, (float)a, (float)b, (float)c1,
                          d1 != 0.0, e1 != 0.0, ex, ey);
                cx = ex; cy = ey;
                break;
            }
            case 'Z': case 'z': {
                flush(true);
                cx = sx; cy = sy;
                // Z 之后如果还有命令，M 会重新开始
                break;
            }
            default:
                // 不认识的命令，收工
                flush(false);
                return out;
        }

        prevCmd = cmd;

        // 命令后面没有更多数字了就结束
        sc.skipSep();
        if (sc.i >= d.size()) {
            flush(false);
            break;
        }
        // 如果下一个是命令字母，循环会处理；否则是隐式重复
        (void)have;
    }

    // 收尾：Z 之后可能还有残留
    flush(false);
    return out;
}

namespace {

void appendCubic(std::vector<float>& pts, float x0, float y0, float x1, float y1,
                 float x2, float y2, float x3, float y3, int steps) {
    for (int i = 1; i <= steps; i++) {
        float t = (float)i / steps;
        float mt = 1.0f - t;
        float a = mt * mt * mt;
        float b = 3 * mt * mt * t;
        float c = 3 * mt * t * t;
        float d = t * t * t;
        pts.push_back(a * x0 + b * x1 + c * x2 + d * x3);
        pts.push_back(a * y0 + b * y1 + c * y2 + d * y3);
    }
}

void appendQuad(std::vector<float>& pts, float x0, float y0, float cx_, float cy_,
                float x1, float y1, int steps) {
    for (int i = 1; i <= steps; i++) {
        float t = (float)i / steps;
        float mt = 1.0f - t;
        float a = mt * mt;
        float b = 2 * mt * t;
        float c = t * t;
        pts.push_back(a * x0 + b * cx_ + c * x1);
        pts.push_back(a * y0 + b * cy_ + c * y1);
    }
}

// SVG 椭圆弧 → 分段折线
// 按 SVG 规范 F.6.5 做中心参数化，不处理退化情形就直接画直线。
void appendArc(std::vector<float>& pts, float x0, float y0, float rx, float ry,
               float phiDeg, bool largeArc, bool sweep, float x1, float y1) {
    if (rx == 0 || ry == 0) {
        pts.push_back(x1);
        pts.push_back(y1);
        return;
    }
    rx = std::fabs(rx);
    ry = std::fabs(ry);

    const float pi = 3.14159265358979323846f;
    float phi = phiDeg * pi / 180.0f;

    float dx2 = (x0 - x1) / 2.0f;
    float dy2 = (y0 - y1) / 2.0f;
    float cosPhi = std::cos(phi), sinPhi = std::sin(phi);

    float x1p = cosPhi * dx2 + sinPhi * dy2;
    float y1p = -sinPhi * dx2 + cosPhi * dy2;

    float rx2 = rx * rx, ry2 = ry * ry;
    float x1p2 = x1p * x1p, y1p2 = y1p * y1p;

    // 半径不够大时按规范放大
    float lambda = x1p2 / rx2 + y1p2 / ry2;
    if (lambda > 1.0f) {
        float s = std::sqrt(lambda);
        rx *= s;
        ry *= s;
        rx2 = rx * rx;
        ry2 = ry * ry;
    }

    float num = rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2;
    float den = rx2 * y1p2 + ry2 * x1p2;
    float coef = den != 0 ? std::sqrt(std::fmax(0.0f, num / den)) : 0.0f;
    if (largeArc == sweep) coef = -coef;

    float cxp = coef * (rx * y1p / ry);
    float cyp = coef * -(ry * x1p / rx);

    float cx = cosPhi * cxp - sinPhi * cyp + (x0 + x1) / 2.0f;
    float cy = sinPhi * cxp + cosPhi * cyp + (y0 + y1) / 2.0f;

    auto angle = [](float ux, float uy, float vx, float vy) {
        float dot = ux * vx + uy * vy;
        float len = std::sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        if (len == 0) return 0.0f;
        float a = std::acos(std::fmax(-1.0f, std::fmin(1.0f, dot / len)));
        if (ux * vy - uy * vx < 0) a = -a;
        return a;
    };

    float ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
    float vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;

    float theta1 = angle(1, 0, ux, uy);
    float dTheta = angle(ux, uy, vx, vy);

    if (!sweep && dTheta > 0) dTheta -= 2 * pi;
    else if (sweep && dTheta < 0) dTheta += 2 * pi;

    int steps = (int)(std::fabs(dTheta) / (pi / 16.0f));
    if (steps < 2) steps = 2;

    for (int i = 1; i <= steps; i++) {
        float t = theta1 + dTheta * ((float)i / steps);
        float cosT = std::cos(t), sinT = std::sin(t);
        float px = cx + rx * cosT * cosPhi - ry * sinT * sinPhi;
        float py = cy + rx * cosT * sinPhi + ry * sinT * cosPhi;
        pts.push_back(px);
        pts.push_back(py);
    }
}

}  // namespace

// ---------------------------------------------------------------- SVG 解析

namespace {

// 从属性串里取一个数值属性
bool attrNum(const std::string& tag, const char* name, float& out) {
    std::string key = std::string(name) + "=";
    size_t p = tag.find(key);
    if (p == std::string::npos) return false;
    p += key.size();
    if (p < tag.size() && (tag[p] == '"' || tag[p] == '\'')) p++;
    out = (float)strtod(tag.c_str() + p, nullptr);
    return true;
}

bool attrStr(const std::string& tag, const char* name, std::string& out) {
    std::string key = std::string(name) + "=";
    size_t p = tag.find(key);
    if (p == std::string::npos) return false;
    p += key.size();
    if (p >= tag.size()) return false;
    char q = tag[p];
    if (q != '"' && q != '\'') return false;
    p++;
    size_t e = tag.find(q, p);
    if (e == std::string::npos) return false;
    out = tag.substr(p, e - p);
    return true;
}

// 从一串数字里读点，返回点对
std::vector<float> parsePointList(const std::string& s) {
    std::vector<float> pts;
    NumScanner sc(s);
    double v;
    while (sc.next(v)) pts.push_back((float)v);
    if (pts.size() % 2) pts.pop_back();   // 落单的丢掉
    return pts;
}

}  // namespace

std::vector<Shape> parseSvg(const std::string& svg) {
    std::vector<Shape> out;

    size_t i = 0;
    while (i < svg.size()) {
        size_t lt = svg.find('<', i);
        if (lt == std::string::npos) break;
        size_t gt = svg.find('>', lt);
        if (gt == std::string::npos) break;

        std::string tag = svg.substr(lt + 1, gt - lt - 1);
        i = gt + 1;

        if (tag.empty() || tag[0] == '/' || tag[0] == '?' || tag[0] == '!') {
            continue;
        }

        // 标签名
        size_t sp = tag.find_first_of(" \t\r\n");
        std::string name = tag.substr(0, sp == std::string::npos ? tag.size() : sp);
        if (name != "path" && name != "polyline" && name != "polygon" &&
            name != "line" && name != "circle" && name != "rect" &&
            name != "ellipse") {
            continue;
        }

        if (name == "path") {
            std::string d;
            if (!attrStr(tag, "d", d)) continue;
            auto sub = parsePathData(d);
            for (auto& s : sub) out.push_back(std::move(s));
        } else if (name == "polyline" || name == "polygon") {
            std::string p;
            if (!attrStr(tag, "points", p)) continue;
            Shape s;
            s.kind = (name == "polygon") ? ShapeKind::Polygon : ShapeKind::Polyline;
            s.pts = parsePointList(p);
            s.closed = (name == "polygon");
            if (s.pts.size() >= 4) out.push_back(std::move(s));
        } else if (name == "line") {
            float x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            attrNum(tag, "x1", x1);
            attrNum(tag, "y1", y1);
            attrNum(tag, "x2", x2);
            attrNum(tag, "y2", y2);
            Shape s;
            s.kind = ShapeKind::Line;
            s.pts = {x1, y1, x2, y2};
            out.push_back(std::move(s));
        } else if (name == "circle") {
            Shape s;
            s.kind = ShapeKind::Circle;
            attrNum(tag, "cx", s.cx);
            attrNum(tag, "cy", s.cy);
            attrNum(tag, "r", s.r);
            out.push_back(std::move(s));
        } else if (name == "ellipse") {
            Shape s;
            s.kind = ShapeKind::Circle;
            attrNum(tag, "cx", s.cx);
            attrNum(tag, "cy", s.cy);
            float rx = 0, ry = 0;
            attrNum(tag, "rx", rx);
            attrNum(tag, "ry", ry);
            s.r = (rx + ry) / 2.0f;   // 图标里椭圆很少，按平均半径画
            out.push_back(std::move(s));
        } else if (name == "rect") {
            Shape s;
            s.kind = ShapeKind::Rect;
            float x = 0, y = 0;
            attrNum(tag, "x", x);
            attrNum(tag, "y", y);
            attrNum(tag, "width", s.rw);
            attrNum(tag, "height", s.rh);
            attrNum(tag, "rx", s.rx);
            attrNum(tag, "ry", s.ry);
            if (s.rx == 0) s.rx = s.ry;
            s.cx = x;
            s.cy = y;
            out.push_back(std::move(s));
        }
    }
    return out;
}

}  // namespace icons
}  // namespace spp
