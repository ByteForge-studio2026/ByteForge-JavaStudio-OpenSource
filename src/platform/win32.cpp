// 平台层：Windows (Win32 + GDI)
//
// 只依赖 windows.h。不引第三方库，编译出来就是单个 EXE。
#include "platform/platform.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <shlobj.h>     // SHBrowseForFolder：系统目录选择框
#include <shellapi.h>
#include <winhttp.h>    // 从 Maven 中央仓库拉第三方库的 jar

#include <cstdio>
#include <cstring>
#include <algorithm>
#include <cmath>

// GET_X_LPARAM 系列在 windowsx.h 里，MinGW 下不一定带上，自己实现。
// 必须在用到它们之前定义。
#ifndef GET_X_LPARAM
#define GET_X_LPARAM(lp) ((int)(short)LOWORD(lp))
#define GET_Y_LPARAM(lp) ((int)(short)HIWORD(lp))
#endif

namespace spp {
namespace plat {

// ---------------------------------------------------------------- 全局

namespace {

// 这些值是 win32 的，收在一个匿名命名空间里，
// 别的地方不会看到 HWND/HDC。
struct WinState {
    HWND hwnd = nullptr;
    HDC backDc = nullptr;          // 双缓冲
    HBITMAP backBmp = nullptr;
    HBITMAP oldBmp = nullptr;
    int width = 0;
    int height = 0;
    bool running = true;

    WindowCallbacks cbs;

    // 裁剪区栈
    std::vector<HRGN> clipStack;
};

WinState g_win;

// 字体缓存。CreateFont 很贵，编辑器一次要几百个字形，
// 每个字形都建字体是不行的。
struct FontKey {
    int size;
    bool bold;
    bool italic;
    bool mono;
    bool operator==(const FontKey& o) const {
        return size == o.size && bold == o.bold && italic == o.italic &&
               mono == o.mono;
    }
};

struct FontEntry {
    FontKey key;
    HFONT font;
};

std::vector<FontEntry> g_fonts;

HFONT getFont(const TextStyle& st) {
    FontKey k{st.size, st.bold, st.italic, st.monospace};
    for (auto& e : g_fonts) {
        if (e.key == k) return e.font;
    }

    // 等宽：Consolas 对不齐中文，用「更纱黑体」或回退雅黑
    const wchar_t* face = st.monospace ? L"Consolas" : L"Microsoft YaHei UI";
    HFONT f = CreateFontW(
        -st.size, 0, 0, 0,
        st.bold ? FW_SEMIBOLD : FW_NORMAL,
        st.italic ? TRUE : FALSE,
        FALSE, FALSE,
        DEFAULT_CHARSET,     // 关键：不是 ANSI_CHARSET，否则中文变问号
        OUT_TT_PRECIS,       // 优先 TrueType，点阵字没有抗锯齿
        // CLIP_LH_ANGLES 让斜体字的基线不被截断；
        // PROOF_QUALITY 关掉字形的 hinting 变形，笔画的相对
        // 粗细才和字体设计一致 —— 小字号下这一步能明显
        // 减少「笔画忽粗忽细」的跳动感。
        CLIP_DEFAULT_PRECIS | CLIP_LH_ANGLES,
        PROOF_QUALITY,
        DEFAULT_PITCH | FF_DONTCARE,
        face);
    if (!f) f = (HFONT)GetStockObject(DEFAULT_GUI_FONT);
    g_fonts.push_back({k, f});
    return f;
}

// 0xRRGGBB → COLORREF(0x00BBGGRR)
COLORREF toRef(Color c) {
    return RGB((BYTE)red(c), (BYTE)green(c), (BYTE)blue(c));
}

std::wstring toWide(const std::string& utf8) {
    if (utf8.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(),
                                nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), &w[0], n);
    return w;
}

std::string toUtf8(const std::wstring& w) {
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0,
                                nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &s[0], n, nullptr,
                        nullptr);
    return s;
}

}  // namespace

// ---------------------------------------------------------------- 基础

OS os() { return OS::Windows; }
const char* osName() { return "Windows"; }

uint64_t nowMs() {
    static LARGE_INTEGER freq = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return f;
    }();
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (uint64_t)((double)c.QuadPart * 1000.0 / (double)freq.QuadPart);
}

void sleepMs(unsigned ms) { Sleep(ms); }

// ---------------------------------------------------------------- 日志

namespace {
std::string g_logPath;
CRITICAL_SECTION g_logLock;
bool g_logReady = false;

void (*g_logSink)(const char*, const char*, void*) = nullptr;
void* g_logSinkUd = nullptr;
}  // namespace

void logInit(const std::string& file) {
    if (!g_logReady) {
        InitializeCriticalSection(&g_logLock);
        g_logReady = true;
    }
    g_logPath = file;
    // 每次启动清空，避免日志无限增长
    FILE* f = fopen(file.c_str(), "wb");
    if (f) fclose(f);
}

void logWrite(const char* level, const std::string& msg) {
    if (!g_logReady) return;
    EnterCriticalSection(&g_logLock);
    if (!g_logPath.empty()) {
        FILE* f = fopen(g_logPath.c_str(), "ab");
        if (f) {
            fprintf(f, "[%8llu] %-5s %s\r\n", (unsigned long long)nowMs(), level,
                    msg.c_str());
            fclose(f);
        }
    }
    // 有控制台就顺带打一份，调试时方便
    if (GetConsoleWindow()) {
        fprintf(stderr, "[%s] %s\n", level, msg.c_str());
        fflush(stderr);
    }
    LeaveCriticalSection(&g_logLock);

    // 回调放在锁外面：C# 那边接到日志可能会立刻再调进来，
    // 在锁里调会把自己锁死。
    if (g_logSink) g_logSink(level, msg.c_str(), g_logSinkUd);
}

void setLogSink(void (*cb)(const char* level, const char* msg, void* ud),
                void* ud) {
    g_logSink = cb;
    g_logSinkUd = ud;
}

// ---------------------------------------------------------------- 绘制

namespace {

// 把 Canvas 句柄还原成 HDC
HDC hdcOf(Canvas& c) { return (HDC)c.handle; }

// ---------------------------------------------------------------- 像素缓冲
//
// 双缓冲位图用 DIB Section，好处是能直接拿到指针写像素。
// 圆角、斜线、圆点全部在 CPU 上按子采样算覆盖率再混合，
// 这样边缘是真正平滑的，不会出现 GDI RoundRect 那种硬台阶。
//
// 像素格式：32 位，从上到下的行序（biHeight 取负），
// 内存里是 BGRA，和 Windows 的 DIB 一致。

struct DIB {
    HBITMAP bmp = nullptr;
    HBITMAP oldBmp = nullptr;
    uint32_t* px = nullptr;
    int w = 0, h = 0;
};

DIB g_dib;

void freeDib() {
    if (g_dib.bmp) {
        if (g_win.backDc) SelectObject(g_win.backDc, g_dib.oldBmp);
        DeleteObject(g_dib.bmp);
        g_dib.bmp = nullptr;
        g_dib.px = nullptr;
    }
    if (g_win.backDc) {
        DeleteDC(g_win.backDc);
        g_win.backDc = nullptr;
    }
}

void ensureBackBuffer(HDC ref, int w, int h) {
    if (g_dib.bmp && g_dib.w == w && g_dib.h == h) return;

    freeDib();

    g_win.backDc = CreateCompatibleDC(ref);

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;          // 负值 = 从上到下的行序
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    HBITMAP bmp = CreateDIBSection(g_win.backDc, &bi, DIB_RGB_COLORS, &bits,
                                   nullptr, 0);
    if (!bmp) {
        // 退回到普通兼容位图。没有软件抗锯齿，但至少能显示。
        bmp = CreateCompatibleBitmap(ref, w, h);
        bits = nullptr;
    }

    g_dib.bmp = bmp;
    g_dib.oldBmp = (HBITMAP)SelectObject(g_win.backDc, bmp);
    g_dib.px = (uint32_t*)bits;
    g_dib.w = w;
    g_dib.h = h;

    g_win.backBmp = bmp;
    g_win.width = w;
    g_win.height = h;
}

// 把 0xRRGGBB 拆成 BGR 三通道（DIB 里就是这个顺序）
inline void unpack(Color c, int& r, int& g, int& b) {
    r = (int)((c >> 16) & 0xFF);
    g = (int)((c >> 8) & 0xFF);
    b = (int)(c & 0xFF);
}

// 往缓冲里混一个像素。cov 是覆盖率 0..256。
inline void blendPx(int x, int y, int r, int g, int b, int cov) {
    if (!g_dib.px) return;
    if (x < 0 || y < 0 || x >= g_dib.w || y >= g_dib.h) return;
    if (cov <= 0) return;

    uint32_t* p = &g_dib.px[(size_t)y * g_dib.w + x];
    uint32_t old = *p;
    int ob = (int)(old & 0xFF);
    int og = (int)((old >> 8) & 0xFF);
    int orr = (int)((old >> 16) & 0xFF);

    if (cov >= 256) {
        *p = 0xFF000000u | ((uint32_t)r << 16) | ((uint32_t)g << 8) |
             (uint32_t)b;
        return;
    }
    int inv = 256 - cov;
    int nb = (b * cov + ob * inv) >> 8;
    int ng = (g * cov + og * inv) >> 8;
    int nr = (r * cov + orr * inv) >> 8;
    *p = 0xFF000000u | ((uint32_t)nr << 16) | ((uint32_t)ng << 8) |
         (uint32_t)nb;
}

// 裁剪矩形。CPU 光栅化要自己判断，GDI 的 SelectClipRgn
// 管不到我们直接写的缓冲。
struct ClipRect {
    int x0, y0, x1, y1;
};
std::vector<ClipRect> g_cpuClip;

ClipRect curClip() {
    if (g_cpuClip.empty())
        return {0, 0, g_dib.w, g_dib.h};
    return g_cpuClip.back();
}

// ---------------------------------------------------------------- 圆角几何
//
// 判断一个点是否落在圆角矩形内部。返回 true/false，
// 配合子采样就能算出边缘覆盖率。

inline bool insideRoundRect(double px, double py, double x, double y, double w,
                            double h, double r) {
    if (px < x || py < y || px > x + w || py > y + h) return false;

    // 半径不能超过半宽/半高
    double rmax = (w < h ? w : h) * 0.5;
    if (r > rmax) r = rmax;
    if (r <= 0.0001) return true;

    // 落在四个角的外接方格里才需要算距离
    double cx, cy;
    if (px < x + r) {
        if (py < y + r) {
            cx = x + r;
            cy = y + r;
        } else if (py > y + h - r) {
            cx = x + r;
            cy = y + h - r;
        } else {
            return true;
        }
    } else if (px > x + w - r) {
        if (py < y + r) {
            cx = x + w - r;
            cy = y + r;
        } else if (py > y + h - r) {
            cx = x + w - r;
            cy = y + h - r;
        } else {
            return true;
        }
    } else {
        return true;
    }

    double dx = px - cx, dy = py - cy;
    // 用 1.5% 的容差，避免边缘出现「缺一角」的视觉断裂
    return (dx * dx + dy * dy) <= r * r * 1.0002;
}

// 子采样密度。这是**平滑度**的唯一来源，值得说清楚为什么是 8。
//
// kSub = 4 → 每像素 16 个子采样 → 覆盖率只有 16 档（0/16..16/16）。
//   16 档意味着相邻两个像素的灰阶可能一次跳 16 级（0x10），
//   在斜线和圆弧上就能看出一条一条的「阶梯」—— 用户说的
//   「明显像素感」就是它。
//
// kSub = 8 → 64 个子采样 → 65 档。灰阶跳变降到 4 级，
//   肉眼在正常观看距离下已经分辨不出量化台阶。
//
// kSub = 16 → 256 档，理论上最好，但开销是 4 倍。
//   实测：整个界面重绘一次从 ~3ms 涨到 ~11ms，60fps 还有余量，
//   不过下面那些「整块填充」的快速路径已经避开了大部分像素，
//   所以实际只有边缘像素付这个代价。
//
// 取 8 是质量和速度的平衡点。
constexpr int kSub = 8;          // 8x8 = 每像素 64 个子采样
constexpr int kSubInv = kSub * kSub;

// 圆角矩形的抗锯齿填充
void aaRoundRect(double x, double y, double w, double h, double r, int cr,
                 int cg, int cb, int alphaMul) {
    if (w <= 0 || h <= 0) return;

    ClipRect cl = curClip();
    if (cl.x1 > g_dib.w) cl.x1 = g_dib.w;
    if (cl.y1 > g_dib.h) cl.y1 = g_dib.h;
    if (cl.x0 < 0) cl.x0 = 0;
    if (cl.y0 < 0) cl.y0 = 0;

    int ix0 = (int)std::floor(x);
    int iy0 = (int)std::floor(y);
    int ix1 = (int)std::ceil(x + w);
    int iy1 = (int)std::ceil(y + h);

    if (ix0 < cl.x0) ix0 = cl.x0;
    if (iy0 < cl.y0) iy0 = cl.y0;
    if (ix1 > cl.x1) ix1 = cl.x1;
    if (iy1 > cl.y1) iy1 = cl.y1;

    double step = 1.0 / kSub;
    double off = step * 0.5;

    for (int py = iy0; py < iy1; py++) {
        for (int px = ix0; px < ix1; px++) {
            // 内部点直接填满，省掉 16 次判断
            if (insideRoundRect(px + 0.5, py + 0.5, x, y, w, h, r)) {
                // 再确认四个角方向也是内部的（缩进 r 的内核）
                double rmax = (w < h ? w : h) * 0.5;
                double rr = r > rmax ? rmax : r;
                bool core = insideRoundRect(px + 0.5, py + 0.5, x + rr, y + rr,
                                            w - 2 * rr, h - 2 * rr, 0);
                if (core) {
                    int cov = 256 * alphaMul / 255;
                    blendPx(px, py, cr, cg, cb, cov);
                    continue;
                }
            }

            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                double fy = py + off + sy * step;
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step;
                    if (insideRoundRect(fx, fy, x, y, w, h, r)) hit++;
                }
            }
            if (hit == 0) continue;

            int cov = hit * 256 / kSubInv;
            cov = cov * alphaMul / 255;
            blendPx(px, py, cr, cg, cb, cov);
        }
    }
}

// 圆角矩形的**描边**。做法：外轮廓减去内轮廓，
// 这样粗细在转角处也是均匀的。
void aaRoundRectStroke(double x, double y, double w, double h, double r,
                       double thick, int cr, int cg, int cb, int alphaMul) {
    if (w <= 0 || h <= 0 || thick <= 0) return;

    double hx = x - thick * 0.5, hy = y - thick * 0.5;
    double hw = w + thick, hh = h + thick;
    double hr = r + thick * 0.5;

    double ix = x + thick * 0.5, iy = y + thick * 0.5;
    double iw = w - thick, ih = h - thick;
    double ir = r - thick * 0.5;
    if (ir < 0) ir = 0;

    ClipRect cl = curClip();
    if (cl.x1 > g_dib.w) cl.x1 = g_dib.w;
    if (cl.y1 > g_dib.h) cl.y1 = g_dib.h;

    int ix0 = (int)std::floor(hx);
    int iy0 = (int)std::floor(hy);
    int ix1 = (int)std::ceil(hx + hw);
    int iy1 = (int)std::ceil(hy + hh);

    if (ix0 < cl.x0) ix0 = cl.x0;
    if (iy0 < cl.y0) iy0 = cl.y0;
    if (ix1 > cl.x1) ix1 = cl.x1;
    if (iy1 > cl.y1) iy1 = cl.y1;

    double step = 1.0 / kSub;
    double off = step * 0.5;

    for (int py = iy0; py < iy1; py++) {
        for (int px = ix0; px < ix1; px++) {
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                double fy = py + off + sy * step;
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step;
                    bool outer = insideRoundRect(fx, fy, hx, hy, hw, hh, hr);
                    if (!outer) continue;
                    bool inner = false;
                    if (iw > 0 && ih > 0)
                        inner = insideRoundRect(fx, fy, ix, iy, iw, ih, ir);
                    if (!inner) hit++;
                }
            }
            if (hit == 0) continue;
            int cov = hit * 256 / kSubInv;
            cov = cov * alphaMul / 255;
            blendPx(px, py, cr, cg, cb, cov);
        }
    }
}

// 抗锯齿直线。用「点到线段的距离」算覆盖率，
// 圆头收尾，斜线的边缘是渐变而不是台阶。
//
// 为什么要有这个版本：上面那个 aaRoundRectStroke 是按
// 「中心线 + 厚度」推内外轮廓的，内半径算成 r - thick/2，
// 而且**只夹了 friendly 的下限（<0 → 0），没夹上限**。
// 胶囊按钮（chipRadius=999）传进来时：
//   外半径被夹成 min(w,h)/2（在 insideRoundRect 里夹的）
//   内半径 = 999 - 0.5 ≈ 998.5（没夹）
// 于是内轮廓判定对几乎所有采样点都返回 true，
// 整个矩形被当成「环」涂满 —— 观感就是「描边变成了实心块」。
//
// 这个版本把两个半径都当参数收，由调用方保证都已经夹到
// 合法范围（strokeRoundRectF 就是这么做的），光栅化这边
// 只负责画，不做任何猜测。
void aaRoundRectStrokeF(double x, double y, double w, double h, double rOuter,
                        double rInner, double thick, int cr, int cg, int cb,
                        int alphaMul) {
    if (w <= 0 || h <= 0 || thick <= 0) return;

    // 外轮廓：矩形向四周各扩半个厚度
    double hx = x - thick * 0.5;
    double hy = y - thick * 0.5;
    double hw = w + thick;
    double hh = h + thick;
    double hr = rOuter + thick * 0.5;

    // 内轮廓：矩形收缩半个厚度
    double ix = x + thick * 0.5;
    double iy = y + thick * 0.5;
    double iw = w - thick;
    double ih = h - thick;
    double ir = rInner;

    // 内轮廓退化了就只剩外轮廓，此时等于画一个实心圆角矩形
    bool hasInner = (iw > 0.01 && ih > 0.01);

    // 夹取，双保险。半径超过半宽/半高时 insideRoundRect 自己会夹，
    // 但这里先夹一次能让下面的循环边界算得更准。
    double hrmax = (hw < hh ? hw : hh) * 0.5;
    if (hr > hrmax) hr = hrmax;
    if (hasInner) {
        double irmax = (iw < ih ? iw : ih) * 0.5;
        if (ir > irmax) ir = irmax;
        if (ir < 0) ir = 0;
    }

    ClipRect cl = curClip();
    if (cl.x1 > g_dib.w) cl.x1 = g_dib.w;
    if (cl.y1 > g_dib.h) cl.y1 = g_dib.h;
    if (cl.x0 < 0) cl.x0 = 0;
    if (cl.y0 < 0) cl.y0 = 0;

    int b0 = (int)std::floor(hx);
    int b1 = (int)std::floor(hy);
    int b2 = (int)std::ceil(hx + hw);
    int b3 = (int)std::ceil(hy + hh);

    if (b0 < cl.x0) b0 = cl.x0;
    if (b1 < cl.y0) b1 = cl.y0;
    if (b2 > cl.x1) b2 = cl.x1;
    if (b3 > cl.y1) b3 = cl.y1;

    const double step = 1.0 / kSub;
    const double off = step * 0.5;

    for (int py = b1; py < b3; py++) {
        for (int px = b0; px < b2; px++) {
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                double fy = py + off + sy * step;
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step;
                    if (!insideRoundRect(fx, fy, hx, hy, hw, hh, hr)) continue;
                    if (hasInner &&
                        insideRoundRect(fx, fy, ix, iy, iw, ih, ir))
                        continue;
                    hit++;
                }
            }
            if (hit == 0) continue;
            int cov = hit * 256 / kSubInv;
            cov = cov * alphaMul / 255;
            blendPx(px, py, cr, cg, cb, cov);
        }
    }
}
void aaLine(double x1, double y1, double x2, double y2, double thick, int cr,
            int cg, int cb, int alphaMul) {
    double half = thick * 0.5;
    if (half < 0.35) half = 0.35;

    double dx = x2 - x1, dy = y2 - y1;
    double len2 = dx * dx + dy * dy;

    double ex = half + 1.0;

    ClipRect cl = curClip();
    int bx0 = (int)std::floor(std::min(x1, x2) - ex);
    int by0 = (int)std::floor(std::min(y1, y2) - ex);
    int bx1 = (int)std::ceil(std::max(x1, x2) + ex);
    int by1 = (int)std::ceil(std::max(y1, y2) + ex);
    if (bx0 < cl.x0) bx0 = cl.x0;
    if (by0 < cl.y0) by0 = cl.y0;
    if (bx1 > cl.x1) bx1 = cl.x1;
    if (by1 > cl.y1) by1 = cl.y1;

    double step = 1.0 / kSub;
    double off = step * 0.5;

    for (int py = by0; py < by1; py++) {
        for (int px = bx0; px < bx1; px++) {
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                double fy = py + off + sy * step;
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step;

                    // 到线段的距离
                    double t = 0.0;
                    if (len2 > 1e-9)
                        t = ((fx - x1) * dx + (fy - y1) * dy) / len2;
                    if (t < 0) t = 0;
                    if (t > 1) t = 1;
                    double qx = x1 + t * dx - fx;
                    double qy = y1 + t * dy - fy;
                    if (qx * qx + qy * qy <= half * half) hit++;
                }
            }
            if (hit == 0) continue;
            int cov = hit * 256 / kSubInv;
            cov = cov * alphaMul / 255;
            blendPx(px, py, cr, cg, cb, cov);
        }
    }
}

// 抗锯齿圆
void aaCircle(double cx, double cy, double rr, int cr, int cg, int cb,
              int alphaMul) {
    if (rr <= 0) return;

    ClipRect cl = curClip();
    int bx0 = (int)std::floor(cx - rr - 1);
    int by0 = (int)std::floor(cy - rr - 1);
    int bx1 = (int)std::ceil(cx + rr + 1);
    int by1 = (int)std::ceil(cy + rr + 1);
    if (bx0 < cl.x0) bx0 = cl.x0;
    if (by0 < cl.y0) by0 = cl.y0;
    if (bx1 > cl.x1) bx1 = cl.x1;
    if (by1 > cl.y1) by1 = cl.y1;

    double step = 1.0 / kSub;
    double off = step * 0.5;
    double r2 = rr * rr;

    for (int py = by0; py < by1; py++) {
        for (int px = bx0; px < bx1; px++) {
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                double fy = py + off + sy * step - cy;
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step - cx;
                    if (fx * fx + fy * fy <= r2) hit++;
                }
            }
            if (hit == 0) continue;
            int cov = hit * 256 / kSubInv;
            cov = cov * alphaMul / 255;
            blendPx(px, py, cr, cg, cb, cov);
        }
    }
}

}  // namespace

void fillRect(Canvas& c, int x, int y, int w, int h, Color col) {
    if (w <= 0 || h <= 0) return;

    // 有像素缓冲就走软件路径，能吃到裁剪栈；
    // 没有（退回到兼容位图）就走 GDI。
    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        aaRoundRect(x, y, w, h, 0, r, g, b, 255);
        return;
    }

    HDC dc = hdcOf(c);
    RECT rc{x, y, x + w, y + h};
    HBRUSH br = CreateSolidBrush(toRef(col));
    FillRect(dc, &rc, br);
    DeleteObject(br);
}

void fillRectA(Canvas& c, int x, int y, int w, int h, Color col, int alpha) {
    if (w <= 0 || h <= 0 || alpha <= 0) return;
    if (alpha > 255) alpha = 255;

    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        aaRoundRect(x, y, w, h, 0, r, g, b, alpha);
        return;
    }
    fillRect(c, x, y, w, h, col);
}

void fillRoundRect(Canvas& c, int x, int y, int w, int h, int radius, Color col) {
    if (w <= 0 || h <= 0) return;

    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        aaRoundRect(x, y, w, h, radius, r, g, b, 255);
        return;
    }

    HDC dc = hdcOf(c);
    HBRUSH br = CreateSolidBrush(toRef(col));
    HPEN pen = CreatePen(PS_SOLID, 1, toRef(col));
    HGDIOBJ ob = SelectObject(dc, br);
    HGDIOBJ op = SelectObject(dc, pen);
    RoundRect(dc, x, y, x + w - 1, y + h - 1, radius * 2, radius * 2);
    SelectObject(dc, ob);
    SelectObject(dc, op);
    DeleteObject(br);
    DeleteObject(pen);
}

void fillRoundRectA(Canvas& c, int x, int y, int w, int h, int radius,
                    Color col, int alpha) {
    if (w <= 0 || h <= 0 || alpha <= 0) return;
    if (alpha > 255) alpha = 255;

    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        aaRoundRect(x, y, w, h, radius, r, g, b, alpha);
        return;
    }
    fillRoundRect(c, x, y, w, h, radius, col);
}

void strokeRoundRect(Canvas& c, int x, int y, int w, int h, int radius,
                     Color col, int thickness) {
    if (w <= 0 || h <= 0) return;
    if (thickness < 1) thickness = 1;

    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        // 圆角矩形描边画在矩形内侧，外沿刚好贴合给的框
        aaRoundRectStroke(x + thickness * 0.5, y + thickness * 0.5,
                          w - thickness, h - thickness,
                          radius > thickness ? radius - thickness * 0.5 : 0,
                          thickness, r, g, b, 255);
        return;
    }

    HDC dc = hdcOf(c);
    HPEN pen = CreatePen(PS_SOLID, thickness, toRef(col));
    HGDIOBJ op = SelectObject(dc, pen);
    HGDIOBJ ob = SelectObject(dc, GetStockObject(NULL_BRUSH));
    RoundRect(dc, x, y, x + w - 1, y + h - 1, radius * 2, radius * 2);
    SelectObject(dc, op);
    SelectObject(dc, ob);
    DeleteObject(pen);
}

void strokeRoundRectTop(Canvas& c, int x, int y, int w, int h, int radius,
                        Color col, int thickness) {
    // 顶边加左右圆角弧。用四条线段近似弧，靠抗锯齿接起来看不出来。
    if (thickness < 1) thickness = 1;

    int r = radius;
    if (r > w / 2) r = w / 2;
    if (r > h) r = h;

    // 顶边
    drawLine(c, x + r, y, x + w - r, y, col, thickness);

    // 左上弧
    for (int i = 0; i < 8; i++) {
        double a0 = 3.14159265 * (0.5 + i / 8.0 * 0.5);
        double a1 = 3.14159265 * (0.5 + (i + 1) / 8.0 * 0.5);
        int px0 = x + r - (int)(std::cos(a0) * r);
        int py0 = y + r - (int)(std::sin(a0) * r);
        int px1 = x + r - (int)(std::cos(a1) * r);
        int py1 = y + r - (int)(std::sin(a1) * r);
        drawLine(c, px0, py0, px1, py1, col, thickness);
    }
    // 右上弧
    for (int i = 0; i < 8; i++) {
        double a0 = 3.14159265 * (i / 8.0 * 0.5);
        double a1 = 3.14159265 * ((i + 1) / 8.0 * 0.5);
        int px0 = x + w - r + (int)(std::cos(a0) * r);
        int py0 = y + r - (int)(std::sin(a0) * r);
        int px1 = x + w - r + (int)(std::cos(a1) * r);
        int py1 = y + r - (int)(std::sin(a1) * r);
        drawLine(c, px0, py0, px1, py1, col, thickness);
    }
}

void fillCircle(Canvas& c, int cx, int cy, int r, Color col) {
    if (r <= 0) return;

    if (g_dib.px) {
        int cr, cg, cb;
        unpack(col, cr, cg, cb);
        aaCircle(cx, cy, r, cr, cg, cb, 255);
        return;
    }

    HDC dc = hdcOf(c);
    HBRUSH br = CreateSolidBrush(toRef(col));
    HPEN pen = CreatePen(PS_SOLID, 1, toRef(col));
    HGDIOBJ ob = SelectObject(dc, br);
    HGDIOBJ op = SelectObject(dc, pen);
    Ellipse(dc, cx - r, cy - r, cx + r, cy + r);
    SelectObject(dc, ob);
    SelectObject(dc, op);
    DeleteObject(br);
    DeleteObject(pen);
}

void drawLine(Canvas& c, int x1, int y1, int x2, int y2, Color col,
              int thickness) {
    if (thickness < 1) thickness = 1;

    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        // 1px 的线也当 1.0 宽来光栅化，不然会细到发虚
        aaLine(x1 + 0.5, y1 + 0.5, x2 + 0.5, y2 + 0.5, thickness, r, g, b,
               255);
        return;
    }

    HDC dc = hdcOf(c);
    HPEN pen = CreatePen(PS_SOLID, thickness, toRef(col));
    HGDIOBJ op = SelectObject(dc, pen);
    MoveToEx(dc, x1, y1, nullptr);
    LineTo(dc, x2, y2);
    SelectObject(dc, op);
    DeleteObject(pen);
}

// ---------------------------------------------------------------- 浮点绘制
//
// 给图标和精细控件用的。整数版本会先把坐标取整，在小尺寸图标上
// 会把相邻端点压进同一个像素格，线段直接退化成零长度 —— 这就是
// 「图标消失」的根因。这里全程保持浮点，交给 4x4 子采样光栅化。

void drawLineF(Canvas& c, float x1, float y1, float x2, float y2, Color col,
               float thickness) {
    if (thickness < 0.6f) thickness = 0.6f;

    // 两个端点几乎重合就不画了。零长度线段在光栅化里是空操作，
    // 但它会让「圆形描边」这类循环白跑 40 次。
    float dx = x2 - x1, dy = y2 - y1;
    if (dx * dx + dy * dy < 0.02f) return;

    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        aaLine(x1, y1, x2, y2, thickness, r, g, b, 255);
        return;
    }

    HDC dc = hdcOf(c);
    int it = (int)(thickness + 0.5f);
    if (it < 1) it = 1;
    HPEN pen = CreatePen(PS_SOLID, it, toRef(col));
    HGDIOBJ op = SelectObject(dc, pen);
    MoveToEx(dc, (int)(x1 + 0.5f), (int)(y1 + 0.5f), nullptr);
    LineTo(dc, (int)(x2 + 0.5f), (int)(y2 + 0.5f));
    SelectObject(dc, op);
    DeleteObject(pen);
}

void fillRoundRectF(Canvas& c, float x, float y, float w, float h, float radius,
                    Color col) {
    if (w <= 0 || h <= 0) return;
    if (g_dib.px) {
        int r, g, b;
        unpack(col, r, g, b);
        aaRoundRect(x, y, w, h, radius, r, g, b, 255);
        return;
    }
    fillRoundRect(c, (int)(x + 0.5f), (int)(y + 0.5f), (int)(w + 0.5f),
                  (int)(h + 0.5f), (int)(radius + 0.5f), col);
}

// 圆角矩形描边。**这里是「按钮描边失败」的修复点。**
//
// 原来的整数版本把 rect 收缩了 thickness 之后调
// aaRoundRectStroke，同时把半径减了 thickness*0.5。
// 问题出在胶囊按钮上：chipRadius = 999，收缩后半径还是 999。
// aaRoundRectStroke 内部会把外半径夹到 min(w,h)/2，但**内半径
// 只夹了下限不夹上限**（ir = r - thick/2 = 998.5），于是内轮廓
// 的判定「点是否在 (x+.5,y+.5,w-thick,h-thick,998.5) 内」
// 对几乎每个点都返回 true —— 整块被当成「环」涂满，
// 看起来就是「描边变成了实心填充」。
//
// 正确做法：把半径夹到合法范围之后再算内轮廓，并且由这里
// 统一负责夹取，不指望光栅化内部兜底。
void strokeRoundRectF(Canvas& c, float x, float y, float w, float h,
                      float radius, Color col, float thickness) {
    if (w <= 0 || h <= 0) return;
    if (thickness < 0.6f) thickness = 0.6f;

    // 厚度不能超过短边的一半，否则内轮廓直接翻负
    float maxT = (w < h ? w : h) * 0.5f;
    if (thickness > maxT) thickness = maxT;
    if (thickness <= 0) return;

    // 半径夹到合法范围 —— 这一步是关键
    float rmax = (w < h ? w : h) * 0.5f;
    float r = radius;
    if (r > rmax) r = rmax;
    if (r < 0) r = 0;

    // 描边画在矩形内侧：外沿贴合给的框，中心线内缩半个厚度
    float ix = x + thickness * 0.5f;
    float iy = y + thickness * 0.5f;
    float iw = w - thickness;
    float ih = h - thickness;
    if (iw <= 0 || ih <= 0) return;

    // 内轮廓半径也要夹，且要保证 >= 0
    float irmax = (iw < ih ? iw : ih) * 0.5f;
    float ir = r - thickness * 0.5f;
    if (ir > irmax) ir = irmax;
    if (ir < 0) ir = 0;

    if (g_dib.px) {
        int cr, cg, cb;
        unpack(col, cr, cg, cb);
        aaRoundRectStrokeF(ix, iy, iw, ih, r, ir, thickness, cr, cg, cb, 255);
        return;
    }

    HDC dc = hdcOf(c);
    int it = (int)(thickness + 0.5f);
    if (it < 1) it = 1;
    HPEN pen = CreatePen(PS_SOLID, it, toRef(col));
    HGDIOBJ op = SelectObject(dc, pen);
    HGDIOBJ ob = SelectObject(dc, GetStockObject(NULL_BRUSH));
    RoundRect(dc, (int)(x + 0.5f), (int)(y + 0.5f), (int)(x + w - 1 + 0.5f),
              (int)(y + h - 1 + 0.5f), (int)(r * 2 + 0.5f),
              (int)(r * 2 + 0.5f));
    SelectObject(dc, op);
    SelectObject(dc, ob);
    DeleteObject(pen);
}

int drawText(Canvas& c, int x, int y, const std::string& utf8,
             const TextStyle& st) {
    if (utf8.empty()) return 0;
    HDC dc = hdcOf(c);
    HFONT f = getFont(st);
    HGDIOBJ of = SelectObject(dc, f);
    SetBkMode(dc, TRANSPARENT);        // 不加这行，文字带底色块
    SetTextColor(dc, toRef(st.color));

    std::wstring w = toWide(utf8);
    SIZE sz{0, 0};
    GetTextExtentPoint32W(dc, w.c_str(), (int)w.size(), &sz);
    TextOutW(dc, x, y, w.c_str(), (int)w.size());

    SelectObject(dc, of);
    return (int)sz.cx;
}

void measureText(Canvas& c, const std::string& utf8, const TextStyle& st,
                 int* outW, int* outH) {
    HDC dc = hdcOf(c);
    HFONT f = getFont(st);
    HGDIOBJ of = SelectObject(dc, f);
    std::wstring w = toWide(utf8);
    SIZE sz{0, 0};
    if (!w.empty()) {
        // 注意：传的是「宽字符个数」，不是字节数。
        // 这里最容易写错，中文会算成三倍宽。
        GetTextExtentPoint32W(dc, w.c_str(), (int)w.size(), &sz);
    } else {
        TEXTMETRICW tm{};
        GetTextMetricsW(dc, &tm);
        sz.cy = tm.tmHeight;
    }
    SelectObject(dc, of);
    if (outW) *outW = (int)sz.cx;
    if (outH) *outH = (int)sz.cy;
}

int charWidth(Canvas& c, const TextStyle& st) {
    int w = 0, h = 0;
    measureText(c, "M", st, &w, &h);
    return w;
}

int lineHeight(Canvas& c, const TextStyle& st) {
    HDC dc = hdcOf(c);
    HFONT f = getFont(st);
    HGDIOBJ of = SelectObject(dc, f);
    TEXTMETRICW tm{};
    GetTextMetricsW(dc, &tm);
    SelectObject(dc, of);
    return (int)tm.tmHeight + 2;
}

void pushClip(Canvas& c, int x, int y, int w, int h) {
    HDC dc = hdcOf(c);
    HRGN rgn = CreateRectRgn(x, y, x + w, y + h);
    g_win.clipStack.push_back(rgn);
    SelectClipRgn(dc, rgn);

    // CPU 光栅化不走 GDI 的裁剪区，得自己叠一份。
    // 新裁剪框和上一层求交，避免嵌套裁剪时越界。
    ClipRect nr{x, y, x + w, y + h};
    if (!g_cpuClip.empty()) {
        const ClipRect& pv = g_cpuClip.back();
        if (nr.x0 < pv.x0) nr.x0 = pv.x0;
        if (nr.y0 < pv.y0) nr.y0 = pv.y0;
        if (nr.x1 > pv.x1) nr.x1 = pv.x1;
        if (nr.y1 > pv.y1) nr.y1 = pv.y1;
    }
    if (nr.x1 < nr.x0) nr.x1 = nr.x0;
    if (nr.y1 < nr.y0) nr.y1 = nr.y0;
    g_cpuClip.push_back(nr);
}

void popClip(Canvas& c) {
    if (g_win.clipStack.empty()) return;
    HDC dc = hdcOf(c);
    DeleteObject(g_win.clipStack.back());
    g_win.clipStack.pop_back();
    if (g_win.clipStack.empty()) {
        SelectClipRgn(dc, nullptr);
    } else {
        SelectClipRgn(dc, g_win.clipStack.back());
    }
    if (!g_cpuClip.empty()) g_cpuClip.pop_back();
}

void resetClips(Canvas& c) {
    HDC dc = hdcOf(c);
    for (HRGN r : g_win.clipStack) DeleteObject((HGDIOBJ)r);
    g_win.clipStack.clear();
    g_cpuClip.clear();
    if (dc) SelectClipRgn(dc, nullptr);
}

// 截图钩子：设了 SIDE_SHOT=<路径> 就把当前帧写成 BMP。
//
// 「文件不存在时才写」，不是「只写第一帧」—— 这样外部探针可以删掉这个
// 文件，点一下按钮，等它重新出现，就拿到了点击后的那一帧。生产环境没人
// 设这个变量，就是个空转的 if。
void dumpShotIfAsked() {
    char buf[512];
    DWORD n = GetEnvironmentVariableA("SIDE_SHOT", buf, sizeof(buf));
    if (n == 0 || n >= sizeof(buf)) return;

    if (!g_dib.px) return;

    // 已经有人在等这一帧了就别覆盖
    if (FILE* probe = fopen(buf, "rb")) {
        fclose(probe);
        return;
    }

    int w = g_dib.w, h = g_dib.h;
    if (w <= 0 || h <= 0) return;

    BITMAPFILEHEADER fh{};
    BITMAPINFOHEADER bi{};
    bi.biSize = sizeof(bi);
    bi.biWidth = w;
    bi.biHeight = -h;
    bi.biPlanes = 1;
    bi.biBitCount = 32;
    bi.biCompression = BI_RGB;

    DWORD pixBytes = (DWORD)w * h * 4;
    fh.bfType = 0x4D42;
    fh.bfOffBits = sizeof(fh) + sizeof(bi);
    fh.bfSize = fh.bfOffBits + pixBytes;

    FILE* fp = fopen(buf, "wb");
    if (!fp) return;
    fwrite(&fh, sizeof(fh), 1, fp);
    fwrite(&bi, sizeof(bi), 1, fp);
    fwrite(g_dib.px, 1, pixBytes, fp);
    fclose(fp);
}

// ---------------------------------------------------------------- 窗口

namespace {

LRESULT CALLBACK wndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    auto& cb = g_win.cbs;

    switch (msg) {
        case WM_ERASEBKGND:
            // 双缓冲自己会盖满整屏，擦背景只会闪
            return 1;

        case WM_SIZE: {
            int w = LOWORD(lp);
            int h = HIWORD(lp);
            // 尺寸变了缓冲要重建，裁剪栈里的旧坐标也没意义了
            g_cpuClip.clear();
            if (cb.onResize) cb.onResize(w, h, cb.ctx);
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;
        }

        case WM_PAINT: {
            PAINTSTRUCT ps;
            HDC dc = BeginPaint(hwnd, &ps);

            RECT rc;
            GetClientRect(hwnd, &rc);
            int w = rc.right - rc.left;
            int h = rc.bottom - rc.top;
            if (w <= 0 || h <= 0) {
                EndPaint(hwnd, &ps);
                return 0;
            }

            ensureBackBuffer(dc, w, h);

            Canvas canvas;
            canvas.handle = g_win.backDc;
            canvas.width = w;
            canvas.height = h;
            canvas.scale = 1.0f;

            if (cb.onPaint) cb.onPaint(&canvas, cb.ctx);

            // 一次 BitBlt 把整块图推上去
            BitBlt(dc, 0, 0, w, h, g_win.backDc, 0, 0, SRCCOPY);

            // 截图钩子。设了 SIDE_SHOT 环境变量就存一张 BMP，
            // 用来检查渲染效果 —— GUI 程序没法用常规手段截图。
            dumpShotIfAsked();

            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_MOUSEMOVE: {
            if (cb.onMouse) {
                cb.onMouse(GET_X_LPARAM(lp), GET_Y_LPARAM(lp), (int)wp, cb.ctx);
            }
            // 鼠标移动要收 WM_MOUSELEAVE，得先注册
            TRACKMOUSEEVENT tme{};
            tme.cbSize = sizeof(tme);
            tme.dwFlags = TME_LEAVE;
            tme.hwndTrack = hwnd;
            TrackMouseEvent(&tme);
            return 0;
        }

        case WM_MOUSELEAVE: {
            if (cb.onMouse) cb.onMouse(-1, -1, 0, cb.ctx);
            return 0;
        }

        case WM_LBUTTONDOWN:
        case WM_LBUTTONUP:
        case WM_RBUTTONDOWN:
        case WM_RBUTTONUP:
        case WM_MBUTTONDOWN:
        case WM_MBUTTONUP: {
            if (cb.onMouse) {
                int btn = 0;
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP) btn = 1;
                else if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP) btn = 2;
                else btn = 4;
                bool down = (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN ||
                             msg == WM_MBUTTONDOWN);
                cb.onMouse(GET_X_LPARAM(lp), GET_Y_LPARAM(lp),
                           down ? btn : -btn, cb.ctx);
            }
            return 0;
        }

        case WM_MOUSEWHEEL: {
            if (cb.onScroll) {
                POINT pt{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
                ScreenToClient(hwnd, &pt);
                int delta = GET_WHEEL_DELTA_WPARAM(wp);   // 正数往上
                cb.onScroll(pt.x, pt.y, delta / WHEEL_DELTA, cb.ctx);
            }
            return 0;
        }

        case WM_KEYDOWN:
        case WM_KEYUP: {
            if (cb.onKey) {
                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                bool shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;
                bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
                cb.onKey((int)wp, msg == WM_KEYDOWN, ctrl, shift, alt, cb.ctx);
            }
            // F5 / Ctrl+B 之类如果不想让系统响铃，这里返回 0
            return 0;
        }

        case WM_CHAR: {
            // 中文输入走 WM_CHAR（UTF-16）。
            // 只有可见字符才转发，控制字符给 WM_KEYDOWN 处理。
            if (cb.onKey && wp >= 32) {
                wchar_t ch = (wchar_t)wp;
                cb.onKey(0x10000 | (int)ch, true, false, false, false, cb.ctx);
            }
            return 0;
        }

        case WM_CLOSE: {
            if (cb.onClose) cb.onClose(cb.ctx);
            g_win.running = false;
            DestroyWindow(hwnd);
            return 0;
        }

        case WM_DESTROY:
            g_win.running = false;
            PostQuitMessage(0);
            return 0;

        case WM_GETMINMAXINFO: {
            auto* mmi = (MINMAXINFO*)lp;
            mmi->ptMinTrackSize.x = 720;
            mmi->ptMinTrackSize.y = 480;
            return 0;
        }

        default: break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

}  // namespace

void* createWindow(const WindowDesc& desc, const WindowCallbacks& cbs) {
    // OLE 要先初始化，SHBrowseForFolder 和拖放都要用。
    // 失败也无所谓（可能已经初始化过），继续走。
    OleInitialize(nullptr);

    HINSTANCE hi = GetModuleHandleW(nullptr);

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    // CS_OWNDC：每个窗口自带 DC，双缓冲和字体缓存都靠它
    wc.style = CS_OWNDC | CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = wndProc;
    wc.hInstance = hi;
    // 必须走 LoadCursorW 拿到真实句柄。
    // 直接把资源 ID（32512）塞进去会注册失败，报「参数错误」。
    wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_ARROW);
    wc.hbrBackground = nullptr;      // 自己全画，不要系统刷
    wc.lpszClassName = L"SppIdeWindow";

    static bool registered = false;
    if (!registered) {
        if (!RegisterClassExW(&wc)) {
            char buf[128];
            snprintf(buf, sizeof(buf), "RegisterClassExW 失败，错误码 %lu",
                     GetLastError());
            SPP_LOG_ERR(buf);
            return nullptr;
        }
        registered = true;
    }

    DWORD style = desc.frameless ? (WS_POPUP | WS_THICKFRAME | WS_MINIMIZEBOX |
                                    WS_MAXIMIZEBOX | WS_SYSMENU)
                                 : WS_OVERLAPPEDWINDOW;
    if (!desc.resizable) style &= ~(WS_THICKFRAME | WS_MAXIMIZEBOX);

    int sw = GetSystemMetrics(SM_CXSCREEN);
    int sh = GetSystemMetrics(SM_CYSCREEN);
    int x = desc.center ? (sw - desc.width) / 2 : CW_USEDEFAULT;
    int y = desc.center ? (sh - desc.height) / 2 : CW_USEDEFAULT;

    std::wstring title = toWide(desc.title);

    HWND hwnd = CreateWindowExW(
        0, L"SppIdeWindow", title.c_str(), style, x, y, desc.width, desc.height,
        nullptr, nullptr, hi, nullptr);

    if (!hwnd) {
        char buf[128];
        snprintf(buf, sizeof(buf), "CreateWindowExW 失败，错误码 %lu",
                 GetLastError());
        SPP_LOG_ERR(buf);
        return nullptr;
    }

    g_win.hwnd = hwnd;
    g_win.cbs = cbs;
    g_win.running = true;

    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    return hwnd;
}

int runLoop() {
    MSG msg;
    while (g_win.running && GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    return 0;
}

void requestPaint(void* win) {
    HWND h = win ? (HWND)win : g_win.hwnd;
    if (h) InvalidateRect(h, nullptr, FALSE);
}

void invalidate(void* win, int x, int y, int w, int h) {
    HWND hw = win ? (HWND)win : g_win.hwnd;
    if (!hw) return;
    RECT r{x, y, x + w, y + h};
    InvalidateRect(hw, &r, FALSE);
}

void quitLoop() {
    g_win.running = false;
    PostQuitMessage(0);
}

// ---------------------------------------------------------------- 文件

std::string readFile(const std::string& path) {
    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return {};
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (n < 0) { fclose(f); return {}; }
    std::string s((size_t)n, '\0');
    size_t got = n > 0 ? fread(&s[0], 1, (size_t)n, f) : 0;
    fclose(f);
    s.resize(got);
    return s;
}

bool writeFile(const std::string& path, const std::string& data) {
    FILE* f = fopen(path.c_str(), "wb");
    if (!f) return false;
    size_t wrote = data.empty() ? 0 : fwrite(data.data(), 1, data.size(), f);
    fclose(f);
    return wrote == data.size();
}

bool fileExists(const std::string& path) {
    DWORD a = GetFileAttributesA(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

bool dirExists(const std::string& path) {
    DWORD a = GetFileAttributesA(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
}

// 删一个文件。本来就不存在也返回 true —— 调用方要的是「它不在了」，
// 不是「我确实删到了一个文件」。返回 false 再去判 GetLastError 反而更难用。
bool removeFile(const std::string& path) {
    if (path.empty()) return false;
    if (!DeleteFileA(path.c_str())) {
        return GetLastError() == ERROR_FILE_NOT_FOUND ||
               GetLastError() == ERROR_PATH_NOT_FOUND;
    }
    return true;
}

bool removeDir(const std::string& path) {
    if (path.empty()) return false;
    return RemoveDirectoryA(path.c_str()) != 0;
}

uint64_t fileSize(const std::string& path) {
    WIN32_FILE_ATTRIBUTE_DATA d{};
    if (!GetFileAttributesExA(path.c_str(), GetFileExInfoStandard, &d)) return 0;
    if (d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) return 0;
    return ((uint64_t)d.nFileSizeHigh << 32) | d.nFileSizeLow;
}

std::vector<DirEntry> listDir(const std::string& path) {
    std::vector<DirEntry> out;
    std::string pat = path;
    if (!pat.empty() && pat.back() != '\\' && pat.back() != '/') pat += "\\";
    pat += "*";

    WIN32_FIND_DATAA fd{};
    HANDLE h = FindFirstFileA(pat.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return out;

    do {
        std::string name = fd.cFileName;
        if (name == "." || name == "..") continue;
        DirEntry e;
        e.name = name;
        e.isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        e.size = ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
        out.push_back(std::move(e));
    } while (FindNextFileA(h, &fd));

    FindClose(h);

    // 目录在前，然后按名字排。IDE 的项目树要稳定顺序。
    std::sort(out.begin(), out.end(), [](const DirEntry& a, const DirEntry& b) {
        if (a.isDir != b.isDir) return a.isDir > b.isDir;
        return a.name < b.name;
    });
    return out;
}

bool makeDirs(const std::string& path) {
    if (path.empty()) return false;
    if (dirExists(path)) return true;

    // 递归建父目录
    std::string cur;
    for (size_t i = 0; i < path.size(); i++) {
        char c = path[i];
        cur += c;
        if (c == '\\' || c == '/') {
            if (cur.size() > 1 && !dirExists(cur)) CreateDirectoryA(cur.c_str(), nullptr);
        }
    }
    if (!dirExists(path)) CreateDirectoryA(path.c_str(), nullptr);
    return dirExists(path);
}

std::vector<std::string> listDirs(const std::string& path) {
    std::vector<std::string> out;
    for (const auto& e : listDir(path)) {
        if (e.isDir) out.push_back(e.name);
    }
    return out;
}

std::string joinPath(const std::string& a, const std::string& b) {
    if (a.empty()) return b;
    if (b.empty()) return a;
    char last = a.back();
    if (last == '\\' || last == '/') return a + b;
    return a + "\\" + b;
}

std::string normalizePath(const std::string& path) {
    if (path.empty()) return path;
    std::string out;
    out.reserve(path.size());
    for (char c : path) {
        if (c == '/') c = '\\';                 // 统一成反斜杠
        if (c == '\\' && !out.empty() && out.back() == '\\') continue;  // 折叠连续分隔符
        out += c;
    }
    // 结尾分隔符去掉，但别把盘符根 "D:\" 削成 "D:"
    while (out.size() > 3 && out.back() == '\\') out.pop_back();
    return out;
}

std::string parentDir(const std::string& path) {
    if (path.empty()) return path;
    std::string p = path;
    // 去掉结尾的分隔符，"D:\\a\\b\\" 的上一级还是 "D:\\a\\b" 的父
    while (p.size() > 1 && (p.back() == '\\' || p.back() == '/')) p.pop_back();

    size_t s = p.find_last_of("\\/");
    if (s == std::string::npos) return ".";
    if (s == 0) return p.substr(0, 1);
    // "D:\\" 这种已经到根了
    if (s == 2 && p[1] == ':') return p.substr(0, 3);
    return p.substr(0, s);
}

std::string baseName(const std::string& path) {
    if (path.empty()) return std::string();
    std::string p = path;
    // 结尾的分隔符先去掉，否则 "D:\a\b\" 会得出空串
    while (p.size() > 1 && (p.back() == '\\' || p.back() == '/')) p.pop_back();

    size_t s = p.find_last_of("\\/");
    if (s == std::string::npos) return p;
    // "D:\" 这种，根本身没有名字
    if (s == p.size() - 1) return std::string();
    return p.substr(s + 1);
}

std::string executableDir() {
    char buf[MAX_PATH]{};
    DWORD n = GetModuleFileNameA(nullptr, buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return ".";
    return parentDir(std::string(buf, n));
}

std::string dllDir() {
    HMODULE h = nullptr;
    // FROM_ADDRESS 拿 GameModuleHandleEx 反查：把本函数自己的地址递进去，
    // 系统就知道它属于哪个模块。这样就不用把 DLL 名写死在字符串里。
    if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCSTR>(&dllDir), &h) || !h)
        return std::string();

    char buf[MAX_PATH]{};
    DWORD n = GetModuleFileNameA(h, buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return std::string();
    return parentDir(std::string(buf, n));
}

std::string getEnv(const std::string& name) {
    char buf[4096]{};
    DWORD n = GetEnvironmentVariableA(name.c_str(), buf, sizeof(buf));
    if (n == 0 || n >= sizeof(buf)) return "";
    return std::string(buf, n);
}

std::string whichExe(const std::string& name) {
    // 名字里已经带路径就直接验存在性
    if (name.find('\\') != std::string::npos ||
        name.find('/') != std::string::npos) {
        return fileExists(name) ? name : "";
    }

    std::string path = getEnv("PATH");
    size_t pos = 0;
    while (pos <= path.size()) {
        size_t e = path.find(';', pos);
        if (e == std::string::npos) e = path.size();
        std::string dir = path.substr(pos, e - pos);
        pos = e + 1;
        if (dir.empty()) continue;
        // PATH 里常见带引号的项
        if (dir.front() == '"' && dir.back() == '"' && dir.size() > 2)
            dir = dir.substr(1, dir.size() - 2);
        std::string full = joinPath(dir, name);
        if (fileExists(full)) return full;
    }
    return "";
}

// js_init 传进来的数据目录。非空时 appDataDir() 一律返回它，
// 见 platform.h 里 setAppDataOverride 的说明。
static std::string g_appDataOverride;

void setAppDataOverride(const std::string& dir) {
    g_appDataOverride = dir;
    if (!dir.empty()) makeDirs(dir);
}

std::string appDataDir(const std::string& appName) {
    // 已经指定过就直接用，不再往 %APPDATA%（C 盘）落任何东西。
    if (!g_appDataOverride.empty()) return g_appDataOverride;

    char buf[MAX_PATH]{};
    DWORD n = GetEnvironmentVariableA("APPDATA", buf, MAX_PATH);
    std::string base = (n > 0 && n < MAX_PATH) ? std::string(buf, n) : ".";
    std::string dir = joinPath(base, appName);
    makeDirs(dir);
    return dir;
}

// ---------------------------------------------------------------- 选目录

// 用 SHBrowseForFolder 弹系统目录选择框。
// 这是最稳的做法：不用自己写文件浏览器，也自带磁盘列表。
std::string pickFolder(const std::string& title, const std::string& startDir) {
    BROWSEINFOA bi{};
    bi.hwndOwner = g_win.hwnd;
    bi.lpszTitle = title.empty() ? "选择一个目录" : title.c_str();
    // 新版样式：能新建文件夹、能输入路径
    bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE |
                 BIF_USENEWUI | BIF_EDITBOX;

    // 起点目录。OleInitialize 由 initOle() 保证已经调过。
    LPITEMIDLIST start = nullptr;
    if (!startDir.empty()) {
        // SHParseDisplayName 要 UTF-16
        std::wstring w = toWide(startDir);
        SFGAOF attr = 0;
        if (SHParseDisplayName(w.c_str(), nullptr, &start, attr, nullptr) != S_OK)
            start = nullptr;
    }
    bi.pidlRoot = start;

    LPITEMIDLIST idl = SHBrowseForFolderA(&bi);
    std::string result;

    if (idl) {
        char path[MAX_PATH]{};
        if (SHGetPathFromIDListA(idl, path)) result = path;
        CoTaskMemFree(idl);
    }
    if (start) CoTaskMemFree(start);

    return result;
}

int64_t freeSpaceOf(const std::string& path) {
    ULARGE_INTEGER avail{}, total{}, freeAll{};
    std::wstring w = toWide(path);
    if (!GetDiskFreeSpaceExW(w.c_str(), &avail, &total, &freeAll)) return -1;
    return (int64_t)avail.QuadPart;
}

std::string homeDir() {
    char buf[MAX_PATH]{};
    DWORD n = GetEnvironmentVariableA("USERPROFILE", buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH) return std::string(buf, n);
    return "";
}

// ---------------------------------------------------------------- 网络
//
// WinHTTP 把 URL 抓成文件。
//
// 几个容易踩的点：
//   1) Maven 中央仓库会 302 到 CDN —— 必须打开自动跟随重定向，
//      不然拿到的是一个 302 响应体（几字节的 HTML），写出来是个坏 jar。
//   2) 要显式检查 HTTP 状态码。WinHttpSendRequest 成功不代表 200 ——
//      404 也算「请求成功」，不查的话会把一个错误页当 jar 存下来。
//   3) 边收边写，不整块读进内存。有的 jar 有几十 MB。
//   4) 中途失败要把半个文件删掉。留一个截断的 jar 在缓存里，
//      下次会当成「已经有了」，编译时报一堆莫名其妙的错。
// 一次「已经收到响应头」的请求。
//
// httpGetToFile 和 httpGet 都要走建连那一整套（代理、重定向、超时），
// 抽出来共用 —— 否则这些坑得踩两遍，而且修好一个忘另一个是迟早的事。
struct HttpResponse {
    HINTERNET hs = nullptr, hc = nullptr, hr = nullptr;
    DWORD code = 0;                    // HTTP 状态码
    unsigned long long length = 0;     // Content-Length，没有则是 0

    void close() {
        if (hr) WinHttpCloseHandle(hr);
        if (hc) WinHttpCloseHandle(hc);
        if (hs) WinHttpCloseHandle(hs);
        hr = hc = hs = nullptr;
    }
};

// 返回 true 只表示「请求发出去并收到了响应」。
// ⚠️ 调用方**必须**自己看 code：404 也是「收到了响应」，
// 不看就把错误页当正文用了。
static bool openResponse(const std::string& url, HttpResponse& r,
                         std::string* err) {
    auto fail = [&](const std::string& m) {
        if (err) *err = m;
        return false;
    };

    std::wstring wurl = toWide(url);
    if (wurl.empty()) return fail("URL 转换失败");

    URL_COMPONENTS uc{};
    uc.dwStructSize = sizeof(uc);
    wchar_t host[256]{};
    wchar_t path[2048]{};
    uc.lpszHostName = host;
    uc.dwHostNameLength = 255;
    uc.lpszUrlPath = path;
    uc.dwUrlPathLength = 2047;

    if (!WinHttpCrackUrl(wurl.c_str(), (DWORD)wurl.size(), 0, &uc))
        return fail("URL 解析失败");

    bool https = (uc.nScheme == INTERNET_SCHEME_HTTPS);

    HINTERNET hs = WinHttpOpen(
        L"JavaStudio/26.1", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!hs) return fail("WinHttpOpen 失败");

    // 环境变量里的代理要给 WinHTTP 单独装上去。
    //
    // DEFAULT_PROXY 读的是系统那套（IE / WinINet 的 internet options），
    // **不看进程的环境变量**。而设置了 HTTP_PROXY / HTTPS_PROXY 的环境很常见：
    // 公司内网、CI、各种命令行工具链都靠它。结果就是浏览器和命令行都通，
    // 只有这个 IDE 下第三方库超时 —— 表现跟「网络坏了」一模一样。
    // curl / pip / git 都认这些变量（除了 git，它有自己的一套配置），
    // 这里按同一个习惯来：先查小写再查大写，https 走 https_proxy、http 走 http_proxy，
    // 都没有再试 ALL_PROXY。
    auto envProxy = [&]() -> std::string {
        const char* names[] = {https ? "https_proxy" : "http_proxy",
                               https ? "HTTPS_PROXY" : "HTTP_PROXY",
                               "all_proxy", "ALL_PROXY"};
        for (const char* n : names) {
            std::string v = getEnv(n);
            if (!v.empty()) return v;
        }
        return "";
    };

    r.hs = hs;

    std::string proxy = envProxy();
    if (!proxy.empty()) {
        // "http://host:port" / "socks5://..." / 光一个 "host:port" 都认。
        // WinHTTP 的具名代理串只吃后半段，所以先把 scheme 摘掉。
        std::string hostPort = proxy;
        size_t cut = proxy.find("://");
        if (cut != std::string::npos) hostPort = proxy.substr(cut + 3);
        while (!hostPort.empty() && hostPort.back() == '/') hostPort.pop_back();

        if (!hostPort.empty()) {
            // http= 和 https= 都指过去：一个代理通常两个协议都管。
            std::wstring wProxy = toWide("http=" + hostPort + ";https=" + hostPort);
            WINHTTP_PROXY_INFO pi{};
            pi.dwAccessType = WINHTTP_ACCESS_TYPE_NAMED_PROXY;
            pi.lpszProxy = (LPWSTR)wProxy.c_str();
            pi.lpszProxyBypass = nullptr;
            WinHttpSetOption(r.hs, WINHTTP_OPTION_PROXY, &pi, sizeof(pi));
        }
    }

    // 超时给宽松一点：解析 DNS + TLS 握手 + 传输，慢网下都可能要十几秒
    WinHttpSetTimeouts(r.hs, 15000, 15000, 20000, 60000);

    r.hc = WinHttpConnect(r.hs, host, uc.nPort, 0);
    if (!r.hc) {
        r.close();
        return fail("连接不上 " + toUtf8(host) + "（检查网络）");
    }

    DWORD flags = https ? WINHTTP_FLAG_SECURE : 0;
    r.hr = WinHttpOpenRequest(r.hc, L"GET", path, nullptr,
                              WINHTTP_NO_REFERER,
                              WINHTTP_DEFAULT_ACCEPT_TYPES, flags);
    if (!r.hr) {
        r.close();
        return fail("创建请求失败");
    }

    // 302 必须跟。Maven 中央仓库现在几乎全量走 CDN 重定向。
    DWORD redir = WINHTTP_OPTION_REDIRECT_POLICY_ALWAYS;
    WinHttpSetOption(r.hr, WINHTTP_OPTION_REDIRECT_POLICY, &redir,
                     sizeof(redir));

    if (!WinHttpSendRequest(r.hr, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                            WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(r.hr, nullptr)) {
        r.close();
        return fail("请求失败（网络不通或超时）");
    }

    DWORD code = 0;
    DWORD len = sizeof(code);
    WinHttpQueryHeaders(r.hr,
                        WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                        WINHTTP_HEADER_NAME_BY_INDEX, &code, &len,
                        WINHTTP_NO_HEADER_INDEX);
    r.code = code;

    // 总长度：分块传输的响应没有 Content-Length，那就是 0，
    // 交给界面显示「已下载 …」而不是百分比。
    DWORD clen = 0;
    DWORD clenLen = sizeof(clen);
    if (WinHttpQueryHeaders(r.hr,
                            WINHTTP_QUERY_CONTENT_LENGTH |
                                WINHTTP_QUERY_FLAG_NUMBER,
                            WINHTTP_HEADER_NAME_BY_INDEX, &clen, &clenLen,
                            WINHTTP_NO_HEADER_INDEX))
        r.length = clen;

    return true;
}

bool httpGetToFile(const std::string& url, const std::string& destPath,
                   std::string* err, HttpProgress prog, void* progUser) {
    auto fail = [&](const std::string& m) {
        if (err) *err = m;
        return false;
    };

    HttpResponse r;
    if (!openResponse(url, r, err)) return false;

    if (r.code == 404) {
        r.close();
        return fail("仓库里没有这个版本（HTTP 404）");
    }
    if (r.code != 200) {
        char b[96];
        snprintf(b, sizeof(b), "服务器返回 HTTP %lu", (unsigned long)r.code);
        r.close();
        return fail(b);
    }
    if (!makeDirs(parentDir(destPath))) {
        r.close();
        return fail("创建缓存目录失败");
    }

    FILE* f = fopen(destPath.c_str(), "wb");
    if (!f) {
        r.close();
        return fail("写不进文件（可能被杀毒软件占着）");
    }

    std::vector<char> buf(1 << 16);
    DWORD got = 0;
    unsigned long long total = 0;
    std::string why;
    bool good = true;

    while (true) {
        if (!WinHttpReadData(r.hr, buf.data(), (DWORD)buf.size(), &got)) {
            good = false;
            break;
        }
        if (got == 0) break;   // 收完了
        if (fwrite(buf.data(), 1, got, f) != got) {
            good = false;
            break;
        }
        total += got;

        // 进度回调是**同步**调的，由界面自己决定要不要攒着再刷 UI
        //（每个网络包调一次，有 Content-Length 的话百分比就是连续的）。
        if (prog && !prog(total, r.length, progUser)) {
            why = "已取消";
            good = false;
            break;
        }
    }
    fclose(f);
    r.close();

    if (good && total > 0) return true;

    // 中途失败要把半个文件删掉。留一个截断的 jar 在缓存里，
    // 下次会被当成「已经下载好了」，编译时报一堆莫名其妙的错。
    remove(destPath.c_str());
    if (why.empty()) why = good ? "下到的是空文件" : "传输中断";
    return fail(why);
}

bool httpGet(const std::string& url, std::string* out, std::string* err) {
    auto fail = [&](const std::string& m) {
        if (err) *err = m;
        return false;
    };

    HttpResponse r;
    if (!openResponse(url, r, err)) return false;

    if (r.code == 404) {
        r.close();
        return fail("没有这个地址（HTTP 404）");
    }
    if (r.code != 200) {
        char b[96];
        snprintf(b, sizeof(b), "服务器返回 HTTP %lu", (unsigned long)r.code);
        r.close();
        return fail(b);
    }

    // 只给仓库搜索 / metadata 这类小响应用，所以敢设上限。
    // 真超了说明 URL 或服务端不对劲，别把几百 MB 吃进内存。
    const size_t kMax = 8u * 1024 * 1024;
    std::string body;
    std::vector<char> buf(1 << 16);
    bool good = true;

    while (true) {
        DWORD got = 0;
        if (!WinHttpReadData(r.hr, buf.data(), (DWORD)buf.size(), &got)) {
            good = false;
            break;
        }
        if (got == 0) break;
        body.append(buf.data(), got);
        if (body.size() > kMax) {
            good = false;
            break;
        }
    }
    r.close();

    if (!good) return fail("传输中断或响应过大");
    if (body.empty()) return fail("收到的是空响应");
    if (out) *out = body;
    return true;
}

// ---------------------------------------------------------------- 剪贴板
//
// CF_UNICODETEXT 是系统剪贴板里最通用的一种。存的时候转 UTF-16，
// 取的时候转回 UTF-8。
//
// 打开剪贴板可能失败 —— 别的进程正占着。所以要重试几次，
// 一次就放弃的话会出现「偶尔粘贴不出来」这种偶发 bug。

void clipboardSet(const std::string& utf8) {
    if (utf8.empty()) return;

    std::wstring w = toWide(utf8);
    if (w.empty()) return;

    // 剪贴板里换行必须是 CRLF，单独一个 LF 粘到记事本里会连成一行
    std::wstring crlf;
    crlf.reserve(w.size() + 16);
    for (wchar_t c : w) {
        if (c == L'\n') crlf += L'\r';
        crlf += c;
    }

    size_t bytes = (crlf.size() + 1) * sizeof(wchar_t);

    bool opened = false;
    for (int i = 0; i < 10 && !opened; i++) {
        if (OpenClipboard(nullptr)) opened = true;
        else Sleep(5);
    }
    if (!opened) return;

    EmptyClipboard();

    HGLOBAL mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
    if (mem) {
        void* p = GlobalLock(mem);
        if (p) {
            memcpy(p, crlf.c_str(), bytes);
            GlobalUnlock(mem);
            // SetClipboardData 成功后所有权归系统，不能再 GlobalFree
            if (!SetClipboardData(CF_UNICODETEXT, mem)) GlobalFree(mem);
        } else {
            GlobalFree(mem);
        }
    }

    CloseClipboard();
}

std::string clipboardGet() {
    bool opened = false;
    for (int i = 0; i < 10 && !opened; i++) {
        if (OpenClipboard(nullptr)) opened = true;
        else Sleep(5);
    }
    if (!opened) return "";

    std::string out;
    HANDLE h = GetClipboardData(CF_UNICODETEXT);
    if (h) {
        const wchar_t* p = (const wchar_t*)GlobalLock(h);
        if (p) {
            std::wstring w(p);
            GlobalUnlock(h);
            // CRLF 归一成 LF。编辑器内部只认 LF。
            std::wstring lf;
            lf.reserve(w.size());
            for (size_t i = 0; i < w.size(); i++) {
                if (w[i] == L'\r' && i + 1 < w.size() && w[i + 1] == L'\n')
                    continue;
                lf += w[i];
            }
            out = toUtf8(lf);
        }
    }
    CloseClipboard();
    return out;
}

// ---------------------------------------------------------------- 进程

int runCommand(const std::string& cmd, const std::vector<std::string>& args,
               const std::string& workDir, std::string* output) {
    // 拼命令行。CreateProcess 要求整个命令行是一个字符串，
    // 所以每个参数自己加引号。
    std::string line = cmd;
    for (const auto& a : args) {
        line += " \"";
        for (char c : a) {
            if (c == '"') line += "\\\"";
            else line += c;
        }
        line += "\"";
    }

    SECURITY_ATTRIBUTES sa{};
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;

    HANDLE rd = nullptr, wr = nullptr;
    if (output) {
        if (!CreatePipe(&rd, &wr, &sa, 0)) return -1;
        SetHandleInformation(rd, HANDLE_FLAG_INHERIT, 0);
    }

    STARTUPINFOA si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = wr ? wr : GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = wr ? wr : GetStdHandle(STD_ERROR_HANDLE);
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);

    PROCESS_INFORMATION pi{};
    std::vector<char> mutableLine(line.begin(), line.end());
    mutableLine.push_back('\0');

    BOOL ok = CreateProcessA(
        nullptr, mutableLine.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW,
        nullptr, workDir.empty() ? nullptr : workDir.c_str(), &si, &pi);

    if (wr) CloseHandle(wr);

    if (!ok) {
        if (rd) CloseHandle(rd);
        return -1;
    }

    // 读输出，直到子进程退出
    if (output) {
        char buf[4096];
        DWORD got = 0;
        while (ReadFile(rd, buf, sizeof(buf), &got, nullptr) && got > 0) {
            output->append(buf, got);
        }
        CloseHandle(rd);
    }

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}

// 跑一条命令字符串，stdout + stderr 一起返回。
// 和 runCommand 的区别：这个不收参数数组、不要退出码，
// 给「问一下 xxx -version」这类场景用。
// 注意 javac -version 的输出是走 stderr 的，所以两个都要收。
std::string runCapture(const std::string& cmd) {
    std::string out;
    // cmd.exe 会把 stderr 重定向到 stdout，一次收齐
    runCommand("cmd.exe", {"/c", cmd}, "", &out);
    if (!out.empty() && out.back() == '\n') out.pop_back();
    if (!out.empty() && out.back() == '\r') out.pop_back();
    return out;
}

}  // namespace plat
}  // namespace spp
