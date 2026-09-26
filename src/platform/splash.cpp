// splash.cpp —— IDEA 风格的启动画面
//
// 一个 560x340 的无边框圆角小窗，中间是产品名和版本，下面是进度条。
// 见到主窗口出来之前先看它，拖得动、能刷新、不会「未响应」。
//
// ------------------------------------------------------------------
// 为什么用「分层窗口 + UpdateLayeredWindow」而不是普通窗口
// ------------------------------------------------------------------
// 用户要的是「小圆角窗口」。普通 CreateWindow 做圆角有两个麻烦：
//   1) 四角外面是方的。要么留一块白的（丑），要么自己裁剪区域
//      （SetWindowRgn 是硬边缘，圆弧上全是台阶）。
//   2) 没法投阴影。Windows 的窗口阴影只给 DWM 标准边框，
//      自绘窗口拿不到。
// 分层窗口直接把一张 32 位 ARGB 每像素 alpha 的图画上去：
// 圆角外面 alpha=0 就是全透明，边缘靠 AA 算出来的半透明像素
// 过渡，DWM 自动按 alpha 打柔和阴影。这是唯一能做到「真圆角 +
// 带阴影」的路子，Chromium、VS Code 的启动画面都这么干。
//
// ------------------------------------------------------------------
// 为什么自己写 GDI 绘制而不复用 win32.cpp 那套
// ------------------------------------------------------------------
// win32.cpp 的背缓冲是 32 位但**没有 alpha 通道**（它用的是
// blendPx 做覆盖混合，写出来的永远是 0xFF alpha）。
// 分层窗口要的正是 alpha，所以这里单独维护一张 DIB，
// 每个像素记 (R,G,B,A)，绘制阶段只改 RGB 和累积 alpha，
// 最后一次性交出去。
//
// 另外这个文件是**同步**的：createSplash 建好窗口就返回，
// 调用方拿着句柄调 splashStep 推进进度，中间可以去做真正
// 耗时的初始化（扫 JDK、读图标库、建索引）。每步内部会 Pump
// 一遍消息，所以拖动、最小化都正常。
#include "platform/platform.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <windowsx.h>   // GET_X_LPARAM / GET_Y_LPARAM

#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "icons/iconlib.h"   // 品牌图标的几何从这里来，不在这儿手画

namespace spp {
namespace plat {

namespace {

// ---------------------------------------------------------------- 小工具

// 0xRRGGBB → COLORREF(0x00BBGGRR)
inline COLORREF toRef(Color c) {
    return RGB((BYTE)red(c), (BYTE)green(c), (BYTE)blue(c));
}

std::wstring toWide(const std::string& utf8) {
    if (utf8.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(),
                                nullptr, 0);
    std::wstring w((size_t)n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), (int)utf8.size(), &w[0], n);
    return w;
}

// 字体缓存。CreateFont 在启动阶段挺贵的，同一个 key 只建一次。
struct FontKey {
    int size;
    bool bold;
    bool mono;
    bool operator==(const FontKey& o) const {
        return size == o.size && bold == o.bold && mono == o.mono;
    }
};

struct FontEntry {
    FontKey key;
    HFONT font;
};

std::vector<FontEntry> g_splashFonts;

HFONT splashFont(int size, bool bold, bool mono = false) {
    FontKey k{size, bold, mono};
    for (auto& e : g_splashFonts) {
        if (e.key == k) return e.font;
    }
    const wchar_t* face = mono ? L"Consolas" : L"Microsoft YaHei UI";
    HFONT f = CreateFontW(-size, 0, 0, 0, bold ? FW_SEMIBOLD : FW_NORMAL,
                          FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                          OUT_TT_PRECIS, CLIP_DEFAULT_PRECIS,
                          ANTIALIASED_QUALITY,   // 灰度抗锯齿，不留彩边
                          DEFAULT_PITCH | FF_DONTCARE, face);
    g_splashFonts.push_back({k, f});
    return f;
}

// ---------------------------------------------------------------- ARGB 画布
//
// 每像素 uint32，内存布局是 BGRA（小端），和 Windows 的
// BITMAPINFO 里 BI_RGB 32bpp 一致。
// 用 A<<24 | R<<16 | G<<8 | B 存。

struct ArgCanvas {
    int w = 0, h = 0;
    std::vector<uint32_t> px;
};

struct SplashState {
    HWND hwnd = nullptr;
    ArgCanvas cv;
    HDC memDc = nullptr;
    HBITMAP bmp = nullptr;
    uint32_t* bits = nullptr;
    int w = 0, h = 0;
    bool valid = false;

    // ---- 就绪状态下的两个按钮
    //
    // 准备完成后窗口停住等用户点：进还是退。
    // 按钮只有进了就绪态才生效，加载阶段点了没反应。
    bool ready = false;      // 是不是已经进入「准备就绪」
    int btnEnter[4] = {0, 0, 0, 0};   // x, y, w, h
    int btnQuit[4] = {0, 0, 0, 0};
    int hoverBtn = 0;        // 0=没有 1=立即进入 2=退出
    bool finished = false;   // 用户点过了
    int result = 0;          // 1=立即进入  2=退出
};

SplashState g_sp;

// 往画布上混一个像素。cov 是覆盖率 0..256。
inline void putPx(ArgCanvas& cv, int x, int y, int r, int g, int b, int a) {
    if (!cv.w || !cv.h) return;
    if (x < 0 || y < 0 || x >= cv.w || y >= cv.h) return;
    if (a <= 0) return;

    // 按覆盖率折算这次要涂多少
    int sa = a * 256 / 255;
    if (sa > 256) sa = 256;

    uint32_t& p = cv.px[(size_t)y * cv.w + x];
    int pb = (int)(p & 0xFF);
    int pg = (int)((p >> 8) & 0xFF);
    int pr = (int)((p >> 16) & 0xFF);
    int pa = (int)((p >> 24) & 0xFF);

    // 源 alpha = sa/256，目标已有 alpha = pa
    // 标准 source-over：
    //   outA = sa + pa*(1-sa)
    //   outC = (C*sa + Cb*pa*(1-sa)) / outA
    int inv = 256 - sa;
    int outA = sa + pa * inv / 256;
    // 夹到 255。
    //
    // 这个夹取不是保险，是必须：sa 取 256 时 outA 也算成 256，
    // 而 alpha 只有一个字节 —— (256 << 24) 在 uint32 里溢出成 0。
    // 结果就是「不透明的实心区反而变成全透明」，整张启动画面只剩
    // 一圈抗锯齿边缘能看见（就是用户说的「半透明」）。
    // 所以这里必须压回 255，不能让它溢出。
    if (outA > 255) outA = 255;
    if (outA <= 0) return;

    int nr = (r * sa + pr * pa * inv / 256) / outA;
    int ng = (g * sa + pg * pa * inv / 256) / outA;
    int nb = (b * sa + pb * pa * inv / 256) / outA;

    p = ((uint32_t)outA << 24) | ((uint32_t)(nr > 255 ? 255 : nr) << 16) |
        ((uint32_t)(ng > 255 ? 255 : ng) << 8) |
        (uint32_t)(nb > 255 ? 255 : nb);
}

// 点是否在圆角矩形里。改自 win32.cpp 的同名函数，
// 参数语义一致，半径自动夹到半宽/半高。
inline bool insideRR(double px, double py, double x, double y, double w,
                     double h, double r) {
    if (px < x || py < y || px > x + w || py > y + h) return false;
    double rmax = (w < h ? w : h) * 0.5;
    if (r > rmax) r = rmax;
    if (r <= 0.0001) return true;
    double cx, cy;
    if (px < x + r) {
        if (py < y + r) { cx = x + r; cy = y + r; }
        else if (py > y + h - r) { cx = x + r; cy = y + h - r; }
        else return true;
    } else if (px > x + w - r) {
        if (py < y + r) { cx = x + w - r; cy = y + r; }
        else if (py > y + h - r) { cx = x + w - r; cy = y + h - r; }
        else return true;
    } else {
        return true;
    }
    double dx = px - cx, dy = py - cy;
    return (dx * dx + dy * dy) <= r * r * 1.0002;
}

// 子采样密度。和 win32.cpp 保持一致 —— 启动画面是用户看到的
// 第一眼，圆角和进度条要是带台阶就太掉价了。
constexpr int kSub = 8;
constexpr int kSubInv = kSub * kSub;

// 抗锯齿圆角矩形填充
void aaRect(ArgCanvas& cv, double x, double y, double w, double h, double r,
            Color col, int alpha) {
    if (w <= 0 || h <= 0 || alpha <= 0) return;
    int cr = (int)red(col), cg = (int)green(col), cb = (int)blue(col);

    int ix0 = (int)std::floor(x), iy0 = (int)std::floor(y);
    int ix1 = (int)std::ceil(x + w), iy1 = (int)std::ceil(y + h);
    if (ix0 < 0) ix0 = 0;
    if (iy0 < 0) iy0 = 0;
    if (ix1 > cv.w) ix1 = cv.w;
    if (iy1 > cv.h) iy1 = cv.h;

    const double step = 1.0 / kSub, off = step * 0.5;

    for (int py = iy0; py < iy1; py++) {
        for (int px = ix0; px < ix1; px++) {
            // 内部实心区直接填，省 16 次判断
            double rmax = (w < h ? w : h) * 0.5;
            double rr = r > rmax ? rmax : r;
            if (insideRR(px + 0.5, py + 0.5, x, y, w, h, r) &&
                insideRR(px + 0.5, py + 0.5, x + rr, y + rr, w - 2 * rr,
                         h - 2 * rr, 0)) {
                putPx(cv, px, py, cr, cg, cb, alpha);
                continue;
            }
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                for (int sx = 0; sx < kSub; sx++) {
                    if (insideRR(px + off + sx * step, py + off + sy * step, x,
                                 y, w, h, r))
                        hit++;
                }
            }
            if (!hit) continue;
            putPx(cv, px, py, cr, cg, cb, alpha * hit / kSubInv);
        }
    }
}

// 抗锯齿圆角矩形**描边**：外轮廓减内轮廓
//
// 注意参数顺序：**thick 在 col 前面**。
//
// 这里踩过一次坑，代价不小：调用处按「颜色在前、粗细在后」的直觉传，
// 结果 thick 收到了颜色值（0x3A3C41 ≈ 380 万），col 收到了 1。
// thick 这么大，内轮廓宽度算成负数，`inner` 永远是 false —— 于是
// 「描边」把整个矩形当成边框全涂了：一层近黑色盖满整张画面，
// 就是用户看到的「窗口半透明」。而且每帧盖一次，越盖越黑，
// 最后整张图都变成 (0,0,1)。
//
// 改签名会牵动一堆调用点，所以顺序不动，只在注释里钉死。
void aaRectStroke(ArgCanvas& cv, double x, double y, double w, double h,
                  double r, double thick, Color col, int alpha) {
    if (w <= 0 || h <= 0 || thick <= 0 || alpha <= 0) return;
    int cr = (int)red(col), cg = (int)green(col), cb = (int)blue(col);

    double hx = x - thick * 0.5, hy = y - thick * 0.5;
    double hw = w + thick, hh = h + thick, hr = r + thick * 0.5;
    double ix = x + thick * 0.5, iy = y + thick * 0.5;
    double iw = w - thick, ih = h - thick, ir = r - thick * 0.5;
    if (ir < 0) ir = 0;

    int ix0 = (int)std::floor(hx), iy0 = (int)std::floor(hy);
    int ix1 = (int)std::ceil(hx + hw), iy1 = (int)std::ceil(hy + hh);
    if (ix0 < 0) ix0 = 0;
    if (iy0 < 0) iy0 = 0;
    if (ix1 > cv.w) ix1 = cv.w;
    if (iy1 > cv.h) iy1 = cv.h;

    const double step = 1.0 / kSub, off = step * 0.5;

    for (int py = iy0; py < iy1; py++) {
        for (int px = ix0; px < ix1; px++) {
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step;
                    double fy = py + off + sy * step;
                    bool outer = insideRR(fx, fy, hx, hy, hw, hh, hr);
                    bool inner = (iw > 0 && ih > 0) &&
                                 insideRR(fx, fy, ix, iy, iw, ih, ir);
                    if (outer && !inner) hit++;
                }
            }
            if (!hit) continue;
            putPx(cv, px, py, cr, cg, cb, alpha * hit / kSubInv);
        }
    }
}

// 抗锯齿实心圆
void aaCircle(ArgCanvas& cv, double cx, double cy, double rad, Color col,
              int alpha) {
    if (rad <= 0 || alpha <= 0) return;
    int cr = (int)red(col), cg = (int)green(col), cb = (int)blue(col);

    int ix0 = (int)std::floor(cx - rad), iy0 = (int)std::floor(cy - rad);
    int ix1 = (int)std::ceil(cx + rad), iy1 = (int)std::ceil(cy + rad);
    if (ix0 < 0) ix0 = 0;
    if (iy0 < 0) iy0 = 0;
    if (ix1 > cv.w) ix1 = cv.w;
    if (iy1 > cv.h) iy1 = cv.h;

    const double step = 1.0 / kSub, off = step * 0.5;

    for (int py = iy0; py < iy1; py++) {
        for (int px = ix0; px < ix1; px++) {
            double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
            if (dx * dx + dy * dy <= (rad - 0.71) * (rad - 0.71)) {
                putPx(cv, px, py, cr, cg, cb, alpha);
                continue;
            }
            int hit = 0;
            for (int sy = 0; sy < kSub; sy++) {
                for (int sx = 0; sx < kSub; sx++) {
                    double fx = px + off + sx * step - cx;
                    double fy = py + off + sy * step - cy;
                    if (fx * fx + fy * fy <= rad * rad) hit++;
                }
            }
            if (!hit) continue;
            putPx(cv, px, py, cr, cg, cb, alpha * hit / kSubInv);
        }
    }
}

// 在画布上画一段文本。用 GDI 的 TextOutW 画到一张临时 32 位
// DIB 上再搬过来 —— 比手工光栅化字形省事，抗锯齿由字体引擎给。
//
// 这里有个坑，踩过一次：
//   量宽度用的是 tmp DC（里面选好了 splashFont），但真正 TextOutW
//   画的是 mem DC。如果忘了把同一个字体也选进 mem，mem 会用系统
//   默认字体（更大更宽），文字就会超出按 splashFont 算出的盒子 ——
//   表现是每个字符串的**尾部被裁掉**（"26.1-Test" 变成 "26.1-Te"）。
//   所以下面量完宽度后，mem 也要 SelectObject 一次同一个字体。
void drawText(ArgCanvas& cv, int x, int y, const std::string& utf8,
              const TextStyle& st) {
    if (utf8.empty()) return;
    std::wstring w = toWide(utf8);
    if (w.empty()) return;

    HFONT f = splashFont(st.size, st.bold, st.monospace);

    // 量一下需要多大
    HDC tmp = CreateCompatibleDC(nullptr);
    HGDIOBJ of = SelectObject(tmp, f);

    SIZE sz{};
    GetTextExtentPoint32W(tmp, w.c_str(), (int)w.size(), &sz);
    // +6 是留给字形右侧外框溢出和斜体的余量。只留 2px 时
    // 粗体最后一个字母的右边会被切掉一点。
    int tw = (int)sz.cx + 6, th = (int)sz.cy + 6;
    if (tw <= 0 || th <= 0) {
        SelectObject(tmp, of);
        DeleteDC(tmp);
        return;
    }

    // 临时 DIB：GDI 在上面画黑底白字，我们按亮度取 alpha
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = tw;
    bi.bmiHeader.biHeight = -th;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    HDC mem = CreateCompatibleDC(tmp);
    HBITMAP bmp = CreateDIBSection(mem, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
    HGDIOBJ ob = SelectObject(mem, bmp);
    // 关键：mem 也要用同一个字体，否则文字会溢出盒子被裁掉
    HGDIOBJ of2 = SelectObject(mem, f);

    // 底涂黑，字涂白 —— 这样每个像素的 R 通道就是覆盖率
    RECT r{0, 0, tw, th};
    HBRUSH black = (HBRUSH)GetStockObject(BLACK_BRUSH);
    FillRect(mem, &r, black);
    SetBkMode(mem, TRANSPARENT);
    SetTextColor(mem, RGB(255, 255, 255));
    TextOutW(mem, 3, 3, w.c_str(), (int)w.size());
    GdiFlush();

    int cr = (int)red(st.color), cg = (int)green(st.color),
        cb = (int)blue(st.color);

    if (bits) {
        const uint32_t* src = (const uint32_t*)bits;
        for (int py = 0; py < th; py++) {
            for (int pxi = 0; pxi < tw; pxi++) {
                // DIB 是 BGRA，取到的是 0x00RRGGBB
                uint32_t v = src[(size_t)py * tw + pxi];
                int cov = (int)((v >> 16) & 0xFF);   // 白字 → R 通道
                if (cov <= 2) continue;
                putPx(cv, x - 3 + pxi, y - 3 + py, cr, cg, cb, cov);
            }
        }
    }

    SelectObject(mem, of2);
    SelectObject(mem, ob);
    DeleteObject(bmp);
    DeleteDC(mem);
    SelectObject(tmp, of);
    DeleteDC(tmp);
}

void measureText(HDC dc, const std::string& utf8, const TextStyle& st,
                 int* outW, int* outH) {
    std::wstring w = toWide(utf8);
    HFONT f = splashFont(st.size, st.bold, st.monospace);
    HGDIOBJ of = SelectObject(dc, f);
    SIZE sz{};
    GetTextExtentPoint32W(dc, w.c_str(), (int)w.size(), &sz);
    SelectObject(dc, of);
    if (outW) *outW = (int)sz.cx;
    if (outH) *outH = (int)sz.cy;
}

// ---------------------------------------------------------------- 截图

// 把 ARGB 画布写成 32 位 BMP。
//
// 这里偷了个懒：不做 alpha 预乘还原，直接把 BGRA 原样写出去。
// 看启动画面「形状对不对、进度条有没有、圆角够不够圆」够用了。
// 背景是透明的（alpha=0）那些像素在 BMP 里会变成纯黑，
// 所以截图上圆角外面是一圈黑 —— 那是透明区，不是画错了。
static void writeBmp32(const char* path, const ArgCanvas& cv) {
    int w = cv.w, h = cv.h;
    if (w <= 0 || h <= 0 || cv.px.empty()) return;

    BITMAPFILEHEADER fh{};
    BITMAPINFOHEADER bi{};
    bi.biSize = sizeof(bi);
    bi.biWidth = w;
    bi.biHeight = -h;      // 负号 = 自上而下
    bi.biPlanes = 1;
    bi.biBitCount = 32;
    bi.biCompression = BI_RGB;

    DWORD pixBytes = (DWORD)w * h * 4;
    fh.bfType = 0x4D42;
    fh.bfOffBits = sizeof(fh) + sizeof(bi);
    fh.bfSize = fh.bfOffBits + pixBytes;

    FILE* fp = fopen(path, "wb");
    if (!fp) return;
    fwrite(&fh, sizeof(fh), 1, fp);
    fwrite(&bi, sizeof(bi), 1, fp);
    fwrite(cv.px.data(), 1, pixBytes, fp);
    fclose(fp);
}

// ---------------------------------------------------------------- 窗口

// 窗口已经按 WS_EX_LAYERED 建好了，把画布推上去。
//
// UpdateLayeredWindow 要的全部参数：
//   hdcDst    = 屏幕 DC
//   pptDst    = 窗口左上角在屏幕上的位置
//   psize     = 窗口尺寸
//   hdcSrc    = 画好图的 DC
//   pptSrc    = 源图左上角（0,0）
//   crKey     = 透明色键（用 ULW_ALPHA 时要填 0）
//   pblend    = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA }
// 缺一不可，少一个就是「窗口全黑」或者「内容偏了」。
void present(SplashState& sp) {
    if (!sp.hwnd || !sp.memDc || !sp.bits) return;

    // 画布内容搬到 DIB
    if ((int)sp.cv.px.size() == sp.w * sp.h) {
        memcpy(sp.bits, sp.cv.px.data(),
               sizeof(uint32_t) * (size_t)(sp.w * sp.h));
    }

    // 截图钩子。和主窗口那边一个套路：设了环境变量就往那儿存一张。
    // 启动画面只在屏幕上活一两秒，事后没法回看，只能画的时候自己存。
    {
        // 抓两张：第一张是加载中，第二张（加 _ready 后缀）是就绪态。
        // 就绪态会停住等点击，手动截图抓不到，只能靠这个钩子。
        static int shotDone = 0;
        const char* sp2 = getenv("SPLASH_SHOT");
        if (sp2 && *sp2) {
            if (shotDone == 0) {
                shotDone = 1;
                writeBmp32(sp2, sp.cv);
            } else if (shotDone == 1 && sp.ready) {
                shotDone = 2;
                std::string base = sp2;
                size_t dot = base.rfind('.');
                std::string rp = (dot == std::string::npos)
                                     ? base + "_ready"
                                     : base.substr(0, dot) + "_ready" +
                                           base.substr(dot);
                writeBmp32(rp.c_str(), sp.cv);
            }
        }
    }

    RECT rc;
    GetWindowRect(sp.hwnd, &rc);
    POINT ptDst{rc.left, rc.top};
    POINT ptSrc{0, 0};
    SIZE sz{sp.w, sp.h};

    HDC screen = GetDC(nullptr);
    BLENDFUNCTION bf{};
    bf.BlendOp = AC_SRC_OVER;
    bf.SourceConstantAlpha = 255;
    bf.AlphaFormat = AC_SRC_ALPHA;
    UpdateLayeredWindow(sp.hwnd, screen, &ptDst, &sz, sp.memDc, &ptSrc, 0, &bf,
                        ULW_ALPHA);
    ReleaseDC(nullptr, screen);
}

// 点在哪个按钮上。0 = 没中。
//
// 坐标一律按**客户区坐标**算（画布就是客户区，两者同一套原点）。
// 之所以要把这件事写死，是因为 Win32 给的坐标不是统一的：
//
//     消息                lParam 里是什么
//     WM_NCHITTEST        屏幕坐标
//     WM_MOUSEMOVE        客户区坐标
//     WM_LBUTTONDOWN/UP   客户区坐标
//
// 上一版把鼠标消息也当成屏幕坐标处理，于是又减了一次窗口原点
// （窗口在屏幕中间，原点差不多是 680,370）—— 命中点被平移到
// 左上角外头老远，按钮永远点不中；而且没点中按钮就会走「拖标题栏」
// 分支，鼠标一按窗口跟着跑。表现就是「卡在这儿，点哪个都不行」。
//
// WM_NCHITTEST 那条路本来是对的，但让按钮区返回 HTCLIENT 只是
// 让消息能发下来，真正的判定还得靠对的坐标。
static int hitButtonClient(HWND hwnd, int cx, int cy) {
    (void)hwnd;
    if (!g_sp.ready) return 0;

    auto in = [&](const int* b) {
        return cx >= b[0] && cx < b[0] + b[2] && cy >= b[1] && cy < b[1] + b[3];
    };
    if (in(g_sp.btnEnter)) return 1;
    if (in(g_sp.btnQuit)) return 2;
    return 0;
}

// WM_NCHITTEST 专用：进来的是屏幕坐标，先换成客户区坐标
static int hitButtonScreen(HWND hwnd, int sx, int sy) {
    POINT pt{sx, sy};
    ScreenToClient(hwnd, &pt);
    return hitButtonClient(hwnd, pt.x, pt.y);
}

// ---------------------------------------------------------------- 品牌图标
//
// 图标的几何来自 icons::parseSvg()，也就是用户那套 177 个线性图标。
// 这里只负责把几何光栅化到自己的 ARGB 画布上 —— **不在这里设计图形**。
//
// 为什么不能直接调 icons::draw()：那个函数画进 spp::plat::Canvas，
// 底下是主窗口的 GDI 位图，画出来的像素 alpha 恒为 255（不透明），
// 而这张画布要的是每像素 alpha。所以只能自己走一遍光栅化，
// 但输入是图标库的路径，不是手写的坐标。
std::vector<spp::icons::Shape> g_brand;

// 点到线段的距离。拿它当「笔刷」来算覆盖率 —— 圆形端点和拐角
// 都自然成立，不用额外处理。
static float distToSeg(float px, float py, float x1, float y1, float x2,
                       float y2) {
    float dx = x2 - x1, dy = y2 - y1;
    float len2 = dx * dx + dy * dy;
    float t = 0.0f;
    if (len2 > 1e-9f) {
        t = ((px - x1) * dx + (py - y1) * dy) / len2;
        if (t < 0.0f) t = 0.0f;
        else if (t > 1.0f) t = 1.0f;
    }
    float qx = x1 + t * dx - px;
    float qy = y1 + t * dy - py;
    return std::sqrt(qx * qx + qy * qy);
}

// 抗锯齿线段。
//
// 做法：每个像素切成 kSub*kSub 个小格，数有多少格落在笔宽内，
// 用这个比例当覆盖率。比解析式求交好写，端点、拐角、粗细变化
// 全都是同一个判定，不会出现某一类边漏画。
static void aaSeg(ArgCanvas& cv, float x1, float y1, float x2, float y2,
                  float thick, Color col, int alpha) {
    const int kSub = 5;                 // 25 级覆盖，够了
    const int kTotal = kSub * kSub;
    const float half = thick * 0.5f;

    float minx = std::min(x1, x2) - half - 1.0f;
    float maxx = std::max(x1, x2) + half + 1.0f;
    float miny = std::min(y1, y2) - half - 1.0f;
    float maxy = std::max(y1, y2) + half + 1.0f;

    int x0 = (int)std::floor(minx), x1i = (int)std::ceil(maxx);
    int y0 = (int)std::floor(miny), y1i = (int)std::ceil(maxy);
    if (x0 < 0) x0 = 0;
    if (y0 < 0) y0 = 0;
    if (x1i > cv.w) x1i = cv.w;
    if (y1i > cv.h) y1i = cv.h;

    int r = (int)red(col), g = (int)green(col), b = (int)blue(col);

    for (int py = y0; py < y1i; py++) {
        for (int px = x0; px < x1i; px++) {
            int cov = 0;
            for (int sy = 0; sy < kSub; sy++) {
                float fy = py + (sy + 0.5f) / kSub;
                for (int sx = 0; sx < kSub; sx++) {
                    float fx = px + (sx + 0.5f) / kSub;
                    if (distToSeg(fx, fy, x1, y1, x2, y2) <= half) cov++;
                }
            }
            if (cov) putPx(cv, px, py, r, g, b, alpha * cov / kTotal);
        }
    }
}

// 把一组图形（24x24 设计坐标）画到画布上。
// 缩放、笔宽、内缩都和 icons::draw() 一致，保证两处看起来是同一个图标。
static void drawShapes(ArgCanvas& cv, const std::vector<spp::icons::Shape>& sh,
                       int x, int y, int size, Color col, int alpha) {
    if (sh.empty() || size <= 0) return;

    const float kDesign = 24.0f;
    float k = (float)size / kDesign;
    float thick = 2.0f * k;
    if (thick < 0.85f) thick = 0.85f;
    float ox = (float)x, oy = (float)y;

    for (const auto& s : sh) {
        switch (s.kind) {
            case spp::icons::ShapeKind::Path:
            case spp::icons::ShapeKind::Polyline:
            case spp::icons::ShapeKind::Polygon: {
                bool closed = s.closed || s.kind == spp::icons::ShapeKind::Polygon;
                for (size_t i = 0; i + 3 < s.pts.size(); i += 2) {
                    aaSeg(cv, ox + s.pts[i] * k, oy + s.pts[i + 1] * k,
                          ox + s.pts[i + 2] * k, oy + s.pts[i + 3] * k, thick,
                          col, alpha);
                }
                if (closed && s.pts.size() >= 4) {
                    size_t n = s.pts.size();
                    aaSeg(cv, ox + s.pts[n - 2] * k, oy + s.pts[n - 1] * k,
                          ox + s.pts[0] * k, oy + s.pts[1] * k, thick, col,
                          alpha);
                }
                break;
            }
            case spp::icons::ShapeKind::Line: {
                if (s.pts.size() < 4) break;
                aaSeg(cv, ox + s.pts[0] * k, oy + s.pts[1] * k,
                      ox + s.pts[2] * k, oy + s.pts[3] * k, thick, col, alpha);
                break;
            }
            case spp::icons::ShapeKind::Circle: {
                // 40 段折线，和 icons::draw() 里一样
                const int seg = 40;
                float cx = ox + s.cx * k, cy = oy + s.cy * k, r = s.r * k;
                float prevX = cx + r, prevY = cy;
                for (int i = 1; i <= seg; i++) {
                    float a = 6.2831853f * (float)i / seg;
                    float nx = cx + r * std::cos(a);
                    float ny = cy + r * std::sin(a);
                    aaSeg(cv, prevX, prevY, nx, ny, thick, col, alpha);
                    prevX = nx;
                    prevY = ny;
                }
                break;
            }
            case spp::icons::ShapeKind::Rect: {
                float x0 = ox + s.cx * k, y0 = oy + s.cy * k;
                float rw = s.rw * k, rh = s.rh * k;
                if (s.rx > 0.5f) {
                    aaRectStroke(cv, x0, y0, rw, rh, s.rx * k, thick, col,
                                 alpha);
                } else {
                    aaSeg(cv, x0, y0, x0 + rw, y0, thick, col, alpha);
                    aaSeg(cv, x0 + rw, y0, x0 + rw, y0 + rh, thick, col, alpha);
                    aaSeg(cv, x0 + rw, y0 + rh, x0, y0 + rh, thick, col, alpha);
                    aaSeg(cv, x0, y0 + rh, x0, y0, thick, col, alpha);
                }
                break;
            }
        }
    }
}

// 分层窗口的窗口过程。它自己不做绘制，但桌面拖动 /
// 任务栏预览这类消息必须有，不然系统会认为窗口无响应。
LRESULT CALLBACK splashProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
        case WM_ERASEBKGND:
            return 1;   // 分层窗口不接受系统擦背景

        case WM_NCHITTEST: {
            // 屏幕上任意处可拖 —— 但**按钮除外**。
            //
            // 返回 HTCAPTION 是让无边框窗口能拖着走的标准做法，
            // 代价是系统在那个区域直接接管鼠标，WM_LBUTTONUP 不再
            // 发给我们。按钮区域必须返回 HTCLIENT，否则按不下去。
            int sx = GET_X_LPARAM(lp), sy = GET_Y_LPARAM(lp);
            if (hitButtonScreen(hwnd, sx, sy)) return HTCLIENT;
            return HTCAPTION;
        }

        case WM_MOUSEMOVE: {
            // 客户区坐标
            int b = hitButtonClient(hwnd, GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
            if (b != g_sp.hoverBtn) {
                g_sp.hoverBtn = b;
                // 悬停高亮要立刻重画，不然得等下一帧
                if (g_sp.ready &&
                    IsWindowVisible(hwnd)) {
                    splashStep(nullptr, 1.0f, "准备就绪");
                }
            }
            return 0;
        }

        case WM_LBUTTONDOWN:
            // 按钮上按下时不发起拖动，交给 WM_LBUTTONUP 处理
            if (hitButtonClient(hwnd, GET_X_LPARAM(lp), GET_Y_LPARAM(lp)))
                return 0;
            // 拖着走。ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN)
            // 是拖无边框窗口的标准写法。
            ReleaseCapture();
            SendMessageW(hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            return 0;

        case WM_LBUTTONUP: {
            // 客户区坐标
            int b = hitButtonClient(hwnd, GET_X_LPARAM(lp), GET_Y_LPARAM(lp));
            if (b) {
                g_sp.result = b;
                g_sp.finished = true;
            }
            return 0;
        }

        case WM_RBUTTONUP:
            // 兜底出口。这个窗口是 WS_EX_NOACTIVATE，抢不到键盘焦点，
            // 所以 Enter / Esc 都按不动 —— 万一按钮那条路又出什么问题，
            // 用户至少还有一下右键能进去，不至于只能去任务管理器。
            g_sp.result = 1;
            g_sp.finished = true;
            return 0;

        case WM_CLOSE:
            // 被关掉（Alt+F4）就当「立即进入」，然后收窗口。
            //
            // 这里**必须**把 finished 置上。上一版只 ShowWindow(SW_HIDE)，
            // 而 splashReady 的等待循环只看 finished —— 于是窗口藏了、
            // 循环还在转，整个程序就那么卡死了。
            g_sp.result = 1;
            g_sp.finished = true;
            ShowWindow(hwnd, SW_HIDE);
            return 0;

        case WM_DESTROY:
            return 0;

        default:
            break;
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

void pump() {
    MSG m;
    while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
}

}  // namespace

void splashSetBrandIcon(const std::string& svg) {
    g_brand.clear();
    if (svg.empty()) return;
    g_brand = spp::icons::parseSvg(svg);
}

// ---------------------------------------------------------------- 公开 API

void* createSplash(int w, int h) {
    if (w <= 0 || h <= 0) return nullptr;
    if (g_sp.hwnd) return g_sp.hwnd;

    static bool reg = false;
    if (!reg) {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = 0;
        wc.lpfnWndProc = splashProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursorW(nullptr, (LPCWSTR)IDC_ARROW);
        wc.hbrBackground = nullptr;
        wc.lpszClassName = L"JavaStudioSplash";
        if (!RegisterClassExW(&wc)) {
            SPP_LOG_ERR("启动画面窗口类注册失败");
            return nullptr;
        }
        reg = true;
    }

    int sw = GetSystemMetrics(SM_CXSCREEN);
    int sh = GetSystemMetrics(SM_CYSCREEN);
    int x = (sw - w) / 2;
    int y = (sh - h) / 2;

    HWND hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
        L"JavaStudioSplash", L"Java Studio", WS_POPUP, x, y, w, h, nullptr,
        nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!hwnd) {
        SPP_LOG_ERR("启动画面窗口创建失败");
        return nullptr;
    }
    // WS_EX_NOACTIVATE 让它不抢焦点，但用户还是能拖动、能关。

    // 画布
    g_sp.hwnd = hwnd;
    g_sp.w = w;
    g_sp.h = h;
    g_sp.cv.w = w;
    g_sp.cv.h = h;
    g_sp.cv.px.assign((size_t)w * h, 0);

    // 一张 32bpp 的 DIB 作为 UpdateLayeredWindow 的源
    HDC screen = GetDC(nullptr);
    g_sp.memDc = CreateCompatibleDC(screen);

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    void* bits = nullptr;
    g_sp.bmp = CreateDIBSection(g_sp.memDc, &bi, DIB_RGB_COLORS, &bits, nullptr,
                                0);
    SelectObject(g_sp.memDc, g_sp.bmp);
    g_sp.bits = (uint32_t*)bits;
    ReleaseDC(nullptr, screen);

    ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    g_sp.valid = true;
    return hwnd;
}

// 画底部两个按钮，并把它们的矩形记回状态里。
//
// 矩形必须在这里写回 SplashState —— 窗口过程要靠它做命中测试。
// 画和命中用同一份坐标，才不会出现「看着在按钮上但点不中」。
static void drawReadyButtons(SplashState& sp, int H) {
    const Color accent = rgb(232, 106, 51);
    const Color fgPri = rgb(236, 237, 240);
    const Color fgMut = rgb(150, 153, 160);
    const Color border = rgb(72, 75, 82);

    int bw = 118, bh = 34;
    int by = H - 20 - bh;
    int gap = 10;

    // 「退出」靠右，「立即进入」在它左边
    int qx = sp.w - 28 - bw;
    int ex = qx - gap - bw;

    sp.btnEnter[0] = ex;
    sp.btnEnter[1] = by;
    sp.btnEnter[2] = bw;
    sp.btnEnter[3] = bh;
    sp.btnQuit[0] = qx;
    sp.btnQuit[1] = by;
    sp.btnQuit[2] = bw;
    sp.btnQuit[3] = bh;

    // ---- 主按钮：实心强调色
    {
        bool hov = (sp.hoverBtn == 1);
        Color bg = hov ? rgb(244, 122, 66) : accent;
        aaRect(sp.cv, ex, by, bw, bh, 8, bg, 255);
        if (hov)
            aaRectStroke(sp.cv, ex + 0.5, by + 0.5, bw - 1, bh - 1, 8, 1,
                         rgb(255, 255, 255), 60);

        TextStyle st;
        st.size = 12;
        st.bold = true;
        st.color = rgb(30, 31, 34);

        int tw = 0, th = 0;
        HDC dc = CreateCompatibleDC(nullptr);
        measureText(dc, "立即进入", st, &tw, &th);
        DeleteDC(dc);
        drawText(sp.cv, ex + (bw - tw) / 2, by + (bh - th) / 2 + 1, "立即进入",
                 st);
    }

    // ---- 次按钮：描边
    {
        bool hov = (sp.hoverBtn == 2);
        aaRect(sp.cv, qx, by, bw, bh, 8,
               hov ? rgb(52, 54, 59) : rgb(40, 42, 46), 255);
        aaRectStroke(sp.cv, qx + 0.5, by + 0.5, bw - 1, bh - 1, 8, 1, border,
                     255);

        TextStyle st;
        st.size = 12;
        st.bold = false;
        st.color = hov ? fgPri : fgMut;

        int tw = 0, th = 0;
        HDC dc = CreateCompatibleDC(nullptr);
        measureText(dc, "退出", st, &tw, &th);
        DeleteDC(dc);
        drawText(sp.cv, qx + (bw - tw) / 2, by + (bh - th) / 2 + 1, "退出", st);
    }

    // ---- 左边一行提示
    {
        TextStyle st;
        st.size = 10;
        st.color = rgb(110, 113, 120);
        drawText(sp.cv, 28, by + (bh - 14) / 2 + 2, "© 2026 by.java IDEs", st);
    }
}

void splashStep(void* splash, float progress, const std::string& stage) {
    SplashState& sp = g_sp;
    if (!sp.hwnd || !sp.valid) return;
    if (!IsWindowVisible(sp.hwnd)) {
        // 用户关掉了就不再画，但消息还得抽，不然主线程里的
        // 初始化过程会让界面「未响应」
        pump();
        return;
    }

    if (progress < 0.0f) progress = 0.0f;
    if (progress > 1.0f) progress = 1.0f;
    (void)splash;

    // ---- 配色。启动画面用深色，和主界面一致。
    const Color cardBg = rgb(30, 31, 34);
    const Color border = rgb(58, 60, 65);
    const Color accent = rgb(232, 106, 51);   // Java 的橘
    const Color fgPri = rgb(236, 237, 240);
    const Color fgMut = rgb(150, 153, 160);
    const Color fgDim = rgb(102, 105, 112);
    const Color trackBg = rgb(52, 54, 59);

    int W = sp.w, H = sp.h;

    // 整张画布清成全透明
    std::fill(sp.cv.px.begin(), sp.cv.px.end(), 0u);

    // ---- 卡片本体
    aaRect(sp.cv, 0, 0, W, H, 14, cardBg, 255);

    // 顶部一条细的强调线，比整圈描边更克制
    aaRect(sp.cv, 1, 1, W - 2, 3, 2, accent, 200);
    aaRectStroke(sp.cv, 0.5, 0.5, W - 1, H - 1, 14, 1, border, 180);

    TextStyle st;

    // ---- 左上角：logo 方块 + 产品名
    //
    // 方块里的图案**来自图标库**（icons/coffee.svg），不是在这儿
    // 手画的。以前这里用几个矩形拼了个杯子，看着像图标其实不是，
    // 换个尺寸或者换个颜色就散架；而且图标库里本来就有现成的。
    {
        int lx = 28, ly = 26;
        aaRect(sp.cv, lx, ly, 40, 40, 11, accent, 255);

        if (!g_brand.empty()) {
            // 留 8px 内边距：40 的方块里画 24 的图标
            drawShapes(sp.cv, g_brand, lx + 8, ly + 8, 24, rgb(30, 31, 34),
                       240);
        }
    }

    {
        st.size = 21;
        st.bold = true;
        st.color = fgPri;
        drawText(sp.cv, 82, 30, "Java Studio", st);

        st.size = 11;
        st.bold = false;
        st.color = accent;
        drawText(sp.cv, 82, 56, "26.1-Test", st);

        st.size = 10;
        st.color = fgDim;
        drawText(sp.cv, 148, 57, "by.java IDEs", st);
    }

    // ---- 右上角：正在初始化的模块名（小字，能看出没卡住）
    {
        st.size = 10;
        st.bold = false;
        st.color = fgDim;
        int tw = 0, th = 0;
        // 用一个临时 DC 量宽度
        HDC dc = CreateCompatibleDC(nullptr);
        measureText(dc, "Java Studio", st, &tw, &th);
        DeleteDC(dc);
        (void)tw;
        (void)th;
    }

    // ---- 中间：状态文字
    {
        st.size = 12;
        st.bold = sp.ready;
        // 就绪之后用绿色。整个界面只有「错」是红色、「好」是绿色，
        // 这里跟着同一套语义，用户一眼能确认没事。
        st.color = sp.ready ? rgb(106, 194, 122) : fgMut;

        const char* msg = sp.ready ? "准备就绪" : (stage.empty() ? "正在启动…"
                                                                 : stage.c_str());
        drawText(sp.cv, 28, H - 118, msg, st);

        // 进度百分比，右对齐
        char pbuf[16];
        int pct = (int)(progress * 100.0f + 0.5f);
        snprintf(pbuf, sizeof(pbuf), "%d%%", pct);
        HDC dc = CreateCompatibleDC(nullptr);
        st.size = 11;
        st.color = fgDim;
        int tw = 0, th = 0;
        measureText(dc, pbuf, st, &tw, &th);
        DeleteDC(dc);
        drawText(sp.cv, W - 28 - tw, H - 118, pbuf, st);
    }

    // ---- 进度条
    {
        int bx = 28, bw = W - 56, bh = 6, by = H - 96;

        // 轨道
        aaRect(sp.cv, bx, by, bw, bh, bh / 2.0, trackBg, 255);

        // 已完成的段。进度极小时也给一小坨，让用户看到「在动」。
        int fw = (int)(bw * progress + 0.5);
        if (fw < 8) fw = 8;
        if (fw > bw) fw = bw;
        aaRect(sp.cv, bx, by, fw, bh, bh / 2.0, accent, 255);

        // 头上一颗亮点，滚动感来自这里
        aaCircle(sp.cv, bx + fw - bh / 2.0, by + bh / 2.0, bh / 2.0 + 1.5,
                 accent, 120);
    }

    // ---- 底部
    //
    // 加载中：两行说明小字。
    // 就绪后：换成「立即进入 / 退出」两个按钮，等用户点。
    if (!sp.ready) {
        st.size = 10;
        st.bold = false;
        st.color = fgDim;
        drawText(sp.cv, 28, H - 62,
                 "自带 JDK 17 / 21 / 25    ·    Maven / Gradle    ·    59 个第三方库",
                 st);

        st.size = 10;
        st.color = rgb(78, 80, 86);
        drawText(sp.cv, 28, H - 40, "© 2026 by.java IDEs", st);
    } else {
        drawReadyButtons(sp, H);
    }

    present(sp);
    pump();

    // 就绪后就不要再睡了 —— 用户在等按钮响应，多睡一帧都是迟滞。
    if (!sp.ready) {
        // 让启动过程真的「看得见」。加太多会拖慢启动，
        // 一帧 8ms 左右刚好 —— 进度条能看出来在走，总耗时又不明显。
        Sleep(8);
    }
}

// 就绪后停在窗口上等用户选。
//
// 返回值：1 = 立即进入，2 = 退出。
//
// 为什么要点一下才走：启动画面是「准备好了」的信号，直接闪一下就消失
// 的话，用户来不及看清用的是哪个 JDK、有没有报错。停在这里还顺带给了
// 一个「不进去了」的出口 —— 不然想退出只能去任务管理器杀进程。
int splashReady(void* splash) {
    (void)splash;
    SplashState& sp = g_sp;
    if (!sp.hwnd || !sp.valid) return 1;

    sp.ready = true;
    sp.finished = false;
    sp.result = 0;
    splashStep(nullptr, 1.0f, "准备就绪");

    // 一直抽消息直到用户点按钮。
    //
    // 这里必须 pump，不然窗口收不到点击，也收不到重绘 ——
    // 系统会把这个「占着主线程不处理消息」的窗口判成未响应。
    //
    // 循环里有两条逃生口，缺一不可：
    //   !IsWindow     窗口被销毁
    //   !IsWindowVisible  窗口被藏起来（WM_CLOSE / 别处把它 hide 了）
    // 只判前者的话，藏起来但没销毁的窗口会让这里转到天荒地老。
    while (!sp.finished) {
        pump();
        Sleep(16);
        if (!IsWindow(sp.hwnd)) break;
        if (!IsWindowVisible(sp.hwnd)) break;
    }

    int r = sp.result;
    if (r != 1 && r != 2) r = 1;
    return r;
}

void destroySplash(void* splash) {
    (void)splash;
    if (!g_sp.hwnd) return;

    // 淡出：把整张图的 alpha 乘性压低，分几步推上去。
    // 分层窗口不支持 SetLayeredWindowAttributes 和 UpdateLayeredWindow
    // 混用，所以还是走自己的画布。
    for (int k = 5; k >= 1; k--) {
        int mul = k * 200 / 5;   // 200 → 40 递减
        ArgCanvas& cv = g_sp.cv;
        for (auto& p : cv.px) {
            int a = (int)((p >> 24) & 0xFF);
            if (!a) continue;
            int na = a * mul / 200;
            p = ((uint32_t)na << 24) | (p & 0x00FFFFFFu);
        }
        present(g_sp);
        pump();
        Sleep(12);
    }

    DestroyWindow(g_sp.hwnd);
    if (g_sp.bmp) DeleteObject(g_sp.bmp);
    if (g_sp.memDc) DeleteDC(g_sp.memDc);

    g_sp = SplashState{};
    pump();
}

}  // namespace plat
}  // namespace spp
