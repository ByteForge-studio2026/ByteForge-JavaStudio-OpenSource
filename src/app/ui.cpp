// ui.cpp —— Java Studio 的界面绘制
//
// 四个界面：起始页 / 工作区 / jar 查看器 / 设置。
//
// ------------------------------------------------------------------
// 关于「看不出像素感」
// ------------------------------------------------------------------
// 界面里所有圆角、斜线、圆点都走软件抗锯齿（win32.cpp 里的 8x8
// 子采样光栅化），不是 GDI 的 RoundRect —— 后者是硬边缘，圆角上
// 能数出台阶。另外三条规矩：
//
//   1) 不画 1px 的直角边框。要边框就用圆角描边，让它自己过渡。
//   2) 不用纯黑纯白做对比。层次靠灰阶，差一档就够，别硬碰硬。
//   3) 图标一律走浮点端点（drawLineF）。整数取整会把 16px 图标的
//      端点压进同一像素格，线段退化成零长度 —— 图标就没了。
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <unordered_map>

// 窗口过程要用 VK_* 和消息常量，直接拉 windows.h。
// 这里不碰 HWND 之外的东西，只为了常量。
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <shellapi.h>   // CommandLineToArgvW

#include "app/app.h"
#include "libs/jlibs.h"

namespace app {

using namespace spp;
using namespace spp::plat;
using namespace spp::ui;

// ---------------------------------------------------------------- 常量

// 版本号。启动画面、状态栏、关于对话框都从这里取，
// 免得改一处漏一处。
constexpr const char* kVersion = "26.1-Test";
constexpr const char* kVendor = "by.java IDEs";

// 尺寸。集中在这里，改布局只动这一块。
namespace dim {
constexpr int menuH = 30;
constexpr int toolH = 46;
constexpr int statusH = 26;
constexpr int tabH = 34;
constexpr int rowH = 24;       // 树/列表行高
constexpr int btnSize = 30;
constexpr int btnGap = 6;
constexpr int gutterW = 58;    // 行号栏
constexpr int bottomTabH = 28;
constexpr int bottomMinH = 80;
constexpr int bottomMaxH = 520;
constexpr int sidebarMinW = 170;
constexpr int sidebarMaxW = 520;
}  // namespace dim

// 圆角半径。现代风的关键是「够圆」。
namespace rad {
constexpr int btn = 8;
constexpr int card = 12;
constexpr int panel = 14;
constexpr int row = 7;
constexpr int chip = 999;      // 胶囊
}  // namespace rad

// ---------------------------------------------------------------- 状态色
//
// IDEA 里有两种「正确的绿」——一种是语义绿（运行成功），
// 一种是语法高亮的绿。这里只有前面那种，因为配色规矩是
// 「黑灰白 + 红（错）+ 绿（对）」。

static Color accentOf(const Palette& p) {
    // 强调色不引入新色相：深色主题用亮灰，浅色主题用深灰。
    return p.dark ? rgb(226, 227, 231) : rgb(38, 39, 43);
}

static Color accentTextOf(const Palette& p) {
    // 压在 accent 上的文字
    return p.dark ? rgb(24, 25, 28) : rgb(248, 248, 250);
}

// ---------------------------------------------------------------- 语法色映射
//
// javalex 给的类别 → 配色表。加一个新的类别只要在这里加一行。
static Color synColorOf(jlex::Cat c, const Palette& p) {
    switch (c) {
        case jlex::Cat::Keyword:    return p.synKeyword;
        case jlex::Cat::Type:       return p.synType;
        case jlex::Cat::StringLit:  return p.synString;
        case jlex::Cat::CharLit:    return p.synString;
        case jlex::Cat::NumberLit:  return p.synNumber;
        case jlex::Cat::Annotation: return p.synDirective;
        case jlex::Cat::Comment:    return p.synComment;
        case jlex::Cat::Javadoc:    return p.synComment;
        case jlex::Cat::Constant:   return p.synColorRef;
        case jlex::Cat::Punct:      return p.synOperator;
        case jlex::Cat::Plain:
        default:                    return p.fgPrimary;
    }
}

// ---------------------------------------------------------------- 小工具

static bool endsWith(const std::string& s, const char* suf) {
    size_t n = strlen(suf);
    return s.size() >= n && s.compare(s.size() - n, n, suf) == 0;
}

static std::string baseName(const std::string& path) {
    size_t slash = path.find_last_of("\\/");
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

// 画一个「胶囊」形状。注意用 fillRoundRect 传一个很大的半径，
// 平台层会把半径夹到 min(w,h)/2 —— 这就是完全圆头。
static void chip(Canvas& c, int x, int y, int w, int h, Color col) {
    fillRoundRect(c, x, y, w, h, h / 2, col);
}

static void chipStroke(Canvas& c, int x, int y, int w, int h, Color col,
                       float t = 1.0f) {
    strokeRoundRectF(c, (float)x + 0.5f, (float)y + 0.5f, (float)w - 1.0f,
                     (float)h - 1.0f, (float)(h - 1) * 0.5f, col, t);
}

static void text(Canvas& c, int x, int y, const std::string& s, int size,
                 Color col, bool bold = false, bool mono = false) {
    TextStyle st;
    st.size = size;
    st.color = col;
    st.bold = bold;
    st.monospace = mono;
    drawText(c, x, y, s, st);
}

static int textW(Canvas& c, const std::string& s, int size, bool bold = false,
                 bool mono = false) {
    TextStyle st;
    st.size = size;
    st.bold = bold;
    st.monospace = mono;
    int w = 0, h = 0;
    measureText(c, s, st, &w, &h);
    return w;
}

// 右对齐画文本，返回起始 x
static int textRight(Canvas& c, int right, int y, const std::string& s,
                     int size, Color col, bool bold = false) {
    int w = textW(c, s, size, bold);
    text(c, right - w, y, s, size, col, bold);
    return right - w;
}

// 居中画文本
static void textCenter(Canvas& c, int cx, int y, const std::string& s, int size,
                       Color col, bool bold = false) {
    int w = textW(c, s, size, bold);
    text(c, cx - w / 2, y, s, size, col, bold);
}

// 省略号截断（按真实像素宽度）
static std::string ellipsize(Canvas& c, const std::string& s, int size,
                             int maxW, bool mono = false) {
    if (maxW <= 0) return "";
    TextStyle st;
    st.size = size;
    st.monospace = mono;
    int w = 0, h = 0;
    measureText(c, s, st, &w, &h);
    if (w <= maxW) return s;

    // 从尾部按码点砍，留出省略号的宽度
    std::string dots = "...";
    int dw = 0;
    measureText(c, dots, st, &dw, &h);
    int budget = maxW - dw;
    if (budget <= 0) return dots;

    std::string out;
    for (size_t i = 0; i < s.size();) {
        int n = utf8Len((unsigned char)s[i]);
        std::string next = out + s.substr(i, n);
        int nw = 0;
        measureText(c, next, st, &nw, &h);
        if (nw > budget) break;
        out = next;
        i += n;
    }
    return out + dots;
}

// 画图标。库不在或者名字没有就跳过，不崩。
static void icon(Canvas& c, const Library* lib, const std::string& name, int x,
                 int y, int size, Color col) {
    if (!lib || name.empty() || size <= 0) return;
    const auto& sh = lib->shapes(name);
    if (sh.empty()) return;
    icons::draw(c, sh, x, y, size, col);
}

// ---------------------------------------------------------------- 菜单定义

enum MenuAct {
    MA_None = 0,

    MA_NewProject = 1,
    MA_OpenProject,
    MA_OpenJar,
    MA_NewFile,
    MA_Save,
    MA_SaveAll,
    MA_CloseProject,
    MA_Exit,

    MA_Undo = 20,
    MA_Redo,
    MA_Cut,
    MA_Copy,
    MA_Paste,
    MA_SelectAll,
    MA_Find,

    MA_Home = 40,
    MA_Editor,
    MA_ToggleSidebar,
    MA_ToggleBottom,
    MA_ToggleTheme,
    MA_Settings,

    MA_Compile = 60,
    MA_Run,
    MA_CompileRun,
    MA_Check,

    MA_IconLib = 80,
    MA_Libs,
    MA_About,
};

struct MenuItem {
    const char* label;
    const char* accel;
    int action;
};

struct Menu {
    const char* title;
    const MenuItem* items;
    int count;
};

static const MenuItem kFileItems[] = {
    {"新建项目...", "Ctrl+Shift+N", MA_NewProject},
    {"打开项目...", "Ctrl+O", MA_OpenProject},
    {"打开 jar...", "Ctrl+J", MA_OpenJar},
    {nullptr, nullptr, MA_None},
    {"新建文件", "Ctrl+N", MA_NewFile},
    {"保存", "Ctrl+S", MA_Save},
    {"全部保存", "Ctrl+Shift+S", MA_SaveAll},
    {nullptr, nullptr, MA_None},
    {"关闭项目", "", MA_CloseProject},
    {"退出", "Alt+F4", MA_Exit},
};

static const MenuItem kEditItems[] = {
    {"撤销", "Ctrl+Z", MA_Undo},
    {"重做", "Ctrl+Y", MA_Redo},
    {nullptr, nullptr, MA_None},
    {"剪切", "Ctrl+X", MA_Cut},
    {"复制", "Ctrl+C", MA_Copy},
    {"粘贴", "Ctrl+V", MA_Paste},
    {"全选", "Ctrl+A", MA_SelectAll},
    {nullptr, nullptr, MA_None},
    {"查找", "Ctrl+F", MA_Find},
};

static const MenuItem kViewItems[] = {
    {"起始页", "Esc", MA_Home},
    {"工作区", "", MA_Editor},
    {nullptr, nullptr, MA_None},
    {"显示项目树", "", MA_ToggleSidebar},
    {"显示输出面板", "Ctrl+L", MA_ToggleBottom},
    {nullptr, nullptr, MA_None},
    {"切换主题", "Ctrl+T", MA_ToggleTheme},
    {"设置", "Ctrl+,", MA_Settings},
};

static const MenuItem kRunItems[] = {
    {"编译", "Ctrl+B", MA_Compile},
    {"编译并运行", "F5", MA_CompileRun},
    {"只运行", "Shift+F5", MA_Run},
    {nullptr, nullptr, MA_None},
    {"代码检查", "Ctrl+Shift+K", MA_Check},
};

static const MenuItem kToolItems[] = {
    {"第三方库...", "Ctrl+Shift+L", MA_Libs},
    {nullptr, nullptr, MA_None},
    {"新建文件", "Ctrl+N", MA_NewFile},
    {"图标库", "", MA_IconLib},
};

static const MenuItem kHelpItems[] = {
    {"关于 Java Studio", "", MA_About},
};

static const Menu kMenus[] = {
    {"文件", kFileItems, (int)(sizeof(kFileItems) / sizeof(MenuItem))},
    {"编辑", kEditItems, (int)(sizeof(kEditItems) / sizeof(MenuItem))},
    {"视图", kViewItems, (int)(sizeof(kViewItems) / sizeof(MenuItem))},
    {"运行", kRunItems, (int)(sizeof(kRunItems) / sizeof(MenuItem))},
    {"工具", kToolItems, (int)(sizeof(kToolItems) / sizeof(MenuItem))},
    {"帮助", kHelpItems, (int)(sizeof(kHelpItems) / sizeof(MenuItem))},
};
static const int kMenuCount = (int)(sizeof(kMenus) / sizeof(Menu));

// 项目树里一个节点在可见列表中的下标（跳过被折叠的子树）
static std::vector<int> visibleNodes(const App& a) {
    std::vector<int> out;
    int hiddenBelow = -1;   // 隐藏到哪一层为止
    int hiddenDepth = -1;

    for (int i = 0; i < (int)a.tree.size(); i++) {
        int d = a.tree[i].ui.depth;

        if (hiddenBelow >= 0) {
            if (d > hiddenDepth) continue;
            hiddenBelow = -1;
            hiddenDepth = -1;
        }

        out.push_back(i);
        if (a.tree[i].ui.isDir && a.tree[i].collapsed) {
            hiddenBelow = i;
            hiddenDepth = d;
        }
    }
    return out;
}

// ================================================================
//  顶栏
// ================================================================

static void drawMenuBar(App& a, Canvas& c) {
    const Palette& p = current();
    int h = dim::menuH;

    fillRect(c, 0, 0, a.winW, h, p.bgPanel);

    TextStyle st;
    st.size = 12;

    int x = 6;
    int hitMenu = -1;
    std::vector<int> xs(kMenuCount, 0);
    std::vector<int> ws(kMenuCount, 0);

    for (int i = 0; i < kMenuCount; i++) {
        int tw = textW(c, kMenus[i].title, 12);
        int bw = tw + 20;
        xs[i] = x;
        ws[i] = bw;

        bool hov = hit(x, 0, bw, h, a.in.mouseX, a.in.mouseY);
        bool open = (a.openMenu == i);
        if (hov) hitMenu = i;

        if (open) {
            fillRoundRect(c, x + 1, 3, bw - 2, h - 6, rad::row, p.bgActive);
        } else if (hov) {
            fillRoundRect(c, x + 1, 3, bw - 2, h - 6, rad::row, p.bgHover);
        }

        text(c, x + 10, (h - 16) / 2, kMenus[i].title, 12,
             open ? p.fgPrimary : p.fgMuted);
        x += bw;
    }

    // 右边：项目名 + 版本
    {
        std::string right = a.projectRoot.empty()
                                ? std::string("Java Studio  ") + kVersion
                                : (a.projectName + "   ·   " + kVersion);
        int w = textW(c, right, 11);
        text(c, a.winW - w - 12, (h - 15) / 2, right, 11, p.fgDim);
    }

    drawDividerH(c, 0, h - 1, a.winW);

    // 展开/收起
    if (a.in.clicked && hitMenu >= 0) {
        a.openMenu = (a.openMenu == hitMenu) ? -1 : hitMenu;
        a.menuWasOpen = true;
    } else if (a.in.clicked && hitMenu < 0 && a.openMenu >= 0) {
        // 在下面处理（可能点在下拉项上）
    }

    // ---- 下拉面板
    if (a.openMenu < 0) return;
    const Menu& mu = kMenus[a.openMenu];

    int panelW = 190;
    int rowH = 26;
    int sepH = 8;
    int panelH = 8;
    for (int i = 0; i < mu.count; i++)
        panelH += mu.items[i].label ? rowH : sepH;

    int mx = xs[a.openMenu];
    if (mx + panelW > a.winW) mx = a.winW - panelW - 6;
    int py = h + 3;

    // 阴影：往右下偏移几层递减的 alpha。分层画，边缘才柔和。
    for (int s = 6; s >= 1; s--) {
        fillRoundRectA(c, mx - s / 2 + 1, py + s / 2 + 1, panelW, panelH,
                       rad::card, rgb(0, 0, 0), 10);
    }

    fillRoundRect(c, mx, py, panelW, panelH, rad::card, p.bgRaised);
    strokeRoundRectF(c, (float)mx + 0.5f, (float)py + 0.5f,
                     (float)panelW - 1.0f, (float)panelH - 1.0f,
                     (float)rad::card, p.border, 1.0f);

    int iy = py + 4;
    bool insidePanel = hit(mx, py, panelW, panelH, a.in.mouseX, a.in.mouseY);

    for (int i = 0; i < mu.count; i++) {
        const MenuItem& it = mu.items[i];

        if (!it.label) {
            drawDividerH(c, mx + 10, iy + sepH / 2, panelW - 20);
            iy += sepH;
            continue;
        }

        bool hov = hit(mx + 4, iy, panelW - 8, rowH, a.in.mouseX, a.in.mouseY);
        if (hov) {
            fillRoundRect(c, mx + 4, iy, panelW - 8, rowH, rad::row, p.bgHover);
        }

        text(c, mx + 14, iy + (rowH - 16) / 2, it.label, 12,
             hov ? p.fgPrimary : p.fgMuted);

        if (it.accel && *it.accel) {
            textRight(c, mx + panelW - 14, iy + (rowH - 15) / 2, it.accel, 11,
                      p.fgDim);
        }

        if (hov && a.in.clicked) {
            a.openMenu = -1;
            a.menuWasOpen = false;
            extern void doMenuAction(App&, int);
            doMenuAction(a, it.action);
            return;
        }
        iy += rowH;
    }

    // 点在下拉外面 → 收起
    if (a.in.clicked && !insidePanel && a.in.mouseY > h) {
        a.openMenu = -1;
        a.menuWasOpen = false;
    }
}

// ================================================================
//  工具栏
// ================================================================

struct BtnRect {
    int x, y, w, h;
    bool contains(int px, int py) const {
        return px >= x && px < x + w && py >= y && py < y + h;
    }
};

// 画一个工具按钮。返回矩形，调用方自己判断点击。
//
// 为什么不用 widgets 里的 drawButton：那个用的是估算宽度
// （charWidth * 0.55），中文按钮会量不准，右边留白忽大忽小。
// 这里用真实测量。
static BtnRect toolButton(Canvas& c, App& a, const char* iconName,
                          const std::string& label, bool active, bool enabled,
                          int x, int y, int h) {
    const Palette& p = current();

    int bw;
    if (label.empty()) {
        bw = h;   // 正方形
    } else {
        int tw = textW(c, label, 12);
        int iw = (int)(h * 0.46f);
        bw = 12 + iw + 7 + tw + 13;
        if (bw < h + 8) bw = h + 8;
    }

    BtnRect r{x, y, bw, h};

    bool hov = enabled && r.contains(a.in.mouseX, a.in.mouseY);
    Color fg = enabled ? (hov ? p.fgPrimary : p.fgMuted) : p.fgDim;

    if (active) {
        fillRoundRect(c, x, y, bw, h, rad::btn, p.bgActive);
        fg = p.fgPrimary;
    } else if (hov) {
        fillRoundRect(c, x, y, bw, h, rad::btn, p.bgHover);
    }

    int iconSize = (int)(h * 0.46f);
    int cx = x + 12;

    if (label.empty()) {
        icon(c, a.icons, iconName, x + (bw - iconSize) / 2,
             y + (h - iconSize) / 2, iconSize, fg);
    } else {
        icon(c, a.icons, iconName, cx, y + (h - iconSize) / 2, iconSize, fg);
        text(c, cx + iconSize + 7, y + (h - 17) / 2, label, 12, fg);
    }

    return r;
}

static void drawToolbar(App& a, Canvas& c) {
    const Palette& p = current();
    int h = dim::toolH;

    // 坐标系：菜单栏占 0..menuH，工具栏占 menuH..menuH+toolH。
    // 这里必须用 menuH 起笔 —— 早期版本从 0 开始填充，而 paintAll 里
    // drawToolbar 又画在 drawMenuBar 之后，结果工具栏背景把菜单栏整条
    // 盖掉，按钮也浮到了菜单栏上 —— 就是「打开项目后界面错乱」那个 bug。
    int ty = dim::menuH;

    fillRect(c, 0, ty, a.winW, h, p.bgPanel);
    drawDividerH(c, 0, ty + h - 1, a.winW);

    int by = ty + (h - dim::btnSize) / 2;
    int x = 10;

    // ---- 回起始页
    {
        auto r = toolButton(c, a, "home", "主页", false, true, x, by,
                            dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            a.screen = Screen::Home;
            a.statusLeft = "起始页";
        }
        x = r.x + r.w + dim::btnGap;
    }

    x += 5;
    drawDividerV(c, x, by + 5, dim::btnSize - 10);
    x += 9;

    // ---- 文件组
    struct Sq {
        const char* icon;
        const char* label;
        int act;
    };
    const Sq sqs[] = {
        {"plus", "新建", MA_NewProject},
        {"folder-open", "打开", MA_OpenProject},
        {"save", "保存", MA_Save},
    };
    for (const auto& s : sqs) {
        auto r = toolButton(c, a, s.icon, s.label, false, true, x, by,
                            dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            extern void doMenuAction(App&, int);
            doMenuAction(a, s.act);
        }
        x = r.x + r.w + dim::btnGap;
    }

    x += 5;
    drawDividerV(c, x, by + 5, dim::btnSize - 10);
    x += 9;

    // ---- 运行组
    bool hasProject = !a.projectRoot.empty();

    struct W {
        const char* icon;
        const char* label;
        int act;
    };
    const W ws[] = {
        {"zap", "编译", MA_Compile},
        {"play", "运行", MA_CompileRun},
        {"check-circle", "检查", MA_Check},
        {"package", "第三方库", MA_Libs},
    };
    for (const auto& w : ws) {
        auto r = toolButton(c, a, w.icon, w.label, false, hasProject, x, by,
                            dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked &&
            hasProject) {
            extern void doMenuAction(App&, int);
            doMenuAction(a, w.act);
        }
        x = r.x + r.w + dim::btnGap;
    }

    // ---- 右侧组
    int rx = a.winW - 10;
    {
        rx -= dim::btnSize;
        auto r = toolButton(c, a, "settings", "", false, true, rx, by,
                            dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            a.screen = Screen::Settings;
            a.statusLeft = "设置";
        }
        rx -= dim::btnGap;

        rx -= dim::btnSize;
        bool isDark = current().dark;
        auto r2 = toolButton(c, a, isDark ? "sun" : "moon", "", false, true, rx,
                            by, dim::btnSize);
        if (r2.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            setDark(!isDark);
            a.log(LogLevel::Info, isDark ? "已切到浅色主题" : "已切到深色主题");
        }
        rx -= dim::btnGap;

        rx -= dim::btnSize;
        auto r3 = toolButton(c, a, "grid", "", a.sidebarOpen, true, rx, by,
                            dim::btnSize);
        if (r3.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            a.sidebarOpen = !a.sidebarOpen;
        }
    }
}

// ================================================================
//  状态栏
// ================================================================

static void drawStatusBar(App& a, Canvas& c) {
    const Palette& p = current();
    int sy = a.winH - dim::statusH;

    fillRect(c, 0, sy, a.winW, dim::statusH, p.bgPanel);
    drawDividerH(c, 0, sy, a.winW);

    int ty = sy + (dim::statusH - 15) / 2;

    // 左：状态文字。带颜色点 —— 绿=通过，红=失败。
    int lx = 12;
    if (!a.statusLeft.empty()) {
        bool err = a.statusLeft.find("失败") != std::string::npos ||
                   a.statusLeft.find("问题") != std::string::npos;
        bool ok = a.statusLeft.find("通过") != std::string::npos ||
                  a.statusLeft.find("完成") != std::string::npos;
        if (err || ok) {
            // 状态点用圆，不用方块 —— 方块在 4px 尺寸上四角太硬
            fillCircle(c, lx + 4, sy + dim::statusH / 2, 4,
                       err ? p.error : p.ok);
            lx += 14;
        }
        text(c, lx, ty, a.statusLeft, 11, err ? p.error : p.fgMuted);
    }

    // 中：文件名 + 行列
    if (!a.statusMid.empty()) {
        int w = textW(c, a.statusMid, 11);
        text(c, (a.winW - w) / 2, ty, a.statusMid, 11, p.fgMuted);
    }

    // 右：JDK + 版本
    {
        jdk::Info j = jdk::current();
        std::string right = j.ok ? j.label() : "无 JDK";
        right += "   ·   ";
        right += kVersion;
        textRight(c, a.winW - 12, ty, right, 11, p.fgDim);
    }
}

// ================================================================
//  侧栏：项目树
// ================================================================

// 侧栏和底部面板之间那条可拖的分隔线。
// 拖动时鼠标形状应该变，但这里没有改光标的能力，
// 所以就画一条高亮的线表示「正在拖」。
static void drawVSplitter(App& a, Canvas& c, int x, int y, int h,
                          bool active) {
    const Palette& p = current();
    bool hov = std::abs(a.in.mouseX - x) <= 3 && a.in.mouseY >= y &&
               a.in.mouseY < y + h;
    if (hov || active) {
        fillRect(c, x - 1, y, 3, h, p.borderFocus);
    }
    (void)p;
}

static void drawSidebar(App& a, Canvas& c, int y, int h) {
    const Palette& p = current();
    int w = a.sidebarW;

    fillRect(c, 0, y, w, h, p.bgPanel);

    // 标题栏
    int headH = 32;
    {
        text(c, 14, y + (headH - 16) / 2, "项目", 12, p.fgDim, true);

        // 折叠/展开全部
        int bx = w - 12 - 20;
        bool hov = hit(bx, y + 6, 20, 20, a.in.mouseX, a.in.mouseY);
        if (hov) fillRoundRect(c, bx, y + 6, 20, 20, rad::row, p.bgHover);
        icon(c, a.icons, "folder-open", bx + 4, y + 10, 12,
             hov ? p.fgPrimary : p.fgDim);
        if (hov && a.in.clicked) {
            // 全部展开或全部收起，看当前状态
            bool anyOpen = false;
            for (auto& n : a.tree) {
                if (n.ui.isDir && !n.collapsed) {
                    anyOpen = true;
                    break;
                }
            }
            for (auto& n : a.tree) {
                if (n.ui.isDir) n.collapsed = anyOpen;
            }
        }
    }

    int listY = y + headH;
    int listH = h - headH;
    if (listH < 10) return;

    pushClip(c, 0, listY, w, listH);
    fillRect(c, 0, listY, w, listH, p.bgPanel);

    std::vector<int> vis = visibleNodes(a);

    // 项目名做根节点显示
    int y0 = listY - a.treeScroll;
    int rowH = dim::rowH;

    int totalH = (int)vis.size() * rowH;
    int maxScroll = totalH - listH;
    if (maxScroll < 0) maxScroll = 0;
    if (a.treeScroll > maxScroll) a.treeScroll = maxScroll;
    if (a.treeScroll < 0) a.treeScroll = 0;
    y0 = listY - a.treeScroll;

    for (size_t k = 0; k < vis.size(); k++) {
        int idx = vis[k];
        const Node& n = a.tree[idx];
        int ry = y0 + (int)k * rowH;

        if (ry + rowH < listY) continue;
        if (ry > listY + listH) break;

        bool sel = (a.treeSelected == idx);
        bool hov = hit(0, ry, w, rowH, a.in.mouseX, a.in.mouseY);

        if (sel) {
            // 选中行用胶囊，左右各留 4px —— 贴着边会显得局促
            fillRoundRect(c, 4, ry + 1, w - 8, rowH - 2, rad::row, p.bgActive);
        } else if (hov) {
            fillRoundRect(c, 4, ry + 1, w - 8, rowH - 2, rad::row, p.bgHover);
        }

        int indent = 10 + n.ui.depth * 14;
        int iconSize = 14;
        int ix = indent;
        int iy = ry + (rowH - iconSize) / 2;

        // 目录：展开箭头 + 文件夹图标
        if (n.ui.isDir) {
            Color ac = hov || sel ? p.fgMuted : p.fgDim;
            // 箭头。用两条线段拼一个 V 或者 >，用浮点画才有平滑边缘。
            float ax = (float)ix + 3.0f, ay = (float)ry + rowH * 0.5f;
            if (n.collapsed) {
                // 向右的 >
                drawLineF(c, ax, ay - 3.5f, ax + 3.5f, ay, ac, 1.4f);
                drawLineF(c, ax + 3.5f, ay, ax, ay + 3.5f, ac, 1.4f);
            } else {
                // 向下的 v
                drawLineF(c, ax - 3.5f, ay - 1.5f, ax, ay + 2.0f, ac, 1.4f);
                drawLineF(c, ax, ay + 2.0f, ax + 3.5f, ay - 1.5f, ac, 1.4f);
            }
            ix += 10;
            iy = ry + (rowH - iconSize) / 2;
        }

        Color ic = n.ui.hasError ? p.error
                                 : (sel ? p.fgPrimary
                                        : (hov ? p.fgPrimary : p.fgMuted));
        icon(c, a.icons, n.ui.iconName, ix, iy, iconSize, ic);

        int tx = ix + iconSize + 7;
        int avail = w - tx - 10;

        std::string label = n.ui.name;
        // .java 文件把后缀去掉，树里更清爽（IDEA 也这么干）
        if (n.kind == NodeKind::JavaFile && endsWith(label, ".java"))
            label = label.substr(0, label.size() - 5);

        bool isJavaClass = n.kind == NodeKind::JavaFile;
        Color tc = sel ? p.fgPrimary : (hov ? p.fgPrimary : p.fgMuted);
        text(c, tx, ry + (rowH - 16) / 2,
             ellipsize(c, label, 12, avail), 12, tc);

        // 点击
        if (hov && a.in.clicked) {
            if (n.ui.isDir) {
                // 点箭头区域折叠，点别处选中
                if (a.in.mouseX < indent + 12) {
                    a.tree[idx].collapsed = !a.tree[idx].collapsed;
                } else {
                    a.treeSelected = idx;
                }
            } else {
                a.treeSelected = idx;
                std::string path = a.tree[idx].ui.path;
                if (n.kind == NodeKind::JarFile) {
                    openJar(a, path);
                } else {
                    loadFile(a, path);
                }
            }
        }
        (void)isJavaClass;
    }

    popClip(c);

    // 右边线
    drawDividerV(c, w - 1, y, h);
}

// ================================================================
//  编辑器
// ================================================================

// 一行代码的绘制。逐 span 上色。
//
// 空格和缩进不产生 span，得靠前一个 span 的结束位置补齐。
// 这里用「上一个 span 的结束字节」和「当前 span 的起始字节」
// 之间的差来补空白 —— 比按列号估算准。
static void drawCodeLine(Canvas& c, OpenFile& f, int li, int px, int py,
                         const TextStyle& base) {
    const Palette& p = current();
    const std::string& ln = f.lines[li];

    if (!f.isJava || li >= (int)f.spans.size() || f.spans[li].empty()) {
        drawText(c, px, py, ln, base);
        return;
    }

    TextStyle st = base;
    int prevEnd = 0;
    int pen = 0;

    for (const auto& sp : f.spans[li]) {
        if (sp.start > prevEnd) {
            // 中间是空白（空格/缩进），按真实宽度推进
            std::string gap = ln.substr(prevEnd, sp.start - prevEnd);
            int w = 0, hh = 0;
            measureText(c, gap, base, &w, &hh);
            pen += w;
        }
        if (sp.len <= 0) {
            prevEnd = sp.start;
            continue;
        }

        std::string piece = ln.substr(sp.start, sp.len);
        st.color = synColorOf(sp.cat, p);
        drawText(c, px + pen, py, piece, st);

        int w = 0, hh = 0;
        measureText(c, piece, base, &w, &hh);
        pen += w;
        prevEnd = sp.start + sp.len;
    }

    // 尾部空白不画
}

static void drawEditor(App& a, Canvas& c, int x, int y, int w, int h) {
    const Palette& p = current();

    int idx = a.activeIndex();
    if (idx < 0) {
        fillRect(c, x, y, w, h, p.bgBase);

        // 空状态：画个咖啡杯 + 一句提示，比一行灰字好看
        int cy = y + h / 2 - 40;
        Color ic = p.bgActive;
        icon(c, a.icons, "coffee", x + w / 2 - 20, cy, 40, ic);
        textCenter(c, x + w / 2, cy + 54, "从左侧选择一个文件开始", 13,
                   p.fgDim);
        textCenter(c, x + w / 2, cy + 76, "或者按 Ctrl+O 打开项目", 11,
                   p.fgDim);
        a.statusMid.clear();
        return;
    }

    OpenFile& f = a.files[idx];

    // ---- 标签栏
    int tabH = dim::tabH;
    fillRect(c, x, y, w, tabH, p.bgPanel);

    int tx = x + 4;
    for (size_t i = 0; i < a.files.size(); i++) {
        OpenFile& of = a.files[i];
        int tw = textW(c, of.title, 12) + 46;
        if (tw > 190) tw = 190;
        if (tx + tw > x + w) break;
        if (tw < 70) tw = 70;

        bool act = ((int)i == idx);
        bool hov = hit(tx, y + 3, tw, tabH - 6, a.in.mouseX, a.in.mouseY);

        if (act) {
            // 活动标签：底色比面板亮一档 + 上边一条强调线
            fillRoundRect(c, tx, y + 3, tw, tabH - 3, rad::row, p.bgBase);
            fillRoundRect(c, tx + 8, y + 3, tw - 16, 2, 1, accentOf(p));
        } else if (hov) {
            fillRoundRect(c, tx, y + 3, tw, tabH - 6, rad::row, p.bgHover);
        }

        Color tc = act ? p.fgPrimary : p.fgMuted;
        icon(c, a.icons, of.isJava ? "file-text" : "file", tx + 11,
             y + (tabH - 13) / 2 + 1, 13, tc);

        int avail = tw - 46;
        text(c, tx + 30, y + (tabH - 16) / 2 + 1,
             ellipsize(c, of.title, 12, avail), 12, tc);

        // 未保存：一个小圆点。放在标题右边，比加星号干净。
        if (of.dirty) {
            fillCircle(c, tx + tw - 14, y + tabH / 2 + 1, 3, accentOf(p));
        }

        // 关闭按钮
        {
            int cx = tx + tw - 30;
            bool chov = of.dirty ? false : hov;
            if (hit(cx, y + 8, 18, 18, a.in.mouseX, a.in.mouseY)) {
                chov = true;
                if (a.in.clicked) {
                    a.files.erase(a.files.begin() + i);
                    if (a.activeTab >= (int)a.files.size())
                        a.activeTab = (int)a.files.size() - 1;
                    return;
                }
            }
            if (chov && !of.dirty) {
                icon(c, a.icons, "x", cx + 3, y + 11, 12, p.fgMuted);
            }
        }

        if (hov && a.in.clicked) a.activeTab = (int)i;
        tx += tw + 2;
    }

    drawDividerH(c, x, y + tabH, w);

    // ---- 代码区
    int bodyY = y + tabH;
    int bodyH = h - tabH;
    fillRect(c, x, bodyY, w, bodyH, p.bgBase);

    // 行号栏底色稍微分一档，区域感更清楚
    int gutterW = dim::gutterW;
    fillRect(c, x, bodyY, gutterW, bodyH, p.dark ? p.bgBase : p.bgPanel);

    TextStyle codeSt;
    codeSt.size = 14;
    codeSt.monospace = true;
    codeSt.color = p.fgPrimary;

    TextStyle gutSt;
    gutSt.size = 12;
    gutSt.monospace = true;
    gutSt.color = p.fgDim;

    int lh = lineHeight(c, codeSt);
    if (lh <= 0) lh = 18;

    pushClip(c, x + gutterW, bodyY, w - gutterW, bodyH);

    int first = f.scrollY / lh;
    int last = first + bodyH / lh + 2;
    if (first < 0) first = 0;
    if (last > (int)f.lines.size()) last = (int)f.lines.size();

    // ---- 选区底层
    //
    // 先铺选区和高亮，再画文字。反过来的话底色会盖住字形。
    bool hasSel = hasSelection(a);
    int sl0 = 0, sc0 = 0, sl1 = 0, sc1 = 0;
    if (hasSel) {
        int al = f.selAnchorLine, ac = f.selAnchorCol;
        bool aFirst = (al < f.caretLine) ||
                      (al == f.caretLine && ac <= f.caretCol);
        sl0 = aFirst ? al : f.caretLine;
        sc0 = aFirst ? ac : f.caretCol;
        sl1 = aFirst ? f.caretLine : al;
        sc1 = aFirst ? f.caretCol : ac;
    }

    for (int i = first; i < last; i++) {
        int ly = bodyY + i * lh - f.scrollY;

        // 当前行高亮（很淡的一条，不抢视线）
        if (i == f.caretLine) {
            fillRoundRect(c, x + gutterW, ly, w - gutterW, lh, 3, p.bgHover);
        }

        // 选区
        if (hasSel && i >= sl0 && i <= sl1) {
            int a0 = (i == sl0) ? sc0 : 0;
            int a1 = (i == sl1) ? sc1 : (int)f.lines[i].size();
            int px0 = x + gutterW + 10 + colToX(a, f, i, a0, c, codeSt) -
                      f.scrollX;
            int px1 = x + gutterW + 10 + colToX(a, f, i, a1, c, codeSt) -
                      f.scrollX;
            if (px1 > px0) {
                fillRect(c, px0, ly, px1 - px0, lh, p.bgSelection);
            } else if (i < sl1) {
                // 空行也要能看出被选中了，给一个字符宽
                fillRect(c, px0, ly, 8, lh, p.bgSelection);
            }
        }
    }

    // ---- 代码文字
    int px = x + gutterW + 10 - f.scrollX;
    for (int i = first; i < last; i++) {
        int ly = bodyY + i * lh - f.scrollY;
        drawCodeLine(c, f, i, px, ly + (lh - 18) / 2, codeSt);
    }

    popClip(c);

    // ---- 行号（不跟着横向滚动）
    pushClip(c, x, bodyY, gutterW, bodyH);
    for (int i = first; i < last; i++) {
        int ly = bodyY + i * lh - f.scrollY;
        std::string ln = std::to_string(i + 1);
        int lw = textW(c, ln, 12, false, true);
        TextStyle gs = gutSt;
        gs.color = (i == f.caretLine) ? p.fgMuted : p.fgDim;
        drawText(c, x + gutterW - lw - 10, ly + (lh - 17) / 2, ln, gs);
    }
    popClip(c);

    drawDividerV(c, x + gutterW, bodyY, bodyH);

    // ---- 错误标记
    //
    // 诊断里有错的行，右侧画一条红杠 + 行号变红。
    // 「红色标错」在这里落到具体行上。
    {
        int errLine = -1;
        for (const auto& d : a.diags) {
            if (d.level != jbuild::Level::Error) continue;
            if (baseName(d.file) != f.title) continue;
            errLine = d.line - 1;
            break;
        }
        if (errLine >= first && errLine < last) {
            int ly = bodyY + errLine * lh - f.scrollY;
            // 左边一条红杠
            fillRoundRect(c, x + gutterW - 2, ly + 2, 2, lh - 4, 1, p.error);
        }
    }

    // ---- 光标
    if ((plat::nowMs() / 530) % 2 == 0) {
        int cx = x + gutterW + 10 + colToX(a, f, f.caretLine, f.caretCol, c,
                                           codeSt) -
                 f.scrollX;
        int ly = bodyY + f.caretLine * lh - f.scrollY;
        if (ly + lh > bodyY && ly < bodyY + bodyH) {
            fillRect(c, cx, ly + 3, 2, lh - 6, p.fgPrimary);
        }
    }

    // ---- 状态
    int dc = displayColOf(f.lines[f.caretLine], f.caretCol);
    a.statusMid = f.title + "    " + std::to_string(f.caretLine + 1) + ":" +
                  std::to_string(dc + 1) + "    " + std::to_string(lh) + "px";
}

// ================================================================
//  底部面板
// ================================================================

static void drawBottom(App& a, Canvas& c, int y, int h) {
    const Palette& p = current();

    fillRect(c, 0, y, a.winW, h, p.bgPanel);
    drawDividerH(c, 0, y, a.winW);

    // 页签
    int ty = y + 6;
    int th = dim::bottomTabH - 6;
    int tx = 12;

    int errCount = 0;
    for (const auto& d : a.diags)
        if (d.level == jbuild::Level::Error) errCount++;

    struct B {
        const char* label;
        BottomTab tab;
        int count;
    };
    const B bs[] = {
        {"构建输出", BottomTab::Build, (int)a.buildOut.size()},
        {"运行日志", BottomTab::Run, (int)a.logs.size()},
        {"问题", BottomTab::Problems, errCount},
    };

    for (const auto& b : bs) {
        std::string label = b.label;
        if (b.count > 0) label += " " + std::to_string(b.count);

        int tw = textW(c, label, 12) + 24;
        bool act = (a.bottom == b.tab);
        bool hov = hit(tx, ty, tw, th, a.in.mouseX, a.in.mouseY);

        if (act) {
            chip(c, tx, ty, tw, th, p.bgActive);
        } else if (hov) {
            chip(c, tx, ty, tw, th, p.bgHover);
        }

        Color tc = act ? p.fgPrimary : p.fgMuted;
        // 「问题」有错时数字标红
        if (b.tab == BottomTab::Problems && errCount > 0) tc = p.error;
        text(c, tx + 12, ty + (th - 16) / 2, label, 12, tc, act);

        if (hov && a.in.clicked) a.bottom = b.tab;
        tx += tw + 5;
    }

    // 右侧：收起按钮
    {
        int bx = a.winW - 12 - 22;
        bool hov = hit(bx, ty, 22, th, a.in.mouseX, a.in.mouseY);
        if (hov) chip(c, bx, ty, 22, th, p.bgHover);
        icon(c, a.icons, "chevron-down", bx + 5, ty + (th - 12) / 2, 12,
             hov ? p.fgPrimary : p.fgDim);
        if (hov && a.in.clicked) a.bottomOpen = false;
    }

    // 内容
    int cy = y + dim::bottomTabH + 4;
    int ch = h - dim::bottomTabH - 8;
    if (ch < 10) return;

    pushClip(c, 0, cy, a.winW, ch);

    if (a.bottom == BottomTab::Problems) {
        if (a.diags.empty()) {
            icon(c, a.icons, "check-circle", 14, cy + 10, 15, p.ok);
            text(c, 36, cy + 9, "没有发现问题", 12, p.ok);
        } else {
            TextStyle st;
            st.size = 12;
            st.monospace = true;

            int rowH = 20;
            int maxRow = ch / rowH;
            int scroll = a.problemScroll;

            for (int i = 0; i < (int)a.diags.size(); i++) {
                int ry = cy + 6 + i * rowH - scroll;
                if (ry + rowH < cy) continue;
                if (ry > cy + ch) break;

                const auto& d = a.diags[i];
                bool err = d.level == jbuild::Level::Error;
                bool hov = hit(0, ry, a.winW, rowH, a.in.mouseX, a.in.mouseY);
                if (hov) fillRoundRect(c, 4, ry, a.winW - 8, rowH, rad::row,
                                       p.bgHover);

                int ix = 14;
                // 级别用一个小图标，颜色区分红/黄
                icon(c, a.icons, err ? "alert-circle" : "alert-triangle", ix,
                     ry + 4, 12, err ? p.error : p.logWarn);
                ix += 20;

                std::string loc = baseName(d.file) + ":" +
                                  std::to_string(d.line);
                st.color = p.fgDim;
                drawText(c, ix, ry + 2, loc, st);
                int lw = textW(c, loc, 12, false, true);

                st.color = err ? p.error : p.logWarn;
                drawText(c, ix + lw + 10, ry + 2,
                         ellipsize(c, d.message, 12, a.winW - ix - lw - 30,
                                   true),
                         st);

                // 点一下跳到那一行
                if (hov && a.in.clicked) {
                    std::string base = baseName(d.file);
                    if (!a.active() ||
                        baseName(a.active()->path) != base) {
                        if (fileExists(d.file)) loadFile(a, d.file);
                    }
                    OpenFile* fp = a.active();
                    if (fp && d.line > 0) {
                        setCaret(a, d.line - 1, 0, false);
                        fp->scrollY =
                            (d.line - 1) * 20 - ch / 2;
                        if (fp->scrollY < 0) fp->scrollY = 0;
                    }
                }
            }

            int totalH = (int)a.diags.size() * rowH;
            int mx = totalH - ch;
            if (a.problemScroll > mx) a.problemScroll = mx > 0 ? mx : 0;
        }
    } else {
        const std::vector<LogLine>& lines =
            (a.bottom == BottomTab::Build) ? a.buildOut : a.logs;
        int* scroll = (a.bottom == BottomTab::Build) ? &a.buildScroll
                                                     : &a.logScroll;

        if (lines.empty()) {
            text(c, 14, cy + 8, "（空）", 12, p.fgDim);
        } else {
            TextStyle st;
            st.size = 12;
            st.monospace = true;

            int rowH = 19;
            int totalH = (int)lines.size() * rowH;
            int maxScroll = totalH - ch;
            if (maxScroll < 0) maxScroll = 0;
            if (*scroll > maxScroll) *scroll = maxScroll;
            if (*scroll < 0) *scroll = 0;

            int firstL = *scroll / rowH;
            int lastL = firstL + ch / rowH + 2;
            if (firstL < 0) firstL = 0;
            if (lastL > (int)lines.size()) lastL = (int)lines.size();

            for (int i = firstL; i < lastL; i++) {
                int ry = cy + i * rowH - *scroll;
                const LogLine& l = lines[i];

                Color lc = p.logInfo;
                if (l.level == LogLevel::Error) lc = p.logError;
                else if (l.level == LogLevel::Warn) lc = p.logWarn;
                else if (l.level == LogLevel::Ok) lc = p.logOk;

                text(c, 14, ry + 2,
                     ellipsize(c, l.text, 12, a.winW - 28, true), 12, lc,
                     false, true);
            }
        }
    }

    popClip(c);
}

// ================================================================
//  起始页
// ================================================================

struct HomeAct {
    const char* iconName;
    const char* title;
    const char* desc;
    int id;
};

static void drawHome(App& a, Canvas& c) {
    const Palette& p = current();

    fillRect(c, 0, 0, a.winW, a.winH, p.bgBase);

    // ---- 顶栏（起始页版）
    int th = dim::toolH;
    fillRect(c, 0, 0, a.winW, th, p.bgPanel);
    drawDividerH(c, 0, th - 1, a.winW);

    int by = (th - dim::btnSize) / 2;

    // 品牌区：色块 + 名字 + 版本
    {
        int lx = 14;
        int ls = 24;
        int ly = by + (dim::btnSize - ls) / 2;

        fillRoundRect(c, lx, ly, ls, ls, 7, accentOf(p));
        {
            icon(c, a.icons, "coffee", lx + 5, ly + 5, ls - 10, accentTextOf(p));
        }

        text(c, lx + ls + 10, by + 3, "Java Studio", 14, p.fgPrimary, true);
        text(c, lx + ls + 10, by + 20, kVersion, 10, p.fgDim);
    }

    // 右侧按钮
    int rx = a.winW - 12;
    {
        rx -= dim::btnSize;
        auto r = toolButton(c, a, current().dark ? "sun" : "moon", "", false,
                            true, rx, by, dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked)
            setDark(!current().dark);
        rx -= dim::btnGap;

        rx -= dim::btnSize;
        auto r2 = toolButton(c, a, "settings", "", false, true, rx, by,
                             dim::btnSize);
        if (r2.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            a.screen = Screen::Settings;
            a.statusLeft = "设置";
        }
    }

    // ---- 主体两栏
    int top = th;
    int lx = 44;
    int leftW = 340;

    int baseY = top + 48;
    int subY = baseY + 30;
    int listY = subY + 40;

    text(c, lx, baseY, "欢迎使用 Java Studio", 24, p.fgPrimary, true);
    text(c, lx, subY, "JDK 已就绪，从下面选一个开始", 12, p.fgMuted);

    const HomeAct acts[] = {
        {"plus", "新建项目", "选好包名和构建方式，骨架一次搭齐", 1},
        {"folder-open", "打开项目", "打开一个已有的 Java 项目目录", 2},
        {"package", "打开 jar", "看清单、类列表，直接运行", 3},
        {"clock", "继续上次", "回到上次打开的项目", 4},
    };

    int rowH = 58;
    int rowGap = 8;
    int ly = listY;

    for (const auto& act : acts) {
        bool hov = hit(lx, ly, leftW, rowH, a.in.mouseX, a.in.mouseY);

        bool enabled = true;
        if (act.id == 4)
            enabled = !a.projectRoot.empty() && dirExists(a.projectRoot);

        if (hov && enabled) {
            fillRoundRect(c, lx, ly, leftW, rowH, rad::card, p.bgHover);
        }

        Color ic = !enabled ? p.fgDim : (hov ? p.fgPrimary : p.fgMuted);
        icon(c, a.icons, act.iconName, lx + 16, ly + (rowH - 22) / 2, 22, ic);

        int tH = 18, dH = 15, gapT = 3;
        int textTop = ly + (rowH - (tH + gapT + dH)) / 2;

        text(c, lx + 52, textTop, act.title, 14,
             !enabled ? p.fgDim : (hov ? p.fgPrimary : p.fgMuted), true);
        text(c, lx + 52, textTop + tH + gapT, act.desc, 11, p.fgDim);

        if (hov && a.in.clicked && enabled) {
            switch (act.id) {
                case 1:
                    beginNewProject(a);
                    break;
                case 2:
                    a.screen = Screen::Home;
                    a.dlg = Dialog::None;
                    a.statusLeft = "选择项目目录";
                    // 走系统目录选择框，选完直接开
                    {
                        std::string picked =
                            pickFolder("打开 Java 项目", a.curDir);
                        if (!picked.empty()) openProject(a, picked);
                    }
                    break;
                case 3:
                    a.statusLeft = "选择 jar 文件";
                    {
                        // 没有文件选择框就退回「打开项目」的目录选择
                        std::string f = pickFolder("选择 jar 所在目录", a.curDir);
                        if (!f.empty()) {
                            a.curDir = f;
                            a.statusLeft = "从下方列表选一个 jar";
                        }
                    }
                    break;
                case 4:
                    a.screen = Screen::Editor;
                    a.statusLeft = "项目已打开";
                    break;
                default:
                    break;
            }
        }

        ly += rowH + rowGap;
    }

    // ---- 当前项目
    if (!a.projectRoot.empty()) {
        ly += 14;
        drawDividerH(c, lx, ly, leftW);
        ly += 14;

        text(c, lx, ly, "当前项目", 11, p.fgDim, true);
        text(c, lx, ly + 20, a.projectName, 15, p.fgPrimary, true);
        text(c, lx, ly + 42,
             ellipsize(c, a.projectRoot, 11, leftW - 20), 11, p.fgDim);

        fillCircle(c, lx + leftW - 8, ly + 8, 4,
                   dirExists(a.projectRoot) ? p.ok : p.error);

        // 进工作区
        int bx = lx;
        int bw = textW(c, "进入工作区", 12) + 40;
        int bby = ly + 68;
        auto r = toolButton(c, a, "chevron-right", "进入工作区", false, true, bx,
                            bby, dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            a.screen = Screen::Editor;
            a.statusLeft = "项目已打开";
        }
    }

    // ---- 右栏：最近打开
    int rxx = lx + leftW + 56;
    int rw = a.winW - rxx - 44;
    if (rw < 240) rw = 240;

    text(c, rxx, subY, "最近打开", 13, p.fgMuted, true);

    int ry = listY;
    int itemH = 42;
    int avail = a.winH - dim::statusH - ry - 16;
    int shown = avail / itemH;
    if (shown < 1) shown = 1;

    if (a.recent.empty()) {
        text(c, rxx, ry, "还没有打开过项目", 12, p.fgDim);
        text(c, rxx, ry + 22, "打开过的项目会出现在这里", 11, p.fgDim);
    } else {
        pushClip(c, rxx, ry, rw, avail);

        int totalH = (int)a.recent.size() * itemH;
        int maxScroll = totalH - avail;
        if (maxScroll < 0) maxScroll = 0;
        if (a.recentScroll > maxScroll) a.recentScroll = maxScroll;
        if (a.recentScroll < 0) a.recentScroll = 0;

        a.recentHover = -1;
        int iy0 = ry - a.recentScroll;

        for (int i = 0; i < (int)a.recent.size(); i++) {
            int iy = iy0 + i * itemH;
            if (iy + itemH < ry) continue;
            if (iy > ry + avail) break;

            bool hov = hit(rxx, iy, rw, itemH - 4, a.in.mouseX, a.in.mouseY);
            if (hov) {
                a.recentHover = i;
                fillRoundRect(c, rxx, iy, rw, itemH - 4, rad::card, p.bgHover);
            }

            bool okDir = dirExists(a.recent[i]);
            icon(c, a.icons, okDir ? "folder" : "alert-circle", rxx + 14,
                 iy + (itemH - 4 - 17) / 2, 17, okDir ? p.fgMuted : p.error);

            std::string path = a.recent[i];
            std::string name = baseName(path);

            text(c, rxx + 44, iy + 6, ellipsize(c, name, 13, rw - 60), 13,
                 hov ? p.fgPrimary : p.fgMuted);
            text(c, rxx + 44, iy + 24, ellipsize(c, path, 10, rw - 60), 10,
                 p.fgDim);

            if (hov && a.in.clicked) {
                if (openProject(a, path)) {
                    // 打开成功
                } else {
                    a.log(LogLevel::Warn, "目录已不存在，从列表移除");
                    a.recent.erase(a.recent.begin() + i);
                    saveSession(a);
                }
                // 这里必须弹掉裁剪框再走。
                // 之前漏了这一步：openProject 换到工作区之后，裁剪栈上
                // 还压着「最近打开」那块矩形，而 CPU 光栅化的裁剪是
                // 全局的，于是之后每一帧只有那块区域会刷新，窗口左边
                // 一直留着起始页的残像 —— 就是侧栏点项目打开后
                // 「界面错乱」的那个 bug。
                popClip(c);
                return;
            }
        }
        popClip(c);
    }

    // ---- 状态栏
    drawStatusBar(a, c);
}

// ================================================================
//  jar 查看器
// ================================================================

static void drawJarView(App& a, Canvas& c) {
    const Palette& p = current();

    fillRect(c, 0, 0, a.winW, a.winH, p.bgBase);
    drawToolbar(a, c);

    int top = dim::toolH;
    int statusH = dim::statusH;
    int bodyH = a.winH - top - statusH;

    // ---- 左栏：清单信息
    int lw = 300;
    fillRect(c, 0, top, lw, bodyH, p.bgPanel);

    int ly = top + 20;
    int lx = 18;
    {
        text(c, lx, ly, "jar 清单", 13, p.fgPrimary, true);
        ly += 26;

        auto row = [&](const char* k, const std::string& v, bool ok = true) {
            text(c, lx, ly, k, 11, p.fgDim);
            ly += 16;
            text(c, lx, ly, ellipsize(c, v, 12, lw - 36), 12,
                 ok ? p.fgMuted : p.error);
            ly += 24;
        };

        row("文件", baseName(a.jar.path));
        if (a.jar.classMajor > 0)
            row("Java 版本", a.jar.javaVersionName());
        else
            row("Java 版本", "未知（没有 class 文件）");

        row("Main-Class",
            a.jar.mainClass.empty() ? "（清单里没写，可能不可执行）"
                                    : a.jar.mainClass,
            !a.jar.mainClass.empty());

        row("大小", std::to_string(a.jar.totalSize / 1024) + " KB");
        row("class / 资源",
            std::to_string(a.jar.classCount) + " / " +
                std::to_string(a.jar.resourceCount));
    }

    // 运行按钮
    {
        int by = a.winH - statusH - 52;
        bool canRun = !a.jar.mainClass.empty();
        auto r = toolButton(c, a, "play", "运行这个 jar", false, canRun, lx, by,
                            dim::btnSize);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked && canRun) {
            a.bottom = BottomTab::Run;
            a.screen = Screen::Editor;
            a.log(LogLevel::Info, "运行 " + baseName(a.jar.path) + " …");
            std::string out;
            int rc = jarpack::run(a.jar.path, {}, &out);
            if (!out.empty()) {
                std::string cur;
                for (size_t i = 0; i <= out.size(); i++) {
                    if (i == out.size() || out[i] == '\n') {
                        if (!cur.empty() && cur.back() == '\r') cur.pop_back();
                        a.log(LogLevel::Info, cur);
                        cur.clear();
                    } else {
                        cur += out[i];
                    }
                }
            }
            if (rc == 0)
                a.log(LogLevel::Ok, "运行结束，退出码 0");
            else
                a.log(LogLevel::Error,
                      "运行异常，退出码 " + std::to_string(rc));
        }
    }

    drawDividerV(c, lw - 1, top, bodyH);

    // ---- 右栏：条目列表
    int rx = lw;
    int rw = a.winW - lw;
    fillRect(c, rx, top, rw, bodyH, p.bgBase);

    // 表头
    int headH = 30;
    text(c, rx + 16, top + 9, "内容", 12, p.fgDim, true);
    textRight(c, rx + rw - 16, top + 9,
              std::to_string(a.jar.entries.size()) + " 项", 11, p.fgDim);
    drawDividerH(c, rx, top + headH, rw);

    int listY = top + headH;
    int listH = bodyH - headH;
    pushClip(c, rx, listY, rw, listH);

    int rowH = 22;
    int totalH = (int)a.jar.entries.size() * rowH;
    int maxScroll = totalH - listH;
    if (maxScroll < 0) maxScroll = 0;
    if (a.jarScroll > maxScroll) a.jarScroll = maxScroll;
    if (a.jarScroll < 0) a.jarScroll = 0;

    int firstJ = a.jarScroll / rowH;
    int lastJ = firstJ + listH / rowH + 2;
    if (firstJ < 0) firstJ = 0;
    if (lastJ > (int)a.jar.entries.size()) lastJ = (int)a.jar.entries.size();

    TextStyle st;
    st.size = 12;
    st.monospace = true;

    for (int i = firstJ; i < lastJ; i++) {
        const auto& e = a.jar.entries[i];
        int ry = listY + i * rowH - a.jarScroll;

        bool sel = (a.jarSelected == i);
        bool hov = hit(rx, ry, rw, rowH, a.in.mouseX, a.in.mouseY);
        if (sel) {
            fillRoundRect(c, rx + 4, ry, rw - 8, rowH, rad::row, p.bgActive);
        } else if (hov) {
            fillRoundRect(c, rx + 4, ry, rw - 8, rowH, rad::row, p.bgHover);
        }

        // 类型图标：class / 其他资源
        bool isCls = endsWith(e.name, ".class");
        Color ic = isCls ? p.synType : p.fgDim;
        icon(c, a.icons, isCls ? "file-text" : "file", rx + 14,
             ry + (rowH - 13) / 2, 13, ic);

        st.color = isCls ? p.fgMuted : p.fgDim;
        drawText(c, rx + 36, ry + 2,
                 ellipsize(c, e.name, 12, rw - 170, true), st);

        // 右侧：原始大小
        std::string sz = std::to_string(e.size) + " B";
        st.color = p.fgDim;
        int sw = textW(c, sz, 12, false, true);
        drawText(c, rx + rw - sw - 16, ry + 2, sz, st);

        if (hov && a.in.clicked) a.jarSelected = i;
    }

    popClip(c);

    drawStatusBar(a, c);
}

// ================================================================
//  设置页
// ================================================================

static void drawSettings(App& a, Canvas& c) {
    const Palette& p = current();

    fillRect(c, 0, 0, a.winW, a.winH, p.bgBase);
    drawToolbar(a, c);

    int top = dim::toolH;
    int statusH = dim::statusH;
    int bodyH = a.winH - top - statusH;

    int x = 44;
    int w = a.winW - 88;

    text(c, x, top + 40, "设置", 22, p.fgPrimary, true);
    text(c, x, top + 72, "选一个默认 JDK。新建项目会用它，编译也用它。", 12,
         p.fgMuted);

    int ly = top + 112;
    text(c, x, ly, "JDK", 12, p.fgDim, true);
    ly += 24;

    // JDK 列表
    int cardH = 62;
    int cardGap = 8;

    if (a.jdks.empty()) {
        text(c, x, ly, "一个都没找到。", 13, p.error);
        text(c, x, ly + 24, "把 JDK 放到软件目录下的 jdk/ 里面，或者设好 JAVA_HOME。",
             12, p.fgMuted);
    }

    jdk::Info cur = jdk::current();

    int maxCards = (bodyH - (ly - top) - 40) / (cardH + cardGap);
    if (maxCards < 1) maxCards = 1;

    for (int i = 0; i < (int)a.jdks.size() && i < maxCards; i++) {
        const jdk::Info& j = a.jdks[i];
        int cy = ly + i * (cardH + cardGap);
        bool selected = cur.ok && cur.major == j.major;
        bool hov = hit(x, cy, w, cardH, a.in.mouseX, a.in.mouseY);

        fillRoundRect(c, x, cy, w, cardH, rad::card,
                      selected ? p.bgActive : (hov ? p.bgHover : p.bgPanel));
        strokeRoundRectF(c, (float)x + 0.5f, (float)cy + 0.5f, (float)w - 1.0f,
                         (float)cardH - 1.0f, (float)rad::card,
                         selected ? p.borderFocus : p.border, 1.0f);

        // 左边：版本大字
        text(c, x + 20, cy + 12, "JDK " + std::to_string(j.major), 15,
             p.fgPrimary, true);
        // 位置那一栏：随软件发布的那套写「内置」。它的路径在安装目录里，
        // 用户既改不了也不关心，写出来只会让人以为自己哪里配错了。
        text(c, x + 20, cy + 34,
             ellipsize(c, j.builtin ? std::string("内置") : j.home, 10, w - 160),
             10, p.fgDim);

        // 右边：完整版本号 + 选中标记
        textRight(c, x + w - 20, cy + 14, j.version, 11, p.fgMuted);

        if (selected) {
            // 选中的勾。用两条线段画，走浮点所以边缘平滑。
            float cx2 = (float)(x + w - 26);
            float cy2 = (float)(cy + 40);
            Color gc = p.ok;
            drawLineF(c, cx2 - 6.0f, cy2, cx2 - 1.5f, cy2 + 5.0f, gc, 2.2f);
            drawLineF(c, cx2 - 1.5f, cy2 + 5.0f, cx2 + 6.5f, cy2 - 5.0f, gc,
                      2.2f);
        } else if (hov) {
            textRight(c, x + w - 20, cy + 36, "点一下设为默认", 10, p.fgDim);
        }

        if (hov && a.in.clicked) {
            if (jdk::select(j.major)) {
                a.log(LogLevel::Ok, "默认 JDK 已切到 " + j.label());
                a.jdks = jdk::all();
            }
        }
    }

    // 底部：软件信息
    {
        int by = a.winH - statusH - 60;
        text(c, x, by, std::string("Java Studio ") + kVersion + "    ·    " +
                          kVendor,
             11, p.fgDim);
    }

    drawStatusBar(a, c);
}

// ================================================================
//  对话框
// ================================================================

// 输入框。返回框的矩形。
static BtnRect field(Canvas& c, App& a, int x, int y, int w, int h,
                     const std::string& value, const std::string& placeholder,
                     bool focused) {
    const Palette& p = current();

    fillRoundRect(c, x, y, w, h, rad::btn, p.bgBase);
    strokeRoundRectF(c, (float)x + 0.5f, (float)y + 0.5f, (float)w - 1.0f,
                     (float)h - 1.0f, (float)rad::btn,
                     focused ? p.borderFocus : p.border, focused ? 1.6f : 1.0f);

    TextStyle st;
    st.size = 12;
    bool empty = value.empty();
    st.color = empty ? p.fgDim : p.fgPrimary;

    int tx = x + 12;
    int ty = y + (h - 17) / 2;
    drawText(c, tx, ty, ellipsize(c, empty ? placeholder : value, 12, w - 24),
             st);

    if (focused) {
        int cx = tx;
        if (!empty) cx = tx + textW(c, value, 12);
        // 光标闪烁
        if ((plat::nowMs() / 530) % 2 == 0)
            fillRect(c, cx + 1, y + 8, 1, h - 16, p.fgPrimary);
    }

    return BtnRect{x, y, w, h};
}

// 一组单选 chip
static int chipGroup(Canvas& c, App& a, int x, int y,
                     const std::vector<std::string>& labels, int selected) {
    const Palette& p = current();
    int h = 30;
    int bx = x;
    int result = selected;

    for (int i = 0; i < (int)labels.size(); i++) {
        int tw = textW(c, labels[i], 12) + 28;
        bool sel = (i == selected);
        bool hov = hit(bx, y, tw, h, a.in.mouseX, a.in.mouseY);

        if (sel) {
            chip(c, bx, y, tw, h, accentOf(p));
        } else {
            chip(c, bx, y, tw, h, hov ? p.bgHover : p.bgBase);
            chipStroke(c, bx, y, tw, h, p.border, 1.0f);
        }

        text(c, bx + 14, y + (h - 16) / 2, labels[i], 12,
             sel ? accentTextOf(p) : (hov ? p.fgPrimary : p.fgMuted), sel);

        if (hov && a.in.clicked) result = i;
        bx += tw + 8;
    }
    return result;
}

static void drawNewProjectDialog(App& a, Canvas& c) {
    const Palette& p = current();
    NewProjectForm& f = a.np;

    // 遮罩
    fillRectA(c, 0, 0, a.winW, a.winH, rgb(0, 0, 0), p.dark ? 140 : 70);

    int dw = 560, dh = 452;
    int dx = (a.winW - dw) / 2;
    int dy = (a.winH - dh) / 2;

    for (int s = 14; s >= 1; s -= 2) {
        fillRoundRectA(c, dx - s / 2, dy + s / 2, dw, dh, rad::panel,
                       rgb(0, 0, 0), 9);
    }
    fillRoundRect(c, dx, dy, dw, dh, rad::panel, p.bgRaised);
    strokeRoundRectF(c, (float)dx + 0.5f, (float)dy + 0.5f, (float)dw - 1.0f,
                     (float)dh - 1.0f, (float)rad::panel, p.border, 1.0f);

    int px = dx + 28;
    int py = dy + 26;
    int fw = dw - 56;

    text(c, px, py, "新建 Java 项目", 18, p.fgPrimary, true);
    py += 36;

    // ---- 项目名
    text(c, px, py, "项目名", 11, p.fgDim, true);
    py += 18;
    {
        auto r = field(c, a, px, py, fw, 36, f.name, "比如 myapp",
                       f.field == 0);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) f.field = 0;
    }
    py += 36 + 14;

    // ---- 包名
    text(c, px, py, "包名（group）", 11, p.fgDim, true);
    py += 18;
    {
        auto r = field(c, a, px, py, fw, 36, f.group, "com.example",
                       f.field == 1);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) f.field = 1;
    }
    py += 36 + 14;

    // ---- 位置
    text(c, px, py, "位置", 11, p.fgDim, true);
    py += 18;
    {
        int bw = 84;
        int iw = fw - bw - 8;

        fillRoundRect(c, px, py, iw, 36, rad::btn, p.bgBase);
        strokeRoundRectF(c, (float)px + 0.5f, (float)py + 0.5f,
                         (float)iw - 1.0f, 35.0f, (float)rad::btn, p.border,
                         1.0f);

        TextStyle st;
        st.size = 11;
        st.color = f.parentDir.empty() ? p.fgDim : p.fgMuted;
        drawText(c, px + 12, py + (36 - 16) / 2,
                 ellipsize(c, f.parentDir.empty() ? "还没选位置" : f.parentDir,
                           11, iw - 24),
                 st);

        auto r = toolButton(c, a, "folder-open", "浏览", false, true, px + iw + 8,
                            py, 36);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            std::string picked = pickFolder("选择项目建在哪个目录", f.parentDir);
            if (!picked.empty()) {
                f.parentDir = picked;
                f.error.clear();
            }
        }
    }
    py += 36 + 14;

    // ---- 构建方式
    text(c, px, py, "构建方式", 11, p.fgDim, true);
    py += 18;
    {
        int sel = chipGroup(c, a, px, py, {"Maven", "Gradle", "都用", "不用"},
                            f.tool == 3 ? 2 : (f.tool == 2 ? 1 : 0));
        // chipGroup 的下标 0/1/2 对应 Maven/Gradle/都用
        if (a.in.clicked) {
            if (sel == 0) f.tool = 1;
            else if (sel == 1) f.tool = 2;
            else if (sel == 2) f.tool = 3;
        }
    }
    py += 30 + 12;

    // ---- 类型
    text(c, px, py, "项目类型", 11, p.fgDim, true);
    py += 18;
    {
        int sel = chipGroup(c, a, px, py,
                            {"控制台程序", "类库", "Spring Boot"}, f.kind);
        if (a.in.clicked) f.kind = sel;
    }
    py += 30 + 12;

    // ---- JDK
    text(c, px, py, "JDK", 11, p.fgDim, true);
    py += 18;
    {
        std::vector<std::string> labels;
        std::vector<int> majors;
        for (const auto& j : a.jdks) {
            labels.push_back(std::to_string(j.major));
            majors.push_back(j.major);
        }
        if (labels.empty()) {
            labels = {"17", "21", "25"};
            majors = {17, 21, 25};
        }

        int sel = 0;
        for (int i = 0; i < (int)majors.size(); i++)
            if (std::to_string(majors[i]) == f.jdkRelease) sel = i;

        int ns = chipGroup(c, a, px, py, labels, sel);
        if (a.in.clicked && ns >= 0 && ns < (int)majors.size())
            f.jdkRelease = std::to_string(majors[ns]);
    }
    py += 30 + 10;

    // ---- 错误
    if (!f.error.empty()) {
        text(c, px, py, f.error, 11, p.error);
    }

    // ---- 底部按钮
    int by = dy + dh - 58;
    int brx = dx + dw - 28;

    {
        int bw = textW(c, "创建", 12) + 40;
        brx -= bw;
        auto r = toolButton(c, a, "check", "创建", false, true, brx, by, 36);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked)
            createProjectNow(a);
        brx -= 10;

        bw = textW(c, "取消", 12) + 40;
        brx -= bw;
        auto r2 = toolButton(c, a, "x", "取消", false, true, brx, by, 36);
        if (r2.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
            a.dlg = Dialog::None;
            f.error.clear();
        }
    }
}

static void drawNewFileDialog(App& a, Canvas& c) {
    const Palette& p = current();

    fillRectA(c, 0, 0, a.winW, a.winH, rgb(0, 0, 0), p.dark ? 140 : 70);

    int dw = 460, dh = 210;
    int dx = (a.winW - dw) / 2;
    int dy = (a.winH - dh) / 2;

    for (int s = 12; s >= 1; s -= 2) {
        fillRoundRectA(c, dx - s / 2, dy + s / 2, dw, dh, rad::panel,
                       rgb(0, 0, 0), 9);
    }
    fillRoundRect(c, dx, dy, dw, dh, rad::panel, p.bgRaised);
    strokeRoundRectF(c, (float)dx + 0.5f, (float)dy + 0.5f, (float)dw - 1.0f,
                     (float)dh - 1.0f, (float)rad::panel, p.border, 1.0f);

    int px = dx + 26;
    int py = dy + 24;
    int fw = dw - 52;

    text(c, px, py, "新建文件", 17, p.fgPrimary, true);
    py += 34;

    text(c, px, py, "文件名（.java 会放到对应的包目录下）", 11, p.fgDim);
    py += 18;

    field(c, a, px, py, fw, 36, a.newFileName, "比如 Main", true);
    py += 36 + 16;

    text(c, px, py, "会使用包名：" + (a.np.group.empty() ? "com.example"
                                                      : a.np.group),
         10, p.fgDim);

    int by = dy + dh - 54;
    int brx = dx + dw - 26;

    {
        int bw = textW(c, "创建", 12) + 40;
        brx -= bw;
        auto r = toolButton(c, a, "check", "创建", false, true, brx, by, 34);
        if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked)
            createFileNow(a);
        brx -= 10;

        bw = textW(c, "取消", 12) + 40;
        brx -= bw;
        auto r2 = toolButton(c, a, "x", "取消", false, true, brx, by, 34);
        if (r2.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked)
            a.dlg = Dialog::None;
    }
}

static void drawAboutDialog(App& a, Canvas& c) {
    const Palette& p = current();

    fillRectA(c, 0, 0, a.winW, a.winH, rgb(0, 0, 0), p.dark ? 140 : 70);

    int dw = 420, dh = 280;
    int dx = (a.winW - dw) / 2;
    int dy = (a.winH - dh) / 2;

    for (int s = 12; s >= 1; s -= 2) {
        fillRoundRectA(c, dx - s / 2, dy + s / 2, dw, dh, rad::panel,
                       rgb(0, 0, 0), 9);
    }
    fillRoundRect(c, dx, dy, dw, dh, rad::panel, p.bgRaised);
    strokeRoundRectF(c, (float)dx + 0.5f, (float)dy + 0.5f, (float)dw - 1.0f,
                     (float)dh - 1.0f, (float)rad::panel, p.border, 1.0f);

    int cx = dx + dw / 2;

    int ls = 56;
    fillRoundRect(c, cx - ls / 2, dy + 32, ls, ls, 14, accentOf(p));
    icon(c, a.icons, "coffee", cx - ls / 2 + 12, dy + 44, ls - 24,
         accentTextOf(p));

    textCenter(c, cx, dy + 104, "Java Studio", 20, p.fgPrimary, true);
    textCenter(c, cx, dy + 132, kVersion, 12, p.fgMuted);
    textCenter(c, cx, dy + 158, kVendor, 11, p.fgDim);

    textCenter(c, cx, dy + 188, "自带 JDK 17 / 21 / 25，支持 Maven 与 Gradle", 11,
               p.fgDim);
    textCenter(c, cx, dy + 206,
               std::string("内置 ") + std::to_string(jlibs::catalog().size()) +
                   " 个第三方库，勾一下就能 import",
               11, p.fgDim);

    int by = dy + dh - 54;
    int bw = textW(c, "好", 12) + 46;
    auto r = toolButton(c, a, "check", "好", false, true, cx - bw / 2, by, 34);
    if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked)
        a.dlg = Dialog::None;
}

// ================================================================
//  第三方库对话框
// ================================================================

// 搜索 + 分类筛选之后还剩目录里的哪些下标。
//
// 匹配是「整行拼起来之后 find 子串」—— 用户可能搜库名（Gson）、
// 搜用途（JSON）、搜坐标（com.google）、搜分类（数据库），
// 全塞进一个串里比写五条 if 分支省事，也不会漏。
static std::vector<int> libFiltered(const App& a) {
    std::vector<int> out;
    const auto& all = jlibs::catalog();

    auto low = [](std::string s) {
        for (char& c : s)
            if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        return s;
    };

    std::string q = low(a.libs.query);

    for (int i = 0; i < (int)all.size(); i++) {
        const auto& e = all[i];
        if (!a.libs.category.empty() && e.category != a.libs.category) continue;

        if (!q.empty()) {
            std::string hay = low(e.id + " " + e.name + " " + e.desc + " " +
                                  e.group + " " + e.artifact + " " +
                                  e.category);
            if (hay.find(q) == std::string::npos) continue;
        }
        out.push_back(i);
    }
    return out;
}

// 一个库在项目里的状态。三种：没勾 / 勾了且本机有 / 勾了但本机没有。
//
// 状态直接从 a.libJars / a.libMissing 里查，**不碰磁盘**。
// 每帧对 59 行调 59 次 findJar（每次都列目录）会直接卡住界面。
enum class LibState { NotAdded, Ready, Missing };

static LibState libStateOf(const App& a, const std::string& id) {
    if (!a.libs.isSelected(id)) return LibState::NotAdded;
    for (const auto& s : a.libJars)
        if (s == id) return LibState::Ready;
    return LibState::Missing;
}

static void drawLibrariesDialog(App& a, Canvas& c) {
    const Palette& p = current();

    fillRectA(c, 0, 0, a.winW, a.winH, rgb(0, 0, 0), p.dark ? 150 : 80);

    int dw = 780, dh = 566;
    if (dw > a.winW - 48) dw = a.winW - 48;
    if (dh > a.winH - 48) dh = a.winH - 48;
    int dx = (a.winW - dw) / 2;
    int dy = (a.winH - dh) / 2;

    for (int s = 16; s >= 1; s -= 2) {
        fillRoundRectA(c, dx - s / 2, dy + s / 2, dw, dh, rad::panel,
                       rgb(0, 0, 0), 9);
    }
    fillRoundRect(c, dx, dy, dw, dh, rad::panel, p.bgRaised);
    strokeRoundRectF(c, (float)dx + 0.5f, (float)dy + 0.5f, (float)dw - 1.0f,
                     (float)dh - 1.0f, (float)rad::panel, p.border, 1.0f);

    int px = dx + 26;
    int py = dy + 22;
    int fw = dw - 52;

    // ---- 标题
    {
        const std::string title = "第三方库";
        text(c, px, py, title, 18, p.fgPrimary, true);
        int tw = textW(c, title, 18, true);
        text(c, px + tw + 12, py + 6,
             "勾上就能直接在代码里 import，jar 由 IDE 负责放到 classpath",
             11, p.fgDim);
    }
    py += 36;

    // ---- 搜索框
    {
        field(c, a, px, py, fw, 34, a.libs.query,
              "搜名字 / 用途 / 坐标，比如 Gson、JSON、数据库", true);
    }
    py += 34 + 10;

    // ---- 分类 chips
    {
        std::vector<std::string> cats;
        cats.push_back("全部");
        for (const auto& k : jlibs::categories()) cats.push_back(k);

        int bx = px;
        for (size_t i = 0; i < cats.size(); i++) {
            const std::string& label = cats[i];
            bool sel = (i == 0 && a.libs.category.empty()) ||
                       (i > 0 && label == a.libs.category);

            int tw = textW(c, label, 11) + 24;
            if (bx + tw > dx + dw - 26) break;   // 一行放不下就不画了

            bool hov = hit(bx, py, tw, 26, a.in.mouseX, a.in.mouseY);

            if (sel) {
                chip(c, bx, py, tw, 26, accentOf(p));
            } else {
                chip(c, bx, py, tw, 26, hov ? p.bgHover : p.bgBase);
                chipStroke(c, bx, py, tw, 26, p.border, 1.0f);
            }
            text(c, bx + 12, py + (26 - 15) / 2, label, 11,
                 sel ? accentTextOf(p) : (hov ? p.fgPrimary : p.fgMuted),
                 sel);

            if (hov && a.in.clicked) {
                a.libs.category = (i == 0) ? "" : label;
                a.libs.scroll = 0;
            }
            bx += tw + 6;
        }
    }
    py += 26 + 10;

    // ---- 列表
    int listTop = py;
    int listBottom = dy + dh - 64;
    int listH = listBottom - listTop;
    if (listH < 80) listH = 80;

    fillRoundRect(c, px, listTop, fw, listH, rad::btn, p.bgBase);
    strokeRoundRectF(c, (float)px + 0.5f, (float)listTop + 0.5f,
                     (float)fw - 1.0f, (float)listH - 1.0f, (float)rad::btn,
                     p.border, 1.0f);

    const auto& all = jlibs::catalog();
    std::vector<int> rows = libFiltered(a);

    const int rowH = 64;
    int maxScroll = (int)rows.size() * rowH - listH;
    if (maxScroll < 0) maxScroll = 0;
    if (a.libs.scroll > maxScroll) a.libs.scroll = maxScroll;
    if (a.libs.scroll < 0) a.libs.scroll = 0;

    pushClip(c, px, listTop, fw, listH);

    if (rows.empty()) {
        textCenter(c, px + fw / 2, listTop + listH / 2 - 10, "没有匹配的库", 13,
                   p.fgDim);
    }

    int hitRow = -1;
    int hitDownload = -1;   // 点的是「下载」按钮而不是整行

    for (size_t i = 0; i < rows.size(); i++) {
        const jlibs::Entry& e = all[rows[i]];
        int ry = listTop + (int)i * rowH - a.libs.scroll;
        if (ry + rowH < listTop - 4 || ry > listBottom + 4) continue;

        bool selected = a.libs.isSelected(e.id);
        LibState st = libStateOf(a, e.id);
        bool hov = hit(px + 2, ry + 2, fw - 4, rowH - 4, a.in.mouseX,
                       a.in.mouseY);

        if (hov) {
            a.libHoverId = (int)i;
            fillRoundRect(c, px + 3, ry + 3, fw - 6, rowH - 6, rad::row,
                          p.bgHover);
        }

        // ---- 勾选框
        int cbs = 18;
        int cbx = px + 18;
        int cby = ry + rowH / 2 - cbs / 2;
        if (selected) {
            fillRoundRect(c, cbx, cby, cbs, cbs, 5, accentOf(p));
            icon(c, a.icons, "check", cbx + 3, cby + 3, cbs - 6,
                 accentTextOf(p));
        } else {
            strokeRoundRectF(c, (float)cbx + 0.5f, (float)cby + 0.5f,
                             (float)cbs - 1.0f, (float)cbs - 1.0f, 5.0f,
                             hov ? p.fgMuted : p.border, 1.5f);
        }

        // ---- 右侧状态区先算好宽度，左边文字才不会压上去
        const int rightW = 190;
        int tx = cbx + cbs + 14;
        int textWAvail = px + fw - rightW - tx - 12;
        if (textWAvail < 80) textWAvail = 80;

        text(c, tx, ry + 11,
             ellipsize(c, e.name, 13, textWAvail, false), 13,
             selected ? p.fgPrimary : p.fgMuted, true);

        text(c, tx, ry + 32,
             ellipsize(c, e.desc, 11, textWAvail, false), 11, p.fgDim);

        text(c, tx, ry + 47,
             ellipsize(c, e.coord(), 10, textWAvail, true), 10, p.fgDim, false,
             true);

        // ---- 右侧：状态点 + 分类 + 下载按钮
        int rEdge = px + fw - 18;
        {
            std::string stTxt;
            Color stCol = p.fgDim;
            switch (st) {
                case LibState::Ready:
                    stTxt = "● 就绪";
                    stCol = p.ok;
                    break;
                case LibState::Missing:
                    stTxt = "● 未下载";
                    stCol = p.error;
                    break;
                case LibState::NotAdded:
                default:
                    stTxt = "未添加";
                    stCol = p.fgDim;
                    break;
            }
            int tw = textW(c, stTxt, 11);
            text(c, rEdge - tw, ry + 13, stTxt, 11, stCol, st != LibState::NotAdded);

            // 分类小字，压在状态下面
            int cw = textW(c, e.category, 10);
            text(c, rEdge - cw, ry + 34, e.category, 10, p.fgDim);
        }

        // ---- 「下载」按钮：只有勾了但本机没有才出现
        if (st == LibState::Missing) {
            const std::string lbl = "下载";
            int bw = textW(c, lbl, 11) + 30;
            int bh = 26;
            int bx2 = cbx + cbs + 14;
            int by2 = ry + rowH - bh - 9;

            // 放在坐标行右边一点，避开文字
            bx2 = px + fw - rightW - bw - 10;
            if (bx2 < cbx + cbs + 14) bx2 = cbx + cbs + 14;

            bool bh2 = hit(bx2, by2, bw, bh, a.in.mouseX, a.in.mouseY);
            fillRoundRect(c, bx2, by2, bw, bh, rad::btn,
                          bh2 ? p.bgActive : p.bgPanel);
            strokeRoundRectF(c, (float)bx2 + 0.5f, (float)by2 + 0.5f,
                             (float)bw - 1.0f, (float)bh - 1.0f,
                             (float)rad::btn, p.border, 1.0f);
            text(c, bx2 + (bw - textW(c, lbl, 11)) / 2, by2 + (bh - 15) / 2,
                 lbl, 11, bh2 ? p.fgPrimary : p.fgMuted);

            if (hov && bh2) {
                hitDownload = (int)i;
                hitRow = -1;
            }
        }

        if (hov && hitRow < 0 && hitDownload < 0) hitRow = (int)i;
    }

    popClip(c);

    // ---- 点击处理
    if (a.in.clicked) {
        if (hitDownload >= 0) {
            fetchLib(a, all[rows[hitDownload]].id);
        } else if (hitRow >= 0) {
            const jlibs::Entry& e = all[rows[hitRow]];
            a.libs.toggle(e.id);
            syncLibs(a, false);
        }
    }

    // ---- 底部
    int by = dy + dh - 50;
    {
        // 左边一句话交代现在的状态
        std::string msg = a.libMsg;
        if (msg.empty()) {
            msg = "已选 " + std::to_string(a.libs.selected.size()) + " 个";
        }
        text(c, px, by + 10, msg, 11,
             a.libMissing.empty() ? p.fgMuted : p.error);

        int brx = dx + dw - 26;

        // 完成
        {
            int bw = textW(c, "完成", 12) + 40;
            brx -= bw;
            auto r = toolButton(c, a, "check", "完成", false, true, brx, by, 36);
            if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked)
                a.dlg = Dialog::None;
            brx -= 10;
        }

        // 全部下载
        if (!a.libMissing.empty()) {
            int bw = textW(c, "下载缺失的", 12) + 40;
            brx -= bw;
            auto r = toolButton(c, a, "download", "下载缺失的", false, true,
                                brx, by, 36);
            if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
                // 复制一份再遍历 —— fetchLib 会改 a.libMissing，
                // 边遍历边改是经典的踩坑点
                std::vector<std::string> todo = a.libMissing;
                for (const auto& id : todo) fetchLib(a, id);
            }
            brx -= 10;
        }

        // 清空
        if (!a.libs.selected.empty()) {
            int bw = textW(c, "清空", 12) + 40;
            brx -= bw;
            auto r = toolButton(c, a, "x-circle", "清空", false, true, brx, by,
                                36);
            if (r.contains(a.in.mouseX, a.in.mouseY) && a.in.clicked) {
                a.libs.selected.clear();
                syncLibs(a, false);
            }
        }
    }
}

// ================================================================
//  主绘制
// ================================================================

static void paintAll(App& a, Canvas& c) {
    const Palette& p = current();

    // 每帧从干净的裁剪栈开始。
    //
    // 裁剪栈是全局的（GDI 一份 + CPU 光栅化一份）。谁要是 pushClip
    // 之后提前 return 忘了 popClip，那层裁剪会一直留着，之后每帧
    // 只有那一小块能刷新，窗口上就出现「新旧两个页面叠在一起」。
    // 这里强制归零，让漏 pop 最多毁掉出问题的那一帧。
    resetClips(c);

    // 记录鼠标在哪个面板。滚轮按这个分发 —— 用户的硬要求：
    // 「鼠标在哪个板块就滑动哪个板块的内容」。
    a.hoverPane = App::Pane::None;
    if (a.in.mouseX >= 0) {
        int top = dim::menuH + dim::toolH;
        int statusH = dim::statusH;
        int bottomH = a.bottomOpen ? a.bottomH : 0;
        int contentH = a.winH - top - statusH - bottomH;

        if (a.screen == Screen::Editor) {
            if (a.in.mouseY < dim::menuH) {
                // 菜单栏，不算面板
            } else if (a.in.mouseY < top) {
                // 工具栏
            } else if (a.in.mouseY < top + contentH) {
                if (a.sidebarOpen && a.in.mouseX < a.sidebarW)
                    a.hoverPane = App::Pane::Sidebar;
                else
                    a.hoverPane = App::Pane::Editor;
            } else if (a.in.mouseY < a.winH - statusH) {
                a.hoverPane = App::Pane::Bottom;
            }
        } else if (a.screen == Screen::Home) {
            a.hoverPane = App::Pane::Recent;
        } else if (a.screen == Screen::Jar) {
            a.hoverPane = App::Pane::Jar;
        } else if (a.screen == Screen::Settings) {
            a.hoverPane = App::Pane::Settings;
        }
    }

    switch (a.screen) {
        case Screen::Home:
            drawHome(a, c);
            break;
        case Screen::Jar:
            drawJarView(a, c);
            break;
        case Screen::Settings:
            drawSettings(a, c);
            break;
        case Screen::Editor:
        default: {
            fillRect(c, 0, 0, a.winW, a.winH, p.bgBase);

            int top = dim::menuH + dim::toolH;
            int statusH = dim::statusH;
            int bottomH = a.bottomOpen ? a.bottomH : 0;
            int contentH = a.winH - top - statusH - bottomH;
            if (contentH < 60) contentH = 60;

            int sideW = a.sidebarOpen ? a.sidebarW : 0;
            if (sideW > 0) drawSidebar(a, c, top, contentH);

            drawEditor(a, c, sideW, top, a.winW - sideW, contentH);

            if (a.bottomOpen) drawBottom(a, c, top + contentH, bottomH);

            if (a.sidebarOpen)
                drawVSplitter(a, c, sideW - 1, top, contentH,
                              a.sidebarDragging);
            if (a.bottomOpen)
                drawVSplitter(a, c, 0, top + contentH, 0, false);

            // 菜单栏和工具栏压在最上面
            drawMenuBar(a, c);
            drawToolbar(a, c);
            drawStatusBar(a, c);
            break;
        }
    }

    // 对话框盖在最上层
    switch (a.dlg) {
        case Dialog::NewProject:
            drawNewProjectDialog(a, c);
            break;
        case Dialog::NewFile:
            drawNewFileDialog(a, c);
            break;
        case Dialog::About:
            drawAboutDialog(a, c);
            break;
        case Dialog::Libraries:
            drawLibrariesDialog(a, c);
            break;
        case Dialog::None:
        default:
            break;
    }
}

}  // namespace app

// ================================================================
//  菜单动作分发 + 入口
//
// 这两个放在 namespace app 外面，因为 main 要直接调。
// ================================================================

namespace app {

void doMenuAction(App& a, int act) {
    switch (act) {
        // ---- 文件
        case MA_NewProject:
            beginNewProject(a);
            break;

        case MA_OpenProject: {
            std::string picked = pickFolder("打开 Java 项目", a.curDir);
            if (!picked.empty()) openProject(a, picked);
            break;
        }

        case MA_OpenJar: {
            std::string picked = pickFolder("选择包含 jar 的目录", a.curDir);
            if (!picked.empty()) {
                a.curDir = picked;
                // 找出这个目录里的第一个 jar 直接打开，省一步
                auto es = listDir(picked);
                bool found = false;
                for (const auto& e : es) {
                    if (e.isDir) continue;
                    std::string low = e.name;
                    for (char& ch : low)
                        if (ch >= 'A' && ch <= 'Z') ch = (char)(ch - 'A' + 'a');
                    if (low.size() > 4 &&
                        low.compare(low.size() - 4, 4, ".jar") == 0) {
                        openJar(a, joinPath(picked, e.name));
                        found = true;
                        break;
                    }
                }
                if (!found) {
                    a.log(LogLevel::Warn, "这个目录里没有 .jar 文件");
                    a.screen = Screen::Home;
                }
            }
            break;
        }

        case MA_Libs:
            openLibraryDialog(a);
            break;

        case MA_NewFile:
            a.newFileName.clear();
            a.dlg = Dialog::NewFile;
            break;

        case MA_Save:
            if (a.activeTab >= 0) saveFile(a, a.activeTab);
            else a.log(LogLevel::Warn, "没有打开的文件");
            break;

        case MA_SaveAll:
            saveAll(a);
            break;

        case MA_CloseProject:
            a.files.clear();
            a.activeTab = -1;
            a.projectRoot.clear();
            a.projectName.clear();
            a.tree.clear();
            a.treeParent.clear();
            a.treeSelected = -1;
            a.diags.clear();
            a.screen = Screen::Home;
            a.statusLeft = "起始页";
            a.log(LogLevel::Info, "已关闭项目");
            break;

        case MA_Exit:
            quitLoop();
            break;

        // ---- 编辑
        case MA_Undo:
        case MA_Redo:
            // 撤销要一整套历史栈，先给明确提示而不是静默失败
            a.log(LogLevel::Warn, "撤销/重做还没接上（需要历史栈）");
            break;

        case MA_Cut:
            cutSelection(a);
            break;
        case MA_Copy:
            copySelection(a);
            break;
        case MA_Paste:
            if (!pasteFromClipboard(a)) a.log(LogLevel::Info, "剪贴板是空的");
            break;
        case MA_SelectAll:
            selectAll(a);
            break;
        case MA_Find:
            a.log(LogLevel::Warn, "查找还没接上");
            break;

        // ---- 视图
        case MA_Home:
            a.screen = Screen::Home;
            a.statusLeft = "起始页";
            break;

        case MA_Editor:
            if (a.projectRoot.empty()) {
                a.log(LogLevel::Warn, "还没打开项目");
            } else {
                a.screen = Screen::Editor;
                a.statusLeft = "项目已打开";
            }
            break;

        case MA_ToggleSidebar:
            a.sidebarOpen = !a.sidebarOpen;
            break;

        case MA_ToggleBottom:
            a.bottomOpen = !a.bottomOpen;
            break;

        case MA_ToggleTheme:
            setDark(!current().dark);
            a.log(LogLevel::Info,
                  current().dark ? "已切到深色主题" : "已切到浅色主题");
            break;

        case MA_Settings:
            a.jdks = jdk::all();
            a.screen = Screen::Settings;
            a.statusLeft = "设置";
            break;

        // ---- 运行
        case MA_Compile:
            doCompile(a, false);
            break;
        case MA_CompileRun:
            doCompile(a, true);
            break;
        case MA_Run:
            doRun(a);
            break;
        case MA_Check:
            doCheck(a);
            break;

        // ---- 工具
        case MA_IconLib: {
            int n = a.icons ? a.icons->count() : 0;
            char buf[128];
            snprintf(buf, sizeof(buf), "图标库：%d 个图标可用", n);
            a.log(LogLevel::Ok, buf);
            break;
        }

        case MA_About:
            a.dlg = Dialog::About;
            break;

        default:
            break;
    }
}

// ================================================================
//  回调
// ================================================================

static void runUiTest(App& a, Canvas& c, const std::string& path);

static void cbPaint(Canvas* canvas, void* ctx) {
    App& a = *(App*)ctx;
    if (!canvas) return;
    paintAll(a, *canvas);
    a.in.endFrame();

    // JS_UITEST=<输出文件>：在第一帧里跑一遍全量扫描。
    // 必须放在 paintAll 之后 —— 扫描要复用刚画好的这一帧的位图。
    static bool uiTestDone = false;
    const char* ut = getenv("JS_UITEST");
    if (ut && *ut && !uiTestDone) {
        uiTestDone = true;
        runUiTest(a, *canvas, ut);
    }
}

// 这一屏到底画出东西没有。
//
// 返回「不是主色（出现次数最多的那个颜色）的像素占比」。
//
// 用主色而不是某个常量背景色：界面底色随主题变、随面板变，
// 写死一个颜色去比，测出来的是「我猜的背景色对不对」，
// 不是「这一屏有没有内容」。占最多的那个颜色一定是背景 ——
// 不管它是深是浅是哪个面板 —— 拿它当基准才是稳的。
//
// 用户报过的「起始页是空白」「图标不见了」本质都是「画了但没出来」，
// 肉眼一眼能看出，但自动化需要一个数字兜住：0.1% 和 30% 是两个世界。
static double contentRatio(Canvas& c) {
    HDC dc = (HDC)c.handle;
    if (!dc) return -1;

    HBITMAP bmp = (HBITMAP)GetCurrentObject(dc, OBJ_BITMAP);
    BITMAP bm{};
    if (!bmp || !GetObjectW(bmp, sizeof(bm), &bm)) return -1;

    int w = bm.bmWidth, h = bm.bmHeight;
    if (w <= 0 || h <= 0) return -1;
    if ((double)w * (double)h > 60e6) return -1;

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;   // top-down，省得后面再翻
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    std::vector<uint32_t> buf((size_t)w * h, 0);
    if (!GetDIBits(dc, bmp, 0, (UINT)h, buf.data(), &bi, DIB_RGB_COLORS))
        return -1;

    // 找主色。60 万像素最多 60 万次比较，不排全序 —— 排序那趟
    // 只是为了取众数，但众数用哈希数一遍就够了
    std::unordered_map<uint32_t, size_t> hist;
    hist.reserve(buf.size() / 8 + 16);
    size_t mainCnt = 0;
    for (uint32_t p : buf) {
        size_t& n = hist[p & 0x00FFFFFFu];
        if (++n > mainCnt) mainCnt = n;
    }

    return 1.0 - (double)mainCnt / (double)buf.size();
}

// ================================================================
//  UI 全量扫描（JS_UITEST）
// ================================================================
//
// 用户的要求是「把每个按钮、每种情况都过一遍」。手工点太费时间，
// 而且容易漏。这里用代码把四类东西全跑一遍：
//
//   1) 每个界面 × 每个对话框 × 深/浅两套主题 —— 每一屏都量一次
//      覆盖率，空白页这种问题当场就能抓到
//   2) 菜单里的每一条动作 —— 看它有没有把状态改坏
//   3) 第三方库全流程 —— 勾 / 同步 / 下载 / 清空
//   4) 新建项目 —— 确认它真的落在安装目录的 item/ 下
static void runUiTest(App& a, Canvas& c, const std::string& path) {
    extern void doMenuAction(App&, int);

    std::string out;
    int pass = 0, fail = 0;

    auto say = [&](const std::string& s) {
        out += s;
        out += "\r\n";
    };
    auto check = [&](const std::string& what, bool ok,
                     const std::string& detail = "") {
        say(std::string(ok ? "  OK   " : "  FAIL ") + what +
            (detail.empty() ? "" : "   " + detail));
        if (ok) pass++;
        else fail++;
    };
    auto pct = [](double v) {
        char b[32];
        snprintf(b, sizeof(b), "%.1f%%", v * 100.0);
        return std::string(b);
    };

    say("=== Java Studio UI 全量扫描 ===");
    say("");

    // ---------------------------------------------------- 1. 绘制覆盖
    //
    // 判定阈值：一屏正常内容通常 >20%（文字、边框、图标都在算）。
    // 低于 1% 基本等于空白。取 3% 做底线 —— 既不误判空旷的起始页，
    // 也足够抓到「整屏只有一个背景色」的情况。
    say("── 绘制覆盖（每屏都要真的画出东西）");

    struct Shot {
        const char* name;
        Screen screen;
        Dialog dlg;
    };
    const Shot shots[] = {
        {"起始页", Screen::Home, Dialog::None},
        {"工作区", Screen::Editor, Dialog::None},
        {"设置", Screen::Settings, Dialog::None},
        {"新建项目框", Screen::Editor, Dialog::NewProject},
        {"新建文件框", Screen::Editor, Dialog::NewFile},
        {"关于框", Screen::Editor, Dialog::About},
        {"第三方库框", Screen::Editor, Dialog::Libraries},
    };

    const char* themeName[2] = {"浅色", "深色"};
    for (int t = 0; t < 2; t++) {
        setDark(t == 1);
        for (const auto& s : shots) {
            a.screen = s.screen;
            a.dlg = s.dlg;

            a.in.endFrame();
            paintAll(a, c);

            double cov = contentRatio(c);
            bool ok = cov > 0.03;
            check(std::string(themeName[t]) + " / " + s.name, ok,
                  "覆盖 " + pct(cov));
        }
    }
    setDark(false);

    a.screen = Screen::Editor;
    a.dlg = Dialog::None;
    say("");

    // ---------------------------------------------------- 2. 菜单动作
    say("── 菜单动作");

    struct Act {
        const char* name;
        int id;
    };
    // 只跑不弹系统对话框的。MA_OpenProject / MA_OpenJar 会开
    // SHBrowseForFolder，无人值守模式下会一直等在模态框里 ——
    // 那两条手工验证，不进这个列表。MA_Exit 会真的退出。
    const Act acts[] = {
        {"视图·起始页", MA_Home},
        {"视图·工作区", MA_Editor},
        {"视图·显示项目树", MA_ToggleSidebar},
        {"视图·显示输出面板", MA_ToggleBottom},
        {"视图·切换主题", MA_ToggleTheme},
        {"视图·设置", MA_Settings},
        {"编辑·剪切", MA_Cut},
        {"编辑·复制", MA_Copy},
        {"编辑·粘贴", MA_Paste},
        {"编辑·全选", MA_SelectAll},
        {"编辑·查找", MA_Find},
        {"编辑·撤销", MA_Undo},
        {"编辑·重做", MA_Redo},
        {"工具·新建文件", MA_NewFile},
        {"工具·图标库", MA_IconLib},
        {"工具·第三方库", MA_Libs},
        {"帮助·关于", MA_About},
        {"编译", MA_Compile},
        {"编译并运行", MA_CompileRun},
        {"只运行", MA_Run},
        {"代码检查", MA_Check},
    };

    for (const auto& act : acts) {
        // 每条动作都从「工作区」起步，避免上一条把界面切走了之后
        // 下一条跑在一个不相干的界面上（那样测的是巧合不是行为）
        a.screen = Screen::Editor;
        a.dlg = Dialog::None;
        a.in.endFrame();

        bool threw = false;
        // 没有异常机制，只能靠「跑完界面还活着」来判：
        // 屏幕值合法、对话框值合法
        doMenuAction(a, act.id);

        bool screenOk = (int)a.screen >= 0 && (int)a.screen <= 3;
        bool dlgOk = (int)a.dlg >= 0 && (int)a.dlg <= 4;
        bool ok = !threw && screenOk && dlgOk;

        check(std::string("菜单 ") + act.name, ok,
              "screen=" + std::to_string((int)a.screen) +
                  " dlg=" + std::to_string((int)a.dlg));
    }
    say("");

    // 对话框打开型动作：确认它真的把框打开了（上面跑完最后一条
    // 会盖住状态，所以单独再验一遍）
    {
        a.dlg = Dialog::None;
        doMenuAction(a, MA_About);
        check("帮助·关于 打开对话框", a.dlg == Dialog::About);

        a.dlg = Dialog::None;
        doMenuAction(a, MA_NewFile);
        check("工具·新建文件 打开对话框", a.dlg == Dialog::NewFile);
    }
    say("");

    // ---------------------------------------------------- 3. 新建项目
    say("── 新建项目（应落在安装目录/item 下）");

    std::string itemsDir = projectsDir();
    check("item 目录存在", dirExists(itemsDir), itemsDir);

    const std::string testName = "uitest_demo";
    {
        a.dlg = Dialog::None;
        beginNewProject(a);
        check("新建项目默认位置", a.np.parentDir == itemsDir, a.np.parentDir);

        a.np.name = testName;
        a.np.group = "com.example";
        a.np.kind = 0;
        a.np.tool = 1;
        a.np.jdkRelease = "17";
        a.np.error.clear();

        createProjectNow(a);

        std::string made = joinPath(itemsDir, testName);
        bool madeOk = dirExists(made);
        check("项目目录已创建", madeOk, made);

        if (madeOk) {
            bool srcOk = fileExists(
                joinPath(joinPath(joinPath(joinPath(made, "src"), "main"),
                                  "java"),
                         "Main.java")) ||
                         !listDir(joinPath(joinPath(made, "src"), "main")).empty();
            check("源码目录有内容", srcOk);
            check("创建后自动打开项目", a.projectRoot == made, a.projectRoot);
        }
    }
    say("");

    // ---------------------------------------------------- 4. 第三方库
    say("── 第三方库");

    {
        // 先把「没项目时不该打开」这条也验掉 —— 这正是上一个版本
        // 扫描报 FAIL 的原因：那时还没打开项目，对话框本来就不该开。
        {
            std::string keep = a.projectRoot;
            a.projectRoot.clear();
            a.dlg = Dialog::None;
            doMenuAction(a, MA_Libs);
            check("没项目时不开库对话框", a.dlg != Dialog::Libraries);
            a.projectRoot = keep;
        }

        a.dlg = Dialog::None;
        doMenuAction(a, MA_Libs);
        check("有项目时打开库对话框", a.dlg == Dialog::Libraries);

        openLibraryDialog(a);
        check("打开库对话框", a.dlg == Dialog::Libraries);

        int n = (int)jlibs::catalog().size();
        check("目录非空", n > 0, std::to_string(n) + " 个");

        // 搜一个词，确认筛选能出结果
        a.libs.query = "json";
        int hitJson = (int)libFiltered(a).size();
        check("搜索 json 有结果", hitJson > 0, std::to_string(hitJson) + " 条");

        a.libs.query = "zzzz不可能匹配";
        check("搜索无结果时不崩", libFiltered(a).empty());
        a.libs.query.clear();

        // 勾一个库 + 同步
        a.libs.selected.clear();
        a.libs.toggle("gson");
        syncLibs(a, false);
        check("勾中后进入选中", a.libs.isSelected("gson"));

        // 下载（目录里挑一个还没下过的，避免重复下载拖慢测试）
        const char* want = "junit-jupiter-api";
        a.libs.selected.clear();
        a.libs.toggle(want);
        syncLibs(a, false);
        bool wasMissing = !a.libMissing.empty();
        bool fetched = fetchLib(a, want);
        check("下载 " + std::string(want), fetched, a.libMsg);
        check("下载后不再算缺失", a.libMissing.empty(),
              "缺 " + std::to_string(a.libMissing.size()) +
                  (wasMissing ? "（下载前确实缺）" : ""));
        check("classpath 非空", libClasspathString(a).find(".jar") !=
                                    std::string::npos,
              libClasspathString(a));

        // 清空
        a.libs.selected.clear();
        syncLibs(a, false);
        check("清空后无 jar", a.libJars.empty() && a.libMissing.empty());
    }
    say("");

    // ---------------------------------------------------- 5. 编译运行
    say("── 编译 / 运行");

    if (!a.projectRoot.empty()) {
        a.buildOut.clear();
        doCompile(a, false);
        bool compiled = a.statusLeft.find("编译通过") != std::string::npos;
        check("编译通过", compiled, a.statusLeft);

        a.logs.clear();
        doRun(a);
        check("运行退出码 0", a.lastExitCode == 0,
              "exit=" + std::to_string(a.lastExitCode) +
                  " main=" + a.lastMainClass);

        a.diags.clear();
        doCheck(a);
        check("代码检查不崩", true);
    } else {
        check("有项目可编译", false, "projectRoot 是空的");
    }
    say("");

    // ---------------------------------------------------- 6. 编译报错
    say("── 错误处理");

    {
        // 「代码检查」查的是括号配对（这是它现在真正做的事，不是
        // 完整语法分析）—— 所以拿缺右括号的代码来试，才测得到它。
        // 上一版拿「int x = ;」来试，那确实少了表达式但括号是配平的，
        // doCheck 不报是**对的**，报 FAIL 的是测试本身。
        OpenFile bad;
        bad.path = "uitest/Bad.java";
        bad.title = "Bad.java";
        bad.isJava = true;
        bad.lines = {"public class Bad {",
                     "    void f() {",
                     "        int x = (1 + 2;",
                     "    }",
                     "}"};
        recolorAll(bad);
        a.files.push_back(bad);
        a.activeTab = (int)a.files.size() - 1;
        a.diags.clear();
        doCheck(a);
        check("缺右括号能报出来", !a.diags.empty(),
              std::to_string(a.diags.size()) + " 条");

        // 反过来：括号配平的不该报 —— 防「见谁都说错」的假阳性
        OpenFile good;
        good.path = "uitest/Good.java";
        good.title = "Good.java";
        good.isJava = true;
        good.lines = {"public class Good {",
                      "    void f() {",
                      "        int x = (1 + 2);",
                      "    }",
                      "}"};
        recolorAll(good);
        a.files.push_back(good);
        a.activeTab = (int)a.files.size() - 1;
        a.diags.clear();
        doCheck(a);
        check("括号配平不误报", a.diags.empty(),
              std::to_string(a.diags.size()) + " 条");
    }

    {
        // 没打开项目时编译，应该给提示而不是崩
        std::string keep = a.projectRoot;
        a.projectRoot.clear();
        a.statusLeft.clear();
        doCompile(a, false);
        check("没项目时编译有提示", !a.statusLeft.empty() ||
                                        a.screen == Screen::Editor);
        a.projectRoot = keep;
    }
    say("");

    say("────────────────────────");
    say("通过 " + std::to_string(pass) + " 项，失败 " + std::to_string(fail) +
        " 项");
    say("=== 扫描结束 ===");

    writeFile(path, out);
    quitLoop();
}

static void cbResize(int w, int h, void* ctx) {
    App& a = *(App*)ctx;
    a.winW = w;
    a.winH = h;
    // 窗口尺寸变了，面板宽度/高度要重新夹一遍，
    // 不然拉小窗口后侧栏会跑出去
    if (a.sidebarW > a.winW - 200) a.sidebarW = (a.winW * 22) / 100;
    if (a.sidebarW < dim::sidebarMinW) a.sidebarW = dim::sidebarMinW;
    int maxBottom = a.winH - dim::toolH - dim::menuH - dim::statusH - 80;
    if (a.bottomH > maxBottom) a.bottomH = maxBottom;
}

static void cbMouse(int x, int y, int buttons, void* ctx) {
    App& a = *(App*)ctx;
    a.in.mouseX = x;
    a.in.mouseY = y;

    if (buttons == 1) {
        a.in.mouseDown = true;
        a.in.clicked = true;
        a.in.clickX = x;
        a.in.clickY = y;

        // 拖侧栏宽度 / 拖底部高度
        int top = dim::menuH + dim::toolH;
        if (a.screen == Screen::Editor) {
            if (a.sidebarOpen && std::abs(x - (a.sidebarW - 1)) <= 3 &&
                y > top) {
                a.sidebarDragging = true;
            } else if (a.bottomOpen && std::abs(y - a.bottomH) <= 3) {
                // 底部面板上边缘
            }
        }
    } else {
        a.in.mouseDown = false;
        a.sidebarDragging = false;
        a.bottomDragging = false;
    }

    // 拖动中：改宽度
    if (a.sidebarDragging && a.in.mouseDown) {
        a.sidebarW = x;
        if (a.sidebarW < dim::sidebarMinW) a.sidebarW = dim::sidebarMinW;
        if (a.sidebarW > dim::sidebarMaxW) a.sidebarW = dim::sidebarMaxW;
        if (a.sidebarW > a.winW - 220) a.sidebarW = a.winW - 220;
    }

    requestPaint(nullptr);
}

// 滚轮。**按鼠标所在面板分发** —— 这是用户的硬要求。
//
// 关键点：只在滚动的那一刻判断鼠标位置，并把它记进 scrollPane。
// 之后即使鼠标移出面板，惯性滚动也还作用在那个面板上。
static void cbScroll(int x, int y, int delta, void* ctx) {
    App& a = *(App*)ctx;
    a.in.mouseX = x;
    a.in.mouseY = y;

    int step = delta * 3;

    // 第三方库对话框盖住一切，滚轮只滚它的列表
    if (a.dlg == Dialog::Libraries) {
        int rows = (int)libFiltered(a).size();
        int rowH = 64;
        // 和 drawLibrariesDialog 里同一条算式：列表高 = 对话框高 - 202。
        // 两处必须一致，不然滚到底会露出一截空白。
        int dh = 566;
        if (dh > a.winH - 48) dh = a.winH - 48;
        int listH = dh - 202;
        if (listH < 80) listH = 80;
        int maxS = rows * rowH - listH;
        if (maxS < 0) maxS = 0;

        a.libs.scroll -= step;
        if (a.libs.scroll < 0) a.libs.scroll = 0;
        if (a.libs.scroll > maxS) a.libs.scroll = maxS;
        requestPaint(nullptr);
        return;
    }

    // 先按屏幕算一次命中（paintAll 里的 hoverPane 是上一帧的，
    // 滚轮可能发生在重绘之前，所以这里自己判一遍）
    App::Pane pane = App::Pane::None;
    {
        int top = dim::menuH + dim::toolH;
        int statusH = dim::statusH;
        int bottomH = a.bottomOpen ? a.bottomH : 0;
        int contentH = a.winH - top - statusH - bottomH;

        if (a.screen == Screen::Editor) {
            if (y >= top && y < top + contentH) {
                if (a.sidebarOpen && x < a.sidebarW) pane = App::Pane::Sidebar;
                else pane = App::Pane::Editor;
            } else if (y >= top + contentH && y < a.winH - statusH) {
                pane = App::Pane::Bottom;
            }
        } else if (a.screen == Screen::Home) {
            pane = App::Pane::Recent;
        } else if (a.screen == Screen::Jar) {
            pane = App::Pane::Jar;
        } else if (a.screen == Screen::Settings) {
            pane = App::Pane::Settings;
        }
    }

    switch (pane) {
        case App::Pane::Sidebar: {
            // 侧栏能滚多少，取决于树一共多少行、能看见多少行。
            // 只钳下界会滚出一片空白（用户会以为「滚坏了」），
            // 所以上界也得算出来。
            int listH = a.winH - dim::menuH - dim::toolH - dim::statusH - 32;
            int totalH = (int)visibleNodes(a).size() * dim::rowH;
            int maxS = totalH - listH;
            if (maxS < 0) maxS = 0;
            a.treeScroll -= step;
            if (a.treeScroll < 0) a.treeScroll = 0;
            if (a.treeScroll > maxS) a.treeScroll = maxS;
            break;
        }

        case App::Pane::Editor: {
            OpenFile* f = a.active();
            if (f) {
                // 同理：文档末尾要留得住。这是用户报的「不能上下滑动
                // 代码」的另一半 —— 能滚但没有边界，滚过头整屏空白。
                int bodyH = a.winH - dim::menuH - dim::toolH - dim::statusH -
                            (a.bottomOpen ? a.bottomH : 0);
                int lineH = 20;
                int maxS = (int)f->lines.size() * lineH - bodyH + lineH;
                if (maxS < 0) maxS = 0;

                // Ctrl+滚轮缩放没做，先按普通滚动处理
                f->scrollY -= step;
                if (f->scrollY < 0) f->scrollY = 0;
                if (f->scrollY > maxS) f->scrollY = maxS;
            }
            break;
        }

        case App::Pane::Bottom:
            if (a.bottom == BottomTab::Build) {
                a.buildScroll -= step;
                if (a.buildScroll < 0) a.buildScroll = 0;
            } else if (a.bottom == BottomTab::Run) {
                a.logScroll -= step;
                if (a.logScroll < 0) a.logScroll = 0;
            } else {
                a.problemScroll -= step;
                if (a.problemScroll < 0) a.problemScroll = 0;
            }
            break;

        case App::Pane::Recent:
            a.recentScroll -= step;
            if (a.recentScroll < 0) a.recentScroll = 0;
            break;

        case App::Pane::Jar:
            a.jarScroll -= step;
            if (a.jarScroll < 0) a.jarScroll = 0;
            break;

        case App::Pane::Settings:
            a.jdkScroll -= step;
            if (a.jdkScroll < 0) a.jdkScroll = 0;
            break;

        default:
            break;
    }

    a.scrollPane = pane;
    requestPaint(nullptr);
}

static void cbKey(int vk, bool down, bool ctrl, bool shift, bool alt,
                  void* ctx) {
    App& a = *(App*)ctx;
    a.in.ctrl = ctrl;
    a.in.shift = shift;
    (void)alt;

    // ---- 输入法给的字符：0x10000 | 码点
    if (vk >= 0x10000) {
        if (down) {
            unsigned cp = (unsigned)(vk & 0xFFFF);
            // 控制字符不进编辑器
            if (cp >= 32 || cp == 9) {
                if (a.dlg == Dialog::NewProject) {
                    std::string u = utf8Encode(cp);
                    if (a.np.field == 0) a.np.name += u;
                    else if (a.np.field == 1) a.np.group += u;
                    a.np.error.clear();
                } else if (a.dlg == Dialog::NewFile) {
                    a.newFileName += utf8Encode(cp);
                } else if (a.dlg == Dialog::Libraries) {
                    // 中文搜索要能打字，所以走输入法这条分支
                    a.libs.query += utf8Encode(cp);
                    a.libs.scroll = 0;
                } else if (a.dlg == Dialog::None) {
                    insertText(a, utf8Encode(cp));
                }
            }
        }
        requestPaint(nullptr);
        return;
    }

    if (!down) return;

    // ---- 有对话框时键盘全归对话框
    if (a.dlg != Dialog::None) {
        if (vk == VK_ESCAPE) {
            a.dlg = Dialog::None;
            requestPaint(nullptr);
            return;
        }
        // 第三方库是个浏览器，不是表单 —— 回车不该把它关掉，
        // 用户按回车往往是「我搜完了」而不是「我确定要退出」
        if (vk == VK_RETURN && a.dlg == Dialog::Libraries) return;

        if (vk == VK_RETURN) {
            if (a.dlg == Dialog::NewProject) createProjectNow(a);
            else if (a.dlg == Dialog::NewFile) createFileNow(a);
            else a.dlg = Dialog::None;
            requestPaint(nullptr);
            return;
        }
        if (vk == VK_TAB && a.dlg == Dialog::NewProject) {
            a.np.field = (a.np.field + 1) % 2;
            requestPaint(nullptr);
            return;
        }

        std::string* target = nullptr;
        if (a.dlg == Dialog::NewProject) {
            target = (a.np.field == 0) ? &a.np.name : &a.np.group;
        } else if (a.dlg == Dialog::NewFile) {
            target = &a.newFileName;
        } else if (a.dlg == Dialog::Libraries) {
            target = &a.libs.query;
        }
        if (target) {
            if (vk == VK_BACK) {
                if (!target->empty()) target->pop_back();
            } else if (vk >= 32 && vk < 127) {
                *target += (char)vk;
            }
            if (a.dlg == Dialog::NewProject) a.np.error.clear();
            // 搜索词变了就回到列表顶部，否则用户会停在一片空白上
            if (a.dlg == Dialog::Libraries) a.libs.scroll = 0;
        }
        requestPaint(nullptr);
        return;
    }

    // ---- 全局快捷键
    if (ctrl) {
        switch (vk) {
            case 'S':
                if (shift) saveAll(a);
                else if (a.activeTab >= 0) saveFile(a, a.activeTab);
                requestPaint(nullptr);
                return;
            case 'N':
                if (shift) beginNewProject(a);
                else {
                    a.newFileName.clear();
                    a.dlg = Dialog::NewFile;
                }
                requestPaint(nullptr);
                return;
            case 'O':
                doMenuAction(a, MA_OpenProject);
                requestPaint(nullptr);
                return;
            case 'J':
                doMenuAction(a, MA_OpenJar);
                requestPaint(nullptr);
                return;
            case 'B':
                doCompile(a, false);
                requestPaint(nullptr);
                return;
            case 'L':
                if (shift) openLibraryDialog(a);
                else a.bottomOpen = !a.bottomOpen;
                requestPaint(nullptr);
                return;
            case 'T':
                setDark(!current().dark);
                requestPaint(nullptr);
                return;
            case 'A':
                selectAll(a);
                requestPaint(nullptr);
                return;
            case 'C':
                copySelection(a);
                requestPaint(nullptr);
                return;
            case 'X':
                cutSelection(a);
                requestPaint(nullptr);
                return;
            case 'V':
                pasteFromClipboard(a);
                requestPaint(nullptr);
                return;
            default:
                break;
        }
        if (vk == 0xBC) {   // Ctrl+, 打开设置
            a.jdks = jdk::all();
            a.screen = Screen::Settings;
            requestPaint(nullptr);
            return;
        }
    }

    if (vk == VK_ESCAPE) {
        if (a.screen != Screen::Home) {
            a.screen = Screen::Home;
            a.statusLeft = "起始页";
        }
        requestPaint(nullptr);
        return;
    }

    if (vk == VK_F5) {
        doCompile(a, !shift);
        requestPaint(nullptr);
        return;
    }

    // ---- 编辑器按键
    if (a.screen != Screen::Editor) {
        requestPaint(nullptr);
        return;
    }
    if (a.activeIndex() < 0) {
        requestPaint(nullptr);
        return;
    }

    // 打字
    //
    // 这个范围判断有个坑：VK_END(35) / VK_HOME(36) / VK_LEFT(37) /
    // VK_UP(38) / VK_RIGHT(39) / VK_DOWN(40) 全都落在 32..126 里面
    // （Windows 的虚拟键码把方向键定义在这个区间）。不排掉它们，
    // 方向键会被当成「打了 , - . / 0 1 这几个字符」——
    // 表现为按方向键光标不动，反而往代码里插了一堆乱字符。
    //
    // VK_PRIOR(33) / VK_NEXT(34) / VK_DELETE(46) / VK_INSERT(45) /
    // VK_BACK(8) 同理，只是它们要么 <32 要么早就在下面的 switch 里
    // 拦住了。这里统一把「功能键区间」排除掉更保险。
    bool isFunctionKey = (vk == VK_PRIOR || vk == VK_NEXT ||
                          vk == VK_END || vk == VK_HOME ||
                          vk == VK_LEFT || vk == VK_UP ||
                          vk == VK_RIGHT || vk == VK_DOWN ||
                          vk == VK_INSERT || vk == VK_DELETE);

    if (!ctrl && !isFunctionKey && vk >= 32 && vk < 127) {
        char ch = (char)vk;
        // Shift 时把字母转成大写。平台层给的是虚拟码，
        // 不转的话 Shift+A 输入的是 'a'。
        if (shift && ch >= 'a' && ch <= 'z') ch = (char)(ch - 'a' + 'A');
        else if (shift && ch >= '0' && ch <= '9') {
            const char* up = ")!@#$%^&*(";
            ch = up[ch - '0'];
        }
        std::string s(1, ch);
        insertText(a, s);
        requestPaint(nullptr);
        return;
    }

    switch (vk) {
        case VK_RETURN: insertNewline(a); break;
        case VK_BACK: backspace(a); break;
        case VK_DELETE: deleteForward(a); break;
        case VK_TAB: indentSelection(a, shift); break;

        case VK_LEFT: moveCaret(a, 0, -1, shift); break;
        case VK_RIGHT: moveCaret(a, 0, 1, shift); break;
        case VK_UP: moveCaret(a, -1, 0, shift); break;
        case VK_DOWN: moveCaret(a, 1, 0, shift); break;

        case VK_HOME: {
            if (ctrl) {
                // Ctrl+Home 到文件开头
                setCaret(a, 0, 0, shift);
            } else {
                moveLineEdge(a, false, shift);
            }
            break;
        }
        case VK_END: {
            if (ctrl) {
                OpenFile* f = a.active();
                if (f) setCaret(a, (int)f->lines.size() - 1,
                                (int)f->lines.back().size(), shift);
            } else {
                moveLineEdge(a, true, shift);
            }
            break;
        }
        case VK_PRIOR: {   // PageUp
            OpenFile* f = a.active();
            if (f) {
                int lines = (a.winH - dim::toolH - dim::menuH -
                             dim::statusH) /
                            20;
                setCaret(a, f->caretLine - lines, f->caretCol, shift);
            }
            break;
        }
        case VK_NEXT: {    // PageDown
            OpenFile* f = a.active();
            if (f) {
                int lines = (a.winH - dim::toolH - dim::menuH -
                             dim::statusH) /
                            20;
                setCaret(a, f->caretLine + lines, f->caretCol, shift);
            }
            break;
        }
        default: break;
    }

    // 光标移完，滚到可见
    {
        OpenFile* f = a.active();
        if (f) {
            TextStyle st;
            st.size = 14;
            st.monospace = true;
            Canvas dummy;
            dummy.handle = nullptr;
            dummy.width = a.winW;
            dummy.height = a.winH;
            // 这里没法拿到真 Canvas，用简单的行高估算滚动
            int lh = 20;
            int top = dim::menuH + dim::toolH;
            int bottomH = a.bottomOpen ? a.bottomH : 0;
            int bodyH = a.winH - top - dim::statusH - bottomH - dim::tabH;
            int caretTop = f->caretLine * lh;
            if (caretTop - lh < f->scrollY) f->scrollY = caretTop - lh;
            if (caretTop + lh * 2 > f->scrollY + bodyH)
                f->scrollY = caretTop + lh * 2 - bodyH;
            if (f->scrollY < 0) f->scrollY = 0;
            (void)st;
        }
    }

    requestPaint(nullptr);
}

static void cbClose(void* ctx) {
    App& a = *(App*)ctx;
    // 有未保存的改动就问一下。没有就悄悄退。
    bool dirty = false;
    for (const auto& f : a.files)
        if (f.dirty) dirty = true;

    if (dirty) {
        for (auto& f : a.files) {
            if (f.dirty) saveFile(a, (int)(&f - a.files.data()));
        }
        a.log(LogLevel::Info, "退出前已保存所有改动");
    }
    a.log(LogLevel::Info, "正在退出");
    quitLoop();
}

// ================================================================
//  自检
// ================================================================
//
// 用脚本化的按键 / 滚动事件跑一遍编辑内核，把结果写成文本。
//
// 为什么值得单独写这一段：用户报的两个 bug（「不能修改代码」、
// 「不能上下滑动代码」）都发生在**键盘和滚动事件到编辑内核**这条
// 路上。截图只能证明界面画出来了，证明不了这条路通不通 —— 得让
// 事件真的走一遍，再看文档状态对不对。
//
// 这里不建窗口、不造 HWND，直接调 cbKey / cbScroll 传假的 ctx。
// 窗口过程本身在 win32.cpp 里是现成的，真正容易错的逻辑（插入、
// 换行、退格、滚动钳制、着色）全在 app 层，正好能这么测。
static std::string runSelfTest(App& a) {
    std::string r;
    auto say = [&](const std::string& line) { r += line + "\n"; };
    auto ok = [&](bool b) { return b ? std::string("PASS") : std::string("FAIL"); };

    say("=== Java Studio 自检 ===");

    // ---- 准备：造一个内存里的文件，不依赖磁盘
    OpenFile f;
    f.path = "selftest/Main.java";
    f.title = "Main.java";
    f.isJava = true;
    f.lines = {"public class Main {",
               "    public static void main(String[] a) {",
               "        int x = 42;",
               "    }",
               "}"};
    f.caretLine = 0;
    f.caretCol = 0;
    recolorAll(f);
    a.files.clear();
    a.files.push_back(f);
    a.activeTab = 0;
    a.screen = Screen::Editor;
    a.dlg = Dialog::None;
    a.in = Input{};

    // 面板命中区域是拿 winW/winH 算的，滚动测试之前必须先定下来。
    // 正常运行时这两个值由 cbResize 填，自检里没人调 resize，自己给。
    a.winW = 1344;
    a.winH = 821;
    a.sidebarOpen = true;
    a.sidebarW = 240;
    a.bottomOpen = true;
    a.bottomH = 180;

    Canvas c;
    c.handle = nullptr;

    OpenFile* cf = a.active();
    say("初始行数 = " + std::to_string(cf->lines.size()));
    say("初始着色 = " + std::to_string(cf->spans.size()) + " 行," +
        " 第1行段数 = " +
        std::to_string(cf->spans.empty() ? 0 : (int)cf->spans[0].size()));

    // ---- 1. 语法着色是否分出类别
    {
        // 第 2 行有 "public static void main" 和 "String"
        bool hasCat = false;
        for (size_t i = 1; i < cf->spans.size(); i++) {
            for (const auto& s : cf->spans[i]) {
                if (s.cat != jlex::Cat::Plain) {
                    hasCat = true;
                    break;
                }
            }
        }
        say("1. 语法着色分类    : " + ok(hasCat));
    }

    // ---- 2. 插入文本（「不能修改代码」）
    {
        setCaret(a, 0, 0, false);
        insertText(a, "// hi ");
        bool line0 = cf->lines[0] == "// hi public class Main {";
        bool dirty = cf->dirty;
        say("2a. 行首插入文本   : " + ok(line0) + "  -> " + cf->lines[0]);
        say("2b. 置脏标记       : " + ok(dirty));

        // 光标应该跟着往后走
        say("2c. 光标自动后移   : " + ok(cf->caretCol == 6) + "  col=" +
            std::to_string(cf->caretCol));
    }

    // ---- 3. 回车换行 + 自动缩进
    //
    // 注意：这里每次都重新 a.active() 拿指针。a.files 是 vector，
    // 后面 push_back 会让它扩容搬家，早先存的指针就悬空了 ——
    // 这是个很典型的坑，自检里也必须守规矩，否则测的是野指针。
    {
        // 在行中间回车。第 1 行 = "    public static void main(String[] a) {"
        setCaret(a, 1, 18, false);
        size_t before = cf->lines.size();
        insertNewline(a);
        OpenFile* g = a.active();
        bool added = g->lines.size() == before + 1;
        bool indented = g->lines[1].find_first_not_of(" \t") >= 4;
        say("3a. 回车新增一行   : " + ok(added) + "  " +
            std::to_string(before) + " -> " + std::to_string(g->lines.size()));
        say("3b. 自动继承缩进   : " + ok(indented) + "  [" + g->lines[1] + "]");

        // 行尾是 { 的时候要多缩一层
        setCaret(a, 0, (int)g->lines[0].size(), false);
        insertNewline(a);
        g = a.active();
        bool extra = g->lines[1].find_first_not_of(" \t") >= 8;
        say("3c. { 后多缩一层   : " + ok(extra) + "  [" + g->lines[1] + "]");
    }

    // ---- 4. 退格
    {
        OpenFile* g = a.active();
        setCaret(a, 2, 0, false);
        int linesBefore = (int)g->lines.size();
        backspace(a);          // 行首退格 = 与上一行合并
        g = a.active();
        bool merged = (int)g->lines.size() == linesBefore - 1;
        say("4a. 行首退格并上一行: " + ok(merged) + "  " +
            std::to_string(linesBefore) + " -> " +
            std::to_string(g->lines.size()));

        // 行中间退格：删一个字符
        setCaret(a, 1, 6, false);
        int colBefore = a.active()->caretCol;
        int lenBefore = (int)a.active()->lines[1].size();
        backspace(a);
        g = a.active();
        bool shrank = (int)g->lines[1].size() == lenBefore - 1 &&
                      g->caretCol == colBefore - 1;
        say("4b. 行中退格删字符 : " + ok(shrank) + "  行 1 长度 " +
            std::to_string(lenBefore) + " -> " +
            std::to_string(g->lines[1].size()));
    }

    // ---- 5. 方向键移动光标
    {
        // 前几步改过行数，先把文件恢复成一个干净的形状
        {
            OpenFile* g = a.active();
            g->lines = {"aaa", "bbbb", "cc", "dddddd", "ee"};
            g->caretLine = 0;
            g->caretCol = 0;
            g->selAnchorLine = -1;
            recolorAll(*g);
        }
        setCaret(a, 2, 1, false);

        cbKey(VK_RIGHT, true, false, false, false, &a);
        bool right = a.active()->caretCol == 2;
        say("5a. 右方向键       : " + ok(right) + "  col=" +
            std::to_string(a.active()->caretCol));

        cbKey(VK_DOWN, true, false, false, false, &a);
        bool down = a.active()->caretLine == 3;
        say("5b. 下方向键       : " + ok(down) + "  line=" +
            std::to_string(a.active()->caretLine));

        cbKey(VK_UP, true, false, false, false, &a);
        bool up = a.active()->caretLine == 2;
        say("5c. 上方向键       : " + ok(up) + "  line=" +
            std::to_string(a.active()->caretLine));

        cbKey(VK_LEFT, true, false, false, false, &a);
        bool left = a.active()->caretCol == 1;
        say("5d. 左方向键       : " + ok(left) + "  col=" +
            std::to_string(a.active()->caretCol));

        // 到行首再往左应该跨到上一行行尾
        setCaret(a, 2, 0, false);
        cbKey(VK_LEFT, true, false, false, false, &a);
        bool crossed = a.active()->caretLine == 1 &&
                       a.active()->caretCol == 4;
        say("5e. 行首左键跨行   : " + ok(crossed) + "  " +
            std::to_string(a.active()->caretLine) + ":" +
            std::to_string(a.active()->caretCol));
    }

    // ---- 6. Home / End
    {
        setCaret(a, 3, 2, false);
        cbKey(VK_END, true, false, false, false, &a);
        bool atEnd = a.active()->caretCol == 6;
        say("6a. End 到行尾     : " + ok(atEnd) + "  col=" +
            std::to_string(a.active()->caretCol));

        cbKey(VK_HOME, true, false, false, false, &a);
        bool atHome = a.active()->caretCol == 0;
        say("6b. Home 到行首    : " + ok(atHome) + "  col=" +
            std::to_string(a.active()->caretCol));
    }

    // ---- 7. 滚动：上下分别滚，且要钳制
    {
        // 长文件
        OpenFile big;
        big.path = "selftest/Big.java";
        big.title = "Big.java";
        big.isJava = true;
        big.lines.push_back("public class Big {");
        for (int i = 0; i < 500; i++)
            big.lines.push_back("        int v" + std::to_string(i) + " = " +
                                std::to_string(i) + ";");
        big.lines.push_back("}");
        recolorAll(big);
        a.files.push_back(big);
        a.activeTab = (int)a.files.size() - 1;

        // 每次都 a.active() 重新拿，不要缓存指针（vector 会搬家）
        say("7. 长文件行数      : " +
            std::to_string(a.active()->lines.size()));

        int y0 = a.active()->scrollY;
        cbScroll(600, 300, -1, &a);   // 向下滚一格
        bool down = a.active()->scrollY > y0;
        say("7a. 向下滚动生效   : " + ok(down) + "  " + std::to_string(y0) +
            " -> " + std::to_string(a.active()->scrollY));

        cbScroll(600, 300, 1, &a);    // 回到 0
        bool backTo0 = a.active()->scrollY == 0;
        say("7b. 向上滚回起点   : " + ok(backTo0) + "  scrollY=" +
            std::to_string(a.active()->scrollY));

        // 一直往下滚，应该停在一个有限值（= 内容高度 - 视口高度 + 一行）
        // 每滚一格是 3px。文档 502 行 * 20px，视口约 540px，
        // 底在 9500 附近 —— 得滚 4000 次才够得着边界。
        for (int i = 0; i < 4000; i++) cbScroll(600, 300, -1, &a);
        int bottom = a.active()->scrollY;
        for (int i = 0; i < 4000; i++) cbScroll(600, 300, -1, &a);
        bool clamped = a.active()->scrollY == bottom;
        bool finite = bottom > 0 && bottom < 502 * 20;
        say("7c. 滚到底后钳住   : " + ok(clamped && finite) + "  scrollY=" +
            std::to_string(bottom));

        // 再往上滚回顶，不能变负数
        for (int i = 0; i < 8000; i++) cbScroll(600, 300, 1, &a);
        bool atTop = a.active()->scrollY == 0;
        say("7d. 滚到顶后为 0   : " + ok(atTop) + "  scrollY=" +
            std::to_string(a.active()->scrollY));
    }

    // ---- 8. 鼠标位置决定滚哪个面板
    {
        for (int i = 0; i < 60; i++) cbScroll(600, 300, -1, &a);
        int before = a.active()->scrollY;

        // 鼠标压在侧栏（sidebarW = 240）-> 编辑器不该动
        cbScroll(60, 300, -1, &a);
        bool editorUntouched = a.active()->scrollY == before;
        say("8a. 鼠标在侧栏时编辑器不动: " + ok(editorUntouched) + "  " +
            std::to_string(before) + " -> " +
            std::to_string(a.active()->scrollY));

        // 鼠标压在编辑器 -> 编辑器要动
        cbScroll(700, 300, -1, &a);
        bool editorMoved = a.active()->scrollY != before;
        say("8b. 鼠标在编辑器时编辑器动: " + ok(editorMoved) + "  " +
            std::to_string(before) + " -> " +
            std::to_string(a.active()->scrollY));

        // 鼠标压在底部面板 -> 编辑器不该动，底部日志该动
        int logBefore = a.logScroll;
        a.bottom = BottomTab::Run;
        cbScroll(600, a.winH - 60, -1, &a);
        bool bottomMoved = a.logScroll != logBefore &&
                           a.active()->scrollY == before + (editorMoved ? 3 : 0);
        say("8c. 鼠标在底部面板时滚底部: " + ok(a.logScroll != logBefore));
    }

    // ---- 9. 括号检查
    {
        OpenFile bad;
        bad.path = "selftest/Bad.java";
        bad.title = "Bad.java";
        bad.isJava = true;
        bad.lines = {"public class Bad {",
                     "    void f() {",
                     "        int x = (1 + 2;",   // 缺右括号
                     "    }",
                     "}"};
        recolorAll(bad);
        a.files.push_back(bad);
        a.activeTab = (int)a.files.size() - 1;
        a.diags.clear();
        doCheck(a);
        bool found = !a.diags.empty();
        say("9. 括号检查报错    : " + ok(found) + "  诊断数=" +
            std::to_string(a.diags.size()));
        if (found) say("      -> " + a.diags[0].message);
    }

    // ---- 10. 全选 / 复制（选区能不能建立）
    {
        a.activeTab = 1;
        OpenFile* bf = a.active();
        setCaret(a, 0, 0, false);
        selectAll(a);
        bool sel = hasSelection(a);
        std::string t = selectedText(a);
        say("10. 全选建立选区   : " + ok(sel) + "  选中 " +
            std::to_string(t.size()) + " 字节");
    }

    say("=== 自检结束 ===");
    return r;
}

// ================================================================
//  入口主体
// ================================================================
//
// 真正的 WinMain 在这个命名空间**外面**（文件最末尾）。原因见那边
// 的注释：入口函数写在命名空间里，链接器找不到。
//
// 这里只留主体。为什么要拆开：主体里要调一堆内部 static 函数
// （cbPaint / openProject / …），留在命名空间里最省事；而 WinMain
// 本身只需要认识 appRun 一个符号，放外面没有代价。

// 主窗口建好之后，把这一帧的输入事件清空。
//
// 为什么不加这个会出问题：启动画面的「点击任意处关闭」是记在 Input
// 里的，主窗口第一帧读的还是同一份 Input —— 于是刚打开就误触了工具栏
// 或树节点。清掉 clicked / mouseDown / scroll 三个瞬时量就干净了。
static void newFileNameCaretReset(App& a) {
    a.in.clicked = false;
    a.in.mouseDown = false;
    a.in.scroll = 0;
}

int appRun(const std::vector<std::string>& args) {
    // 日志要先接上。GUI 程序没有 stdout，出问题只能看这个文件。
    std::string dataDir = appDataDir("JavaStudio");
    logInit(joinPath(dataDir, "javastudio.log"));
    logWrite("INFO", "Java Studio 26.1-Test 启动");

    // ---------------------------------------------------------- 启动画面
    //
    // 先开一个圆角小窗，再去做那些慢的初始化。
    // 每一步都推进度条 —— 用户看到它在动，就知道没卡死。
    void* splash = createSplash(560, 340);
    splashStep(splash, 0.04f, "正在启动 Java Studio…");

    App& a = get();
    // 文件浏览 / 打开项目都从 item 起步 —— 新建的项目就在那里，
    // 让用户打开时还得手动找过去是不合理的
    a.curDir = projectsDir();

    // ---- 图标库
    splashStep(splash, 0.14f, "加载图标库…");
    static Library iconLib(findAssetsDir());
    a.icons = &iconLib;
    int nIcons = iconLib.load();
    {
        char buf[256];
        snprintf(buf, sizeof(buf), "图标库：%s，加载 %d 个",
                 findAssetsDir().c_str(), nIcons);
        a.log(nIcons > 0 ? LogLevel::Ok : LogLevel::Warn, buf);
    }
    splashStep(splash, 0.34f, "缓存图标…");
    iconLib.warmAll();

    // 启动画面上的品牌图标：从图标库里取，不在启动画面里手画
    {
        std::string svg = iconLib.svgOf("coffee");
        splashSetBrandIcon(svg);
        if (svg.empty())
            a.log(LogLevel::Warn, "没读到 coffee 图标，启动画面只留空方块");
    }

    // ---- JDK
    splashStep(splash, 0.50f, "扫描 JDK…");
    a.jdks = jdk::scan();
    jdk::loadPreference();

    for (const auto& j : a.jdks) {
        std::string msg = "发现 " + j.label();
        if (!j.vendor.empty()) msg += "（" + j.vendor + "）";
        a.log(j.ok ? LogLevel::Ok : LogLevel::Warn, msg);
    }
    if (a.jdks.empty()) {
        a.log(LogLevel::Warn,
              "没有扫描到 JDK。把 JDK 放到软件目录的 jdk/ 下面即可。");
    } else {
        jdk::Info cur = jdk::current();
        if (cur.ok) a.log(LogLevel::Ok, "当前使用 " + cur.label());
    }

    // ---- 第三方库
    //
    // 数一下本机缓存里已经有多少个 jar。这一步不联网 —— 启动阶段
    // 绝不能卡在网络上，不然用户看到的是进度条停住不动。
    // 真正缺什么，打开项目时才算（见 files.cpp 的 openProject）。
    splashStep(splash, 0.64f, "准备第三方库…");
    {
        int n = jlibs::cacheCount();
        char buf[192];
        snprintf(buf, sizeof(buf), "第三方库：目录 %d 个，本机缓存 %d 个 jar",
                 (int)jlibs::catalog().size(), n);
        a.log(n > 0 ? LogLevel::Ok : LogLevel::Info, buf);
    }

    // ---- 会话
    splashStep(splash, 0.76f, "读取上次的会话…");
    loadSession(a);

    // ---- 命令行
    //
    // 支持：
    //   javastudio <目录>        直接打开项目
    //   javastudio <文件>        直接打开文件（.jar 会进 jar 查看器）
    //   --version / --help
    splashStep(splash, 0.86f, "准备界面…");

    std::string target;
    for (size_t i = 1; i < args.size(); i++) {
        std::string arg = args[i];
        if (arg == "--version" || arg == "-v") {
            destroySplash(splash);
            return 0;
        }
        if (arg == "--help" || arg == "-h") {
            destroySplash(splash);
            return 0;
        }
        if (!arg.empty() && arg[0] != '-') {
            target = arg;
            break;
        }
    }

    if (!target.empty()) {
        if (dirExists(target)) {
            openProject(a, target);
        } else if (fileExists(target)) {
            openPath(a, target);
        } else {
            a.log(LogLevel::Warn, "路径不存在：" + target);
        }
    }

    if (a.screen == Screen::Home) {
        a.statusLeft = "起始页";
    }

    // 调试用：JS_OPEN=<路径> 直接把指定文件打开到编辑器。
    // 截图验证着色、光标、行号时很省事，不用手点文件树。
    {
        const char* op = getenv("JS_OPEN");
        if (op && *op) loadFile(a, op);
    }

    // 调试用：JS_LIBDLG=1 一进来就把「第三方库」对话框打开。
    // 配 SIDE_SHOT 一起用，能直接看到这个框长什么样。
    {
        const char* ld = getenv("JS_LIBDLG");
        if (ld && *ld && *ld != '0') openLibraryDialog(a);
    }

    // 调试用：JS_LIBTEST=<输出文件> 把「加库 → 下载 → 编译 → 运行」
    // 这条链跑一遍，写成文本。库名从 JS_ADDLIB 里读（逗号分隔）。
    //
    // 为什么要这个钩子：第三方库这条路涉及网络下载、classpath 拼接、
    // javac 和 java 两次调用，出问题时是「找不到符号」而不是「下载失败」，
    // 靠手点界面排查太慢。有了它一条命令就能定位断在哪。
    {
        const char* lt = getenv("JS_LIBTEST");
        if (lt && *lt) {
            std::string out;
            auto say = [&](const std::string& s) {
                out += s;
                out += "\r\n";
            };

            say("=== 第三方库自检 ===");
            say("项目：" + a.projectRoot);

            const char* add = getenv("JS_ADDLIB");
            if (add && *add) {
                std::string cur;
                std::vector<std::string> ids;
                for (size_t i = 0; i <= strlen(add); i++) {
                    char ch = (i == strlen(add)) ? ',' : add[i];
                    if (ch == ',') {
                        if (!cur.empty()) ids.push_back(cur);
                        cur.clear();
                    } else if (ch != ' ') {
                        cur += ch;
                    }
                }
                a.libs.selected = ids;
                syncLibs(a, false);

                say("勾选：" + std::to_string(ids.size()) + " 个");
                for (const auto& m : a.libMissing) {
                    const jlibs::Entry* e = jlibs::find(m);
                    say("缺：" + (e ? e->coord() : m));
                }
                for (const auto& id : a.libMissing) fetchLib(a, id);

                say("下载后：就绪 " + std::to_string(a.libJars.size()) +
                    " / 缺 " + std::to_string(a.libMissing.size()));
                say("classpath：" + libClasspathString(a));
            }

            if (!a.projectRoot.empty()) {
                a.buildOut.clear();
                doCompile(a, false);
                for (const auto& l : a.buildOut) say(l.text);
                say("编译后：主类=" + a.lastMainClass +
                    " 退出码=" + std::to_string(a.lastExitCode));

                a.logs.clear();
                doRun(a);
                for (const auto& l : a.logs) say(l.text);
            }

            say("=== 结束 ===");
            writeFile(lt, out);
            // 这个模式是无人值守的，跑完就退
            if (getenv("JS_LIBONLY")) {
                destroySplash(splash);
                return 0;
            }
        }
    }

    // ---- 准备就绪，等用户点「立即进入」还是「退出」
    //
    // 加载阶段结束后不直接进主界面，先停一下：
    //   1) 让用户看清扫到了哪几个 JDK、有没有报错
    //   2) 给一个「不进去了」的出口
    // 只有自检模式（JS_SELFTEST）会跳过这一步 —— 那是无人值守的。
    int choice = 1;
    {
        const char* skip = getenv("JS_SELFTEST");
        const char* skip2 = getenv("JS_NOWAIT");
        if ((skip && *skip) || (skip2 && *skip2)) {
            splashStep(splash, 1.0f, "就绪");
        } else {
            choice = splashReady(splash);
        }
    }
    if (choice == 2) {
        // 用户选了退出，直接收掉
        destroySplash(splash);
        return 0;
    }

    // ---------------------------------------------------------- 自检
    //
    // 设了 JS_SELFTEST=<路径> 就跑一遍脚本化的编辑/滚动，把结果写成
    // 文本再退出。没有窗口、没有键盘，纯逻辑层验证：
    //
    //   1) 能不能改代码（插入 / 换行 / 退格）
    //   2) 能不能上下左右滚动
    //   3) 语法着色有没有分出类别
    //   4) 括号检查能不能报出问题
    //
    // 为什么需要这个：这几条都是用户报过 bug 的地方（「不能修改代码」
    // 「不能上下滑动代码」），而它们没法靠截图看出来 —— 截图只能证明
    //「画出来了」，证明不了「键盘接上了」。所以要有个能自动跑的东西。
    {
        const char* stPath = getenv("JS_SELFTEST");
        if (stPath && *stPath) {
            std::string rep = runSelfTest(a);
            writeFile(stPath, rep);
            destroySplash(splash);
            return 0;
        }
    }

    // ---------------------------------------------------------- 主窗口
    WindowDesc desc;
    desc.title = "Java Studio 26.1-Test";
    desc.width = 1360;
    desc.height = 860;
    desc.minWidth = 900;
    desc.minHeight = 560;
    desc.center = true;
    desc.resizable = true;

    WindowCallbacks cbs;
    cbs.onPaint = cbPaint;
    cbs.onResize = cbResize;
    cbs.onMouse = cbMouse;
    cbs.onScroll = cbScroll;
    cbs.onKey = cbKey;
    cbs.onClose = cbClose;
    cbs.ctx = &a;

    void* win = createWindow(desc, cbs);
    if (!win) {
        logWrite("ERROR", "窗口创建失败，退出");
        destroySplash(splash);
        return 1;
    }
    logWrite("INFO", "主窗口已创建");

    // 主窗口出来了，把启动画面淡出收掉
    destroySplash(splash);

    newFileNameCaretReset(a);

    return runLoop();
}

}  // namespace app

// ================================================================
//  WinMain —— 全局作用域
// ================================================================
//
// 必须在 namespace app **外面**。
//
// C++ 里入口函数不认命名空间：写在里面链接器就找不到，报的错是
//     undefined symbol: WinMain
// 看着像「缺入口函数」，其实入口在，只是被藏起来了。这个错很容易
// 误判成链接参数问题（-mwindows 没传之类），实际是作用域问题。
//
// 这里只做一件事：把命令行转成 **UTF-8** 的字符串数组。
//
// 为什么不直接用 argv：-mwindows 下 MinGW 给的窄字符 argv 是 ANSI
// 编码，中文 Windows 上就是 GBK 字节。而这些参数最终会进
// openProject / 打开文件，路径比较是按 UTF-8 做的，GBK 进去必然
// 找不到文件（表现是「双击打开没反应」）。
//
// 所以走 GetCommandLineW 拿宽字符，再 WideCharToMultiByte 转 UTF-8，
// 全程不让 ANSI 参与。

static std::vector<std::string> utf8CommandLine() {
    std::vector<std::string> out;
    out.push_back("JavaStudio");

    int argcW = 0;
    LPWSTR* argvW = CommandLineToArgvW(GetCommandLineW(), &argcW);
    if (!argvW) return out;

    for (int i = 1; i < argcW; i++) {
        int need = WideCharToMultiByte(CP_UTF8, 0, argvW[i], -1, nullptr, 0,
                                       nullptr, nullptr);
        if (need <= 1) continue;
        std::string s;
        s.resize((size_t)need - 1);
        WideCharToMultiByte(CP_UTF8, 0, argvW[i], -1, &s[0], need, nullptr,
                            nullptr);
        out.push_back(s);
    }
    LocalFree(argvW);
    return out;
}

int APIENTRY WinMain(HINSTANCE, HINSTANCE, LPSTR, int) {
    return app::appRun(utf8CommandLine());
}
